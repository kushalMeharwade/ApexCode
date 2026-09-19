using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Core.Services;
using AiAssistant.Tools.Services;
using Microsoft.Extensions.AI;

namespace AiAssistant.Tools.Functions;

internal static class CommandToolArguments
{
    public static int Integer(IReadOnlyDictionary<string, object?> args, string name, int fallback, int min, int max)
    {
        if (!args.TryGetValue(name, out var value)) return fallback;
        if (!int.TryParse(value?.ToString(), out var result) || result < min || result > max)
            throw new ArgumentException($"{name} must be an integer between {min} and {max}.");
        return result;
    }
}

public sealed class ReadCommandOutputFunction : IToolProvider
{
    private readonly CommandSessionService _sessions;
    private readonly IVisualStudioEnvironmentService _environment;
    public ReadCommandOutputFunction(CommandSessionService sessions, IVisualStudioEnvironmentService environment)
    { _sessions = sessions; _environment = environment; }
    public AIFunction CreateFunction() => new CommandSessionFunction(_sessions, _environment, false);
}

public sealed class StopCommandFunction : IToolProvider
{
    private readonly CommandSessionService _sessions;
    private readonly IVisualStudioEnvironmentService _environment;
    public StopCommandFunction(CommandSessionService sessions, IVisualStudioEnvironmentService environment)
    { _sessions = sessions; _environment = environment; }
    public AIFunction CreateFunction() => new CommandSessionFunction(_sessions, _environment, true);
}

internal sealed class CommandSessionFunction : CustomAIFunction
{
    private readonly CommandSessionService _sessions;
    private readonly IVisualStudioEnvironmentService _environment;
    private readonly bool _stop;
    public CommandSessionFunction(CommandSessionService sessions, IVisualStudioEnvironmentService environment, bool stop)
        : base(stop ? "stop_command" : "read_command_output",
            stop ? "Stop an owned command session and its child processes. Does not start or retry commands." :
            "Read incremental command output. Pass next_cursor from the previous response; repeat while running or has_more. Wait defaults to 10000 ms. Inspect full log if truncated. Running is not successful verification.",
            "{\"type\":\"object\",\"properties\":{\"session_id\":{\"type\":\"string\"},\"cursor\":{\"type\":\"integer\",\"minimum\":0},\"yield_time_ms\":{\"type\":\"integer\",\"minimum\":1000,\"maximum\":30000}},\"required\":[\"session_id\"]}")
    { _sessions = sessions; _environment = environment; _stop = stop; }

    protected override async Task<object?> InvokeCoreImplAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        try
        {
            if (!arguments.TryGetValue("session_id", out var id) || string.IsNullOrWhiteSpace(id?.ToString())) return "Error: session_id is required.";
            var workspace = await _environment.GetWorkspaceRootAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(workspace)) return "Error: No workspace is open.";
            if (_stop) return await _sessions.StopAsync(id!.ToString()!, workspace, cancellationToken).ConfigureAwait(false);
            long cursor = 0;
            if (arguments.TryGetValue("cursor", out var value) && (!long.TryParse(value?.ToString(), out cursor) || cursor < 0))
                return "Error: cursor must be a nonnegative integer.";
            int wait = CommandToolArguments.Integer(arguments, "yield_time_ms", 10000, 1000, 30000);
            return await _sessions.ReadAsync(id!.ToString()!, workspace, cursor, wait, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return "Error: " + ex.Message; }
    }
}
