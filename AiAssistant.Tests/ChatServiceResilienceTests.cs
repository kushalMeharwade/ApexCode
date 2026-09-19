#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Core.Models;
using AiAssistant.Core.Pipeline;
using AiAssistant.Core.Services;
using AiAssistant.Engine.Services;
using AiAssistant.Llm.Services;
using AiAssistant.Storage.Repositories;
using AiAssistant.Tools.Functions;
using AiAssistant.Tools.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ApexCode.Services;
using Xunit;
using Message = Microsoft.Extensions.AI.ChatMessage;
using UiMessage = AiAssistant.Core.Models.ChatMessage;

namespace AiAssistant.Tests;

public class ChatServiceResilienceTests
{
    private static ChatResponseUpdate Text(string text) => new(ChatRole.Assistant, text);
    private static ChatResponseUpdate Call(string name, string id = "c", params (string Key, object? Value)[] args) =>
        new() { Role = ChatRole.Assistant, Contents = new List<AIContent>
        { new FunctionCallContent(id, name, args.ToDictionary(a => a.Key, a => a.Value)) } };

    private sealed class FakeClient : IChatClient
    {
        public readonly List<List<Message>> Requests = new();
        public Func<int, IEnumerable<object>> Script = _ => new object[] { Text("done") };
        public object? GetService(Type type, object? key = null) => null;
        public void Dispose() { }
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<Message> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var updates = new List<ChatResponseUpdate>();
            await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken)) updates.Add(update);
            return updates.ToChatResponse();
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Message> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToList());
            foreach (var item in Script(Requests.Count))
            {
                if (item is Exception error) throw error;
                if (item is TimeSpan delay) { await Task.Delay(delay, cancellationToken); continue; }
                yield return (ChatResponseUpdate)item;
            }
        }
    }

    private sealed class Harness
    {
        public readonly FakeClient Client = new();
        public readonly List<ConversationTurn> History = new();
        public readonly List<string> Errors = new();
        public readonly List<UiMessage> Completed = new();
        public readonly ChatService Service;
        public readonly Mock<IPlanService> Plans = new();


        public Harness(params AIFunction[] tools) : this(null, tools) { }

        public Harness(CommandSessionService? commands, params AIFunction[] tools)
        {
            var session = new Mock<ISessionManager>();
            session.Setup(s => s.GetConversationHistoryAsync(It.IsAny<string>())).ReturnsAsync(() => History.ToList());
            session.Setup(s => s.AddMessageAsync(It.IsAny<string>(), It.IsAny<ConversationTurn>()))
                .Callback<string, ConversationTurn>((_, turn) => History.Add(turn)).Returns(Task.CompletedTask);
            var registry = new Mock<IToolRegistry>();
            registry.Setup(r => r.GetTools()).Returns(tools);
            var factory = new Mock<IChatClientFactory>();
            factory.Setup(f => f.CreateClient(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns(Client);
            var pipeline = new Mock<IChatRequestPipeline>();
            pipeline.Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<UiMessage>>(), It.IsAny<string>(),
                It.IsAny<ChatOptions>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string provider, IReadOnlyList<UiMessage> history, string user, ChatOptions options, string mode,
                    int budget, IDictionary<string, object?> metadata, CancellationToken ct) =>
                    new PreparedChatRequest(new ContextStateBuilder().Build(), "test system", history, user, new object()));
            Service = new ChatService(session.Object, factory.Object, registry.Object, Mock.Of<IContextCompactor>(),
                pipeline.Object, Mock.Of<IRoslynEditService>(), Mock.Of<IDiffPreviewService>(), Mock.Of<ICompileValidator>(),
                Mock.Of<IErrorFixer>(), Mock.Of<IProviderProfileRepository>(), Mock.Of<ICheckpointService>(),
                NullLogger<ChatService>.Instance, Mock.Of<IOutputLogger>(), Mock.Of<IStatusBarService>(), Mock.Of<IInfoBarService>(),
                Mock.Of<ISettingsService>(), Plans.Object, Mock.Of<IToolGatingService>(),
                Mock.Of<IVisualStudioEnvironmentService>(), Mock.Of<ILogBus>(), new TaskLockService(), commands);
            Service.ActiveMode = AgentModes.Act;
            Service.ResilienceOptions = new ChatResilienceOptions
            {
                MaxModelRequests = 12, MaxEmptyRetries = 2, MaxTransportRetries = 2, MaxRateLimitRetries = 2,
                MaxConsecutiveMistakes = 3, MaxIdenticalToolCalls = 2,
                BackoffBase = TimeSpan.Zero, MaxBackoff = TimeSpan.Zero,
                TurnTimeout = TimeSpan.FromSeconds(5), StreamIdleTimeout = TimeSpan.FromSeconds(1)
            };
            Service.ErrorOccurred += (_, error) => Errors.Add(error);
            Service.AgentPaused += (_, pause) => Errors.Add(pause.Summary);
            Service.StreamingCompleted += (_, message) => Completed.Add(message);
        }

        public Task Run(CancellationToken ct = default) => Service.SendMessageAsync("s", "original task", ct: ct);
        public string ModelHistory(int request) => string.Join("\n", Client.Requests[request].SelectMany(m => m.Contents)
            .Select(c => c is FunctionResultContent result ? result.Result?.ToString() : c.ToString()));
    }

    private static AIFunction Tool(string name, Func<object?> invoke, string schema = "{\"type\":\"object\",\"properties\":{}}") =>
        new InlineCustomAIFunction(name, "test", schema, _ => Task.FromResult(invoke()));

    [Fact]
    public async Task InvalidBatch_GetsCorrectionOpportunityBeforePausing()
    {
        var h = new Harness();
        h.Client.Script = n => n == 1
            ? new object[] { Call("unknown", "a"), Call("unknown", "b"), Call("unknown", "c") }
            : new object[] { Text("recovered") };
        await h.Run();
        Assert.Equal(2, h.Client.Requests.Count);
        Assert.Empty(h.Errors);
        Assert.Equal(3, h.Client.Requests[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Count());
    }

    [Fact]
    public async Task Pause_IsPersistedBeforeNotification_AndContinuationClearsIt()
    {
        var h = new Harness();
        AgentPause? notified = null;
        h.Service.AgentPaused += (_, pause) =>
        {
            Assert.Contains("Pause", h.History.Last().SerializedContentBlocks);
            notified = pause;
        };
        h.Client.Script = n => new object[] { Call("unknown", "c" + n) };
        await h.Run();
        Assert.NotNull(notified);
        Assert.Equal(AgentPauseReason.InvalidToolArguments, notified!.Reason);
        Assert.Contains("unknown", notified.Details);
        var saved = await h.Service.GetPausedTurnAsync("s");
        Assert.Equal(notified.Details, saved!.Details);
        h.Client.Script = _ => new object[] { Text("recovered") };
        await h.Service.SendMessageAsync("s", saved.RecoveryPrompt, "Continue");
        Assert.Null(await h.Service.GetPausedTurnAsync("s"));
        Assert.Contains("invalid_tool_call", h.ModelHistory(h.Client.Requests.Count - 1));
    }

    [Fact]
    public async Task ContextUsage_UsesLatestProviderUsageWithoutDoubleCountingCache()
    {
        var h = new Harness(Tool("read_file", () => "file contents"));
        var counts = new List<int>();
        h.Service.TokenUsageChanged += (_, usage) => counts.Add(usage.Current);
        h.Client.Script = n => new object[]
        {
            n == 1 ? Call("read_file") : Text("done"),
            new ChatResponseUpdate { Contents = new List<AIContent>
            {
                new UsageContent(new UsageDetails
                {
                    InputTokenCount = n == 1 ? 1000 : 1500,
                    OutputTokenCount = 100,
                    CachedInputTokenCount = 500
                })
            } }
        };
        await h.Run();
        Assert.Equal(2, h.Client.Requests.Count);
        Assert.Contains(1100, counts);
        Assert.Equal(1600, counts.Last());
    }

    [Fact]
    public async Task EmptyCompletedResponse_FeedsBackAndRecovers()
    {
        var h = new Harness();
        h.Client.Script = n => n == 1 ? new object[] { Text("  "), Text("") } : new object[] { Text("answer") };
        await h.Run();
        Assert.Equal(2, h.Client.Requests.Count);
        Assert.Contains(h.Client.Requests[1], m => m.Text.Contains("empty_response"));
        Assert.Empty(h.Errors);
        Assert.Equal("answer", Assert.Single(h.Completed).Content);
    }

    [Fact]
    public async Task EmptyResponse_ExhaustionAsksUserAtConfiguredLimit()
    {
        var h = new Harness();
        h.Client.Script = _ => Array.Empty<object>();
        await h.Run();
        Assert.Equal(3, h.Client.Requests.Count);
        Assert.Single(h.Errors);
        Assert.Contains("Choose Continue", h.History.Last().Content);
        Assert.Single(h.Completed);
    }

    [Fact]
    public async Task EmptyDeltasWithinStream_DoNotRestartGeneration()
    {
        var h = new Harness();
        h.Client.Script = _ => new object[] { Text(""), Text("hello"), Text(""), Text(" world") };
        await h.Run();
        Assert.Single(h.Client.Requests);
        Assert.Equal("hello world", Assert.Single(h.Completed).Content);
    }

    [Fact]
    public async Task RateLimit_RecoversWithStructuredFeedback()
    {
        var h = new Harness();
        h.Client.Script = n => n == 1 ? new object[] { new Azure.RequestFailedException(429, "rate limited") } : new object[] { Text("answer") };
        await h.Run();
        Assert.Equal(2, h.Client.Requests.Count);
        Assert.Contains(h.Client.Requests[1], m => m.Text.Contains("rate_limit"));
        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task RateLimit_StopsAfterConfiguredBudget()
    {
        var h = new Harness();
        h.Client.Script = _ => new object[] { new Azure.RequestFailedException(429, "rate limited") };
        await h.Run();
        Assert.Equal(3, h.Client.Requests.Count);
        Assert.Single(h.Errors);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    public async Task PermanentProviderFailure_DoesNotRetry(int status)
    {
        var h = new Harness();
        h.Client.Script = _ => new object[] { new Azure.RequestFailedException(status, "Invalid request") };
        await h.Run();
        Assert.Single(h.Client.Requests);
        Assert.Single(h.Errors);
        Assert.Single(h.Completed);
    }

    [Fact]
    public async Task ContextOverflow_RetainsOriginalAndLatestTask()
    {
        var h = new Harness();
        h.History.Add(new ConversationTurn { Role = "user", Content = "initial requirement" });
        h.History.Add(new ConversationTurn { Role = "assistant", Content = "older detail" });
        h.Client.Script = n => n == 1 ? new object[] { new Azure.RequestFailedException(400, "context length exceeded") } : new object[] { Text("answer") };
        await h.Run();
        Assert.Equal(2, h.Client.Requests.Count);
        Assert.Contains(h.Client.Requests[1], m => m.Text.Contains("initial requirement"));
        Assert.Contains(h.Client.Requests[1], m => m.Text.Contains("original task"));
        Assert.DoesNotContain(h.Client.Requests[1], m => m.Text.Contains("older detail"));
        Assert.Empty(h.Errors);
    }

    [Theory]
    [InlineData("<read_file><path>a</path>")]
    [InlineData("<read_file><path>a</wrong></read_file>")]
    [InlineData("<invented_tool><path>a</path></invented_tool>")]
    public async Task MalformedXml_FeedsBackBeforeExecutingAnything(string xml)
    {
        int invoked = 0;
        var h = new Harness(Tool("read_file", () => { invoked++; return "ok"; }));
        h.Client.Script = n => n == 1 ? new object[] { Text(xml) } : new object[] { Text("answer") };
        await h.Run();
        Assert.Equal(0, invoked);
        Assert.Equal(2, h.Client.Requests.Count);
        Assert.Contains(h.Client.Requests[1], m => m.Text.Contains("invalid_tool_xml"));
        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task UnknownNativeTool_IsRejectedAndModelCanCorrectIt()
    {
        int invoked = 0;
        var h = new Harness(Tool("read_file", () => { invoked++; return "ok"; }));
        h.Client.Script = n => n == 1 ? new object[] { Call("invented") } :
            n == 2 ? new object[] { Call("read_file") } : new object[] { Text("answer") };
        await h.Run();
        Assert.Equal(1, invoked);
        Assert.Equal(3, h.Client.Requests.Count);
        Assert.Contains(h.Client.Requests[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>(),
            r => r.Result!.ToString()!.Contains("Unknown or unavailable"));
        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task NativeArgumentParsingError_IsNeverExecuted()
    {
        int invoked = 0;
        var h = new Harness(Tool("read_file", () => { invoked++; return "ok"; }));
        var broken = Call("read_file");
        ((FunctionCallContent)broken.Contents[0]).Exception = new FormatException("bad JSON");
        h.Client.Script = n => n == 1 ? new object[] { broken } : new object[] { Text("answer") };
        await h.Run();
        Assert.Equal(0, invoked);
        Assert.Contains(h.Client.Requests[1], m => m.Text.Contains("incomplete_response"));
        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task XmlUsesPlanModeGuard_NotRawRegistryFunction()
    {
        int invoked = 0;
        var h = new Harness(Tool("execute_query", () => { invoked++; return "ok"; },
            "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}},\"required\":[\"query\"]}"));
        h.Service.ActiveMode = AgentModes.Plan;
        h.Client.Script = _ => new object[] { Text("<execute_query><query>DELETE FROM users</query></execute_query>") };
        await h.Run();
        Assert.Equal(0, invoked);
        Assert.Equal(3, h.Client.Requests.Count);
        Assert.Single(h.Errors);
    }

    [Fact]
    public async Task SuccessfulTool_ResetsConsecutiveMistakes()
    {
        var h = new Harness(Tool("read_file", () => "ok"));
        h.Service.ResilienceOptions.MaxConsecutiveMistakes = 2;
        h.Client.Script = n => n == 1 || n == 3 ? new object[] { Call("invented", "bad" + n) } :
            n == 2 ? new object[] { Call("read_file") } : new object[] { Text("answer") };
        await h.Run();
        Assert.Equal(4, h.Client.Requests.Count);
        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task FailedTerminalTool_DoesNotClaimCompletion()
    {
        var h = new Harness(Tool("complete_task", () => "Error: task does not exist"));
        h.Client.Script = n => new object[] { Call("complete_task", "c" + n) };
        await h.Run();
        Assert.Equal(3, h.Client.Requests.Count);
        Assert.Single(h.Errors);
    }

    [Theory]
    [InlineData("<read_file></read_file>")]
    [InlineData("<read_file><path>a</path><extra>b</extra></read_file>")]
    public async Task XmlMissingOrExtraParameter_ProducesToolResultError(string xml)
    {
        int invoked = 0;
        var h = new Harness(Tool("read_file", () => { invoked++; return "ok"; },
            "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"]}"));
        h.Client.Script = n => n == 1 ? new object[] { Text(xml) } : new object[] { Text("answer") };
        await h.Run();
        Assert.Equal(0, invoked);
        Assert.Contains(h.Client.Requests[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>(),
            r => r.Result!.ToString()!.Contains("invalid_tool_call"));
    }

    [Fact]
    public async Task PartialXmlAcrossDeltas_ExecutesOnceAfterCompletion()
    {
        int invoked = 0;
        var h = new Harness(Tool("read_file", () => { invoked++; return "ok"; }));
        h.Client.Script = n => n == 1 ? new object[] { Text("<read_"), Text("file>"), Text("</read_file>") } : new object[] { Text("answer") };
        await h.Run();
        Assert.Equal(1, invoked);
        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task XmlExampleInCodeFence_IsNotExecuted()
    {
        int invoked = 0;
        var h = new Harness(Tool("read_file", () => { invoked++; return "ok"; }));
        h.Client.Script = _ => new object[] { Text("Example:\n```xml\n<read_file></read_file>\n```") };
        await h.Run();
        Assert.Equal(0, invoked);
        Assert.Single(h.Client.Requests);
        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task CancellationDuringBackoff_PersistsEarlierToolResults()
    {
        var h = new Harness(Tool("read_file", () => "file contents"));
        using var cancellation = new CancellationTokenSource();
        h.Service.RetryInitiated += (_, _) => cancellation.Cancel();
        h.Client.Script = n => n == 1 ? new object[] { Call("read_file") } : new object[] { new IOException("lost") };
        await h.Run(cancellation.Token);
        Assert.Equal(2, h.Client.Requests.Count);
        Assert.Contains("file contents", h.History.Last().SerializedContentBlocks);
        Assert.True(Assert.Single(h.Completed).WasCancelled);
        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task DroppedStreamWithToolCall_DoesNotExecutePartialGeneration()
    {
        int invoked = 0;
        var h = new Harness(Tool("read_file", () => { invoked++; return "ok"; }));
        h.Client.Script = n => n == 1 ? new object[] { Call("read_file"), new IOException("connection dropped") }
            : n == 2 ? new object[] { Call("read_file") } : new object[] { Text("answer") };
        await h.Run();
        Assert.Equal(1, invoked);
        Assert.Equal(3, h.Client.Requests.Count);
        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task CompletedToolThenDroppedNextGeneration_DoesNotReplayTool()
    {
        int invoked = 0;
        var h = new Harness(Tool("read_file", () => { invoked++; return "ok"; }));
        h.Client.Script = n => n == 1 ? new object[] { Call("read_file") }
            : n == 2 ? new object[] { Text("partial"), new IOException("lost") } : new object[] { Text("answer") };
        await h.Run();
        Assert.Equal(1, invoked);
        Assert.Single(h.Client.Requests[2].SelectMany(m => m.Contents).OfType<FunctionResultContent>());
    }

    [Fact]
    public async Task NoToolStop_ContinuesThenStopsAtMistakeLimit()
    {
        var h = new Harness(new AiAssistant.Tools.Functions.Act.AttemptCompletionTool());
        h.Client.Script = _ => new object[] { Text("I will do that next.") };
        await h.Run();
        Assert.Equal(3, h.Client.Requests.Count);
        Assert.Contains(h.Client.Requests[1], m => m.Text.Contains("stopped_before_completion"));
        Assert.Single(h.Errors);
    }

    private static ChatResponseUpdate Completion(string summary = "Fixed the issue", string verification = "Checks passed") =>
        Call("attempt_completion", "finish", ("summary", summary), ("verification", verification));

    [Fact]
    public async Task CompletionRejectsPendingCommandVerification()
    {
        using var commands = new CommandSessionService();
        CommandSessionService.Owner.Value = "s";
        commands.Start("ping -n 30 127.0.0.1", Path.GetTempPath(), false, 30, CancellationToken.None);
        var h = new Harness(commands, new AiAssistant.Tools.Functions.Act.AttemptCompletionTool());
        h.Client.Script = _ => new object[] { Completion() };
        await h.Run();
        Assert.Contains("unread final output", h.ModelHistory(1));
        Assert.DoesNotContain(h.Completed, message => message.Content.Contains("Verification: Checks passed"));
    }

    [Fact]
    public void PollingUsesTotalBudgetWithoutIdenticalCallFailures()
    {
        var recovery = new ChatTurnRecovery(new ChatResilienceOptions { MaxIdenticalToolCalls = 1, MaxToolCalls = 3 });
        var args = new Dictionary<string, object?> { ["session_id"] = "session", ["cursor"] = 0 };
        Assert.Null(recovery.CheckToolCall("read_command_output", args));
        Assert.Null(recovery.CheckToolCall("read_command_output", args));
        Assert.Null(recovery.CheckToolCall("read_command_output", args));
        Assert.Contains("total tool-call budget", recovery.CheckToolCall("read_command_output", args));
    }

    [Fact]
    public async Task CompletionWithoutPlan_PublishesAndPersistsSummary()
    {
        var h = new Harness(new AiAssistant.Tools.Functions.Act.AttemptCompletionTool());
        h.Client.Script = n => n == 1 ? new object[] { Text("I will inspect this.") } : new object[] { Completion() };
        await h.Run();
        Assert.Equal(2, h.Client.Requests.Count);
        Assert.Empty(h.Errors);
        Assert.Contains("Fixed the issue", h.Completed.Last().Content);
        Assert.Contains("Verification: Checks passed", h.History.Last().Content);
        Assert.Contains("Fixed the issue", h.History.Last().SerializedContentBlocks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedCompletion_HasOneCanonicalAnswerInLiveAndPersistedTrace(bool xml)
    {
        var h = new Harness(new AiAssistant.Tools.Functions.Act.AttemptCompletionTool());
        var prose = new string('x', 2000);
        h.Client.Script = _ => xml
            ? new object[] { Text(prose + "<attempt_completion><summary>Done</summary><verification>Checked</verification></attempt_completion>") }
            : new object[] { Text(prose), Completion("Done", "Checked") };
        await h.Run();
        Assert.Single(h.Client.Requests); // Rendering requires no additional model call.
        var live = Assert.Single(h.Completed);
        var liveAnswer = Assert.Single(live.ContentBlocks.Where(b => b.CompletionCallId != null));
        Assert.Equal("Done\n\nVerification: Checked", liveAnswer.Text);
        Assert.DoesNotContain(live.ContentBlocks, b => b.Text == prose);
        var persisted = System.Text.Json.JsonSerializer.Deserialize<List<ContentBlock>>(h.History.Last().SerializedContentBlocks);
        var savedAnswer = Assert.Single(persisted.Where(b => b.CompletionCallId != null));
        Assert.Equal(liveAnswer.CompletionCallId, savedAnswer.CompletionCallId);
        Assert.Equal(liveAnswer.Text, savedAnswer.Text);
        Assert.Contains(persisted, b => b.Name == "attempt_completion" && b.CallId == savedAnswer.CompletionCallId);
        Assert.Equal(live.Content, h.History.Last().Content);
        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task RejectedCompletion_DoesNotPublishCanonicalAnswer()
    {
        var h = new Harness(new AiAssistant.Tools.Functions.Act.AttemptCompletionTool());
        h.Client.Script = _ => new object[] { Completion(verification: " ") };
        await h.Run();
        Assert.Single(h.Errors);
        Assert.DoesNotContain(h.Completed.SelectMany(m => m.ContentBlocks ?? Array.Empty<ContentBlock>()),
            b => b.CompletionCallId != null);
        var persisted = System.Text.Json.JsonSerializer.Deserialize<List<ContentBlock>>(h.History.Last().SerializedContentBlocks);
        Assert.DoesNotContain(persisted, b => b.CompletionCallId != null);
    }

    [Fact]
    public async Task QuestionToolAlone_DoesNotForceTextToRetry()
    {
        var h = new Harness(Tool("ask_question", () => "asked"));
        await h.Run();
        Assert.Single(h.Client.Requests);
        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task PlanMode_AllowsTextDespiteRegisteredCompletionTool()
    {
        var h = new Harness(new AiAssistant.Tools.Functions.Act.AttemptCompletionTool());
        h.Service.ActiveMode = AgentModes.Plan;
        await h.Run();
        Assert.Single(h.Client.Requests);
        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task XmlCompletion_PublishesSummary()
    {
        var h = new Harness(new AiAssistant.Tools.Functions.Act.AttemptCompletionTool());
        h.Client.Script = _ => new object[] { Text("<attempt_completion><summary>Finished</summary><verification>Reviewed results</verification></attempt_completion>") };
        await h.Run();
        Assert.Single(h.Client.Requests);
        Assert.Empty(h.Errors);
        Assert.Contains("Verification: Reviewed results", h.History.Last().Content);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CompletionRejectsEmptyVerification(string verification)
    {
        var h = new Harness(new AiAssistant.Tools.Functions.Act.AttemptCompletionTool());
        h.Client.Script = n => n == 1 ? new object[] { Completion(verification: verification) } : new object[] { Completion() };
        await h.Run();
        Assert.Equal(2, h.Client.Requests.Count);
        Assert.Contains("nonempty", h.ModelHistory(1));
        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task CompletionRejectsUnfinishedPlan()
    {
        var h = new Harness(new AiAssistant.Tools.Functions.Act.AttemptCompletionTool());
        h.Plans.Setup(p => p.GetActivePlanAsync("s", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Plan { Status = PlanStatus.InProgress, Tasks = new List<PlanTask> { new PlanTask() } });
        h.Client.Script = _ => new object[] { Completion() };
        await h.Run();
        Assert.Contains("unfinished tasks", h.ModelHistory(1));
        Assert.Single(h.Errors);
    }

    [Fact]
    public async Task CompletionMustWaitForSeparateToolResults()
    {
        int invoked = 0;
        var h = new Harness(new AiAssistant.Tools.Functions.Act.AttemptCompletionTool(),
            Tool("read_file", () => { invoked++; return "ok"; }));
        h.Client.Script = n => n == 1 ? new object[] { Completion(), Call("read_file", "r") } : new object[] { Completion() };
        await h.Run();
        Assert.Equal(2, h.Client.Requests.Count);
        Assert.Equal(1, invoked);
        Assert.Contains("separate response", h.ModelHistory(1));
        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task TerminalNativeCall_StopsAndSkipsRemainingBatch()
    {
        int invoked = 0;
        var h = new Harness(Tool("ask_question", () => "question asked"), Tool("read_file", () => { invoked++; return "ok"; }));
        h.Client.Script = _ => new object[] { Call("ask_question", "q"), Call("read_file", "r") };
        await h.Run();
        Assert.Single(h.Client.Requests);
        Assert.Equal(0, invoked);
        Assert.Empty(h.Errors);
        Assert.Contains("Skipped", h.History.Last().SerializedContentBlocks);
    }

    [Fact]
    public async Task RepeatedIdenticalNativeCalls_AreBoundedBeforeSideEffects()
    {
        int invoked = 0;
        var h = new Harness(Tool("read_file", () => { invoked++; return "ok"; }));
        h.Client.Script = n => new object[] { Call("read_file", "c" + n) };
        await h.Run();
        Assert.Equal(2, invoked);
        Assert.Equal(5, h.Client.Requests.Count);
        Assert.Single(h.Errors);
    }

    [Fact]
    public async Task RepeatedToolFailures_StopAndFeedErrorsToModel()
    {
        var h = new Harness(Tool("read_file", () => throw new IOException("file unavailable")));
        h.Client.Script = n => new object[] { Call("read_file", "c" + n) };
        await h.Run();
        Assert.Equal(3, h.Client.Requests.Count);
        Assert.Contains(h.Client.Requests[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>(),
            r => r.Result!.ToString()!.Contains("tool_failed"));
        Assert.Single(h.Errors);
    }

    [Fact]
    public async Task EndlessDifferentCalls_StopAtGlobalRequestBudget()
    {
        var h = new Harness(Tool("read_file", () => "ok", "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}}}"));
        h.Service.ResilienceOptions.MaxModelRequests = 4;
        h.Client.Script = n => new object[] { Call("read_file", "c" + n, ("path", n.ToString())) };
        await h.Run();
        Assert.Equal(4, h.Client.Requests.Count);
        Assert.Single(h.Errors);
    }

    [Fact]
    public async Task IdleStreamTimeout_ExhaustsTransportBudget()
    {
        var h = new Harness();
        h.Service.ResilienceOptions.StreamIdleTimeout = TimeSpan.FromMilliseconds(10);
        h.Client.Script = _ => new object[] { TimeSpan.FromSeconds(1) };
        await h.Run();
        Assert.Equal(3, h.Client.Requests.Count);
        Assert.Single(h.Errors);
    }

    [Fact]
    public async Task TurnTimeout_AsksUserAndFinalizesOnce()
    {
        var h = new Harness();
        h.Service.ResilienceOptions.TurnTimeout = TimeSpan.FromMilliseconds(150);
        h.Client.Script = _ => new object[] { TimeSpan.FromSeconds(1) };
        await h.Run();
        Assert.Single(h.Client.Requests);
        Assert.Contains("timeout", Assert.Single(h.Errors));
        Assert.False(Assert.Single(h.Completed).WasCancelled);
        Assert.Contains("Choose Continue", h.History.Last().Content);
    }

    [Fact]
    public async Task TruncatedResponse_DoesNotExecuteNativeCall()
    {
        int invoked = 0;
        var h = new Harness(Tool("read_file", () => { invoked++; return "ok"; }));
        var truncated = Call("read_file");
        truncated.FinishReason = ChatFinishReason.Length;
        h.Client.Script = n => n == 1 ? new object[] { truncated } : new object[] { Text("answer") };
        await h.Run();
        Assert.Equal(0, invoked);
        Assert.Equal(2, h.Client.Requests.Count);
    }

    [Fact]
    public void ContextTrimAfterFeedback_PreservesLatestUserInstruction()
    {
        var messages = new List<Message>
        {
            new(ChatRole.System, "system"), new(ChatRole.User, "initial"), new(ChatRole.Assistant, "old"),
            new(ChatRole.User, "latest instruction"), new(ChatRole.Assistant, "progress"),
            new(ChatRole.User, ChatTurnRecovery.Feedback("transport", "retry"))
        };
        Assert.True(ChatTurnRecovery.TrimHistory(messages));
        Assert.Contains(messages, m => m.Text == "latest instruction");
        Assert.Contains(messages, m => m.Text == "initial");
        Assert.False(ChatTurnRecovery.TrimHistory(messages));
    }

    [Fact]
    public void ToolFingerprint_NormalizesArgumentOrder()
    {
        var recovery = new ChatTurnRecovery(new ChatResilienceOptions { MaxIdenticalToolCalls = 1 });
        Assert.Null(recovery.CheckToolCall("tool", new Dictionary<string, object?> { ["a"] = 1, ["b"] = 2 }));
        Assert.NotNull(recovery.CheckToolCall("tool", new Dictionary<string, object?> { ["b"] = 2, ["a"] = 1 }));
    }

    [Fact]
    public async Task Cancellation_DoesNotBecomeModelFeedbackOrRetry()
    {
        var h = new Harness();
        using var cancellation = new CancellationTokenSource();
        h.Client.Script = _ => new object[] { TimeSpan.FromSeconds(1) };
        cancellation.CancelAfter(150);
        await h.Run(cancellation.Token);
        Assert.Single(h.Client.Requests);
        Assert.Empty(h.Errors);
        Assert.True(Assert.Single(h.Completed).WasCancelled);
    }

    [Fact]
    public void ParserUnderscoreTag_AdvancesInsteadOfLooping()
    {
        Assert.False(AssistantMessageParser.ContainsUnknownXmlTags("<_private>"));
    }

    [Fact]
    public void RetryAfterDate_IsCappedAndExpiredResetIsZero()
    {
        var recovery = new ChatTurnRecovery(new ChatResilienceOptions());
        var error = new IOException("429");
        error.Data["Retry-After"] = DateTimeOffset.UtcNow.AddHours(1).ToString("R");
        Assert.Equal(TimeSpan.FromSeconds(30), recovery.RetryDelay(error, 1));
        error.Data.Clear();
        error.Data["X-RateLimit-Reset"] = "1";
        Assert.Equal(TimeSpan.Zero, recovery.RetryDelay(error, 1));
    }

    [Fact]
    public void MissingResultInBatch_IsRepairedByCallId()
    {
        var messages = new List<Message>
        {
            new(ChatRole.Assistant, new List<AIContent> { new FunctionCallContent("a", "one"), new FunctionCallContent("b", "two") }),
            new(ChatRole.Tool, new List<AIContent> { new FunctionResultContent("a", "done") }),
            new(ChatRole.User, "continue")
        };
        ChatTurnRecovery.RepairToolPairs(messages);
        Assert.Equal(2, messages[1].Contents.Count);
        Assert.Contains(messages[1].Contents.OfType<FunctionResultContent>(), r => r.CallId == "b" && r.Result!.ToString()!.Contains("unknown"));
    }
}
