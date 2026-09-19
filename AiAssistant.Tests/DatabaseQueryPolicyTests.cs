using System;
using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Core.Models;
using AiAssistant.Core.Services;
using AiAssistant.Storage.Models;
using AiAssistant.Tools.Functions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using ApexCode.Tools;
using Xunit;

namespace AiAssistant.Tests;

public class DatabaseQueryPolicyTests
{
    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("-- UPDATE is a comment\nSELECT [DELETE], [DROP] FROM [dbo].[Users];")]
    [InlineData("SELECT 'delete me; EXEC dbo.Bad' AS [UPDATE]")]
    [InlineData("/* DROP TABLE ignored */ SELECT COUNT(*) FROM dbo.Users")]
    [InlineData(";WITH x AS (SELECT 1 AS n) SELECT n FROM x;")]
    [InlineData("SELECT u.Id, COUNT(o.OrderId) FROM dbo.Users u LEFT JOIN dbo.Orders o ON o.UserId=u.Id GROUP BY u.Id")]
    [InlineData("SELECT TOP (10) Id FROM dbo.Users ORDER BY Id")]
    [InlineData("SELECT 1 UNION ALL SELECT 2")]
    public void ReadsAreAllowedByBothBoundaries(string sql)
    {
        Assert.Null(DatabaseQueryPolicy.GetRejectionReason(sql));
        Assert.True(ReadOnlyGuardAIFunction.IsReadOnlyStatement(sql));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-- SELECT 1")]
    [InlineData("SELECT * FROXX Users")]
    [InlineData("SELECT * INTO dbo.Copy FROM dbo.Users")]
    [InlineData("WITH x AS (SELECT * FROM dbo.Users) DELETE FROM x")]
    [InlineData("WITH x AS (SELECT * FROM dbo.Users) UPDATE x SET Name='changed'")]
    [InlineData("EXEC dbo.MutatingProcedure")]
    [InlineData("EXEC sp_help")]
    [InlineData("EXEC(N'DELETE FROM dbo.Users')")]
    [InlineData("SELECT 1; DELETE FROM dbo.Users")]
    [InlineData("SELECT 1 DELETE FROM dbo.Users")]
    [InlineData("SELECT 1; SELECT 2")]
    [InlineData("SELECT 1\nGO\nSELECT 2")]
    [InlineData("SELECT NEXT VALUE FOR dbo.OrderSequence")]
    [InlineData("SELECT * FROM dbo.Users WITH (UPDLOCK)")]
    [InlineData("SELECT * FROM dbo.Users OPTION (MAXDOP 8)")]
    [InlineData("SELECT * FROM OtherDb.dbo.Users")]
    [InlineData("SELECT * FROM Server.OtherDb.dbo.Users")]
    [InlineData("SELECT * FROM OPENQUERY(RemoteServer, 'DELETE FROM Users')")]
    [InlineData("SELECT * FROM OPENROWSET(BULK 'C:\\private.txt', SINGLE_CLOB) AS x")]
    [InlineData("SELECT dbo.CustomFunction()")]
    [InlineData("SELECT * FROM dbo.CustomTableFunction()")]
    [InlineData("SELECT @x=1")]
    [InlineData("SELECT * FROM #temp")]
    public void UnsafeReadsAreBlocked(string sql)
    {
        Assert.NotNull(DatabaseQueryPolicy.GetRejectionReason(sql));
        Assert.False(ReadOnlyGuardAIFunction.IsReadOnlyStatement(sql));
    }

    [Fact]
    public void OversizedQueryIsRejected()
        => Assert.False(DatabaseQueryPolicy.IsReadOnly("SELECT '" + new string('x', DatabaseQueryPolicy.MaximumQueryCharacters) + "'"));

    [Fact]
    public async Task GuardDoesNotInvokeInnerToolForSelectInto()
    {
        var invoked = false;
        var inner = AIFunctionFactory.Create((string queryString) => { invoked = true; return "unexpected"; }, "execute_query");
        var guard = new ReadOnlyGuardAIFunction(inner, "blocked", "queryString");
        var result = await guard.InvokeAsync(new AIFunctionArguments { ["queryString"] = "SELECT 1 AS x INTO dbo.NewTable" });
        Assert.Equal("blocked", result);
        Assert.False(invoked);
    }

    [Theory]
    [InlineData("Plan")]
    [InlineData("Act")]
    public void ModePromptsExplicitlyDescribeReadQueries(string mode)
    {
        Assert.Contains(DatabaseQueryPolicy.ReadOnlyInstruction, AgentModePolicy.BuildModeInstruction(mode));
        Assert.False(AgentModePolicy.IsBlockedInPlanMode("execute_query"));
        Assert.False(AgentModePolicy.IsBlockedInPlanMode("check_database_connection"));
        foreach (PlanStatus status in Enum.GetValues(typeof(PlanStatus)))
            Assert.Contains(DatabaseQueryPolicy.ReadOnlyInstruction, EnhancedAgentModePolicy.BuildInstruction(status));
    }
}

public class DatabaseApprovalSafetyTests
{
    private readonly Mock<IDatabaseConnectionService> _connections = new();
    private readonly Mock<ISettingsService> _settings = new();
    private readonly Mock<IApprovalService> _approval = new();
    private readonly Mock<IOutputLogger> _output = new();
    private DatabaseConnection _active = new() { DatabaseName = "ApprovedDatabase", RequireApproval = false };

    public DatabaseApprovalSafetyTests()
    {
        _connections.SetupGet(x => x.ActiveConnection).Returns(() => _active);
        _connections.Setup(x => x.GetConnectionStringAsync(It.IsAny<DatabaseConnection>())).ReturnsAsync("");
        _settings.Setup(x => x.GetToolApprovalMode(It.IsAny<string>())).Returns("allowall");
    }

    private DatabaseQueryToolExecutor Create(bool withApproval = true) => new(_connections.Object,
        Mock.Of<ILogger<DatabaseQueryToolExecutor>>(), _output.Object, withApproval ? _approval.Object : null, _settings.Object);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiredApprovalFailsClosedWithoutService(bool schema)
    {
        _active = _active with { RequireApproval = true };
        var executor = Create(false);
        var result = schema ? await executor.GetSchemaAsync() : await executor.ExecuteQueryAsync("SELECT 1");
        Assert.Contains("approval service is unavailable", result);
        _connections.Verify(x => x.GetConnectionStringAsync(It.IsAny<DatabaseConnection>()), Times.Never);
    }

    [Theory]
    [InlineData("execute_query")]
    [InlineData("get_database_schema")]
    public async Task DenySettingPreventsConnectionResolution(string tool)
    {
        _settings.Setup(x => x.GetToolApprovalMode(tool)).Returns("denyall");
        var executor = Create();
        var result = tool == "execute_query" ? await executor.ExecuteQueryAsync("SELECT 1") : await executor.GetSchemaAsync();
        Assert.Contains("disabled in tool settings", result);
        _connections.Verify(x => x.GetConnectionStringAsync(It.IsAny<DatabaseConnection>()), Times.Never);
        _settings.Verify(x => x.GetToolApprovalMode("execute_command"), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectionSwitchDuringApprovalKeepsApprovedSnapshot(bool schema)
    {
        _active = _active with { RequireApproval = true };
        var original = _active;
        object approvalParameters = null;
        _approval.Setup(x => x.RequestApprovalAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>()))
            .Callback<string, string, object>((tool, description, parameters) =>
            {
                approvalParameters = parameters;
                _active = new DatabaseConnection { DatabaseName = "DifferentDatabase" };
            }).ReturnsAsync(new ApprovalRequest { IsApproved = true });
        var executor = Create();
        if (schema) await executor.GetSchemaAsync(); else await executor.ExecuteQueryAsync("SELECT 1");
        Assert.Contains("ApprovedDatabase", System.Text.Json.JsonSerializer.Serialize(approvalParameters));
        _connections.Verify(x => x.GetConnectionStringAsync(original), Times.Once);
        _connections.Verify(x => x.GetConnectionStringAsync(_active), Times.Never);
        _connections.Verify(x => x.GetActiveConnectionStringAsync(), Times.Never);
    }

    [Theory]
    [InlineData(PermissionLevel.ReadOnly)]
    [InlineData(PermissionLevel.AllowWrite)]
    [InlineData(PermissionLevel.FullAccess)]
    public async Task LegacyPermissionsDoNotEnableWrites(PermissionLevel permission)
    {
        _active = _active with { Permission = permission };
        Assert.Contains("Permission denied", await Create().ExecuteQueryAsync("SELECT 1 AS x INTO dbo.Copy"));
        _approval.Verify(x => x.RequestApprovalAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>()), Times.Never);
        _connections.Verify(x => x.GetConnectionStringAsync(It.IsAny<DatabaseConnection>()), Times.Never);
    }

    [Fact]
    public async Task CancellationStopsBeforeApprovalOrConnection()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create().ExecuteQueryAsync("SELECT 1", cancellation.Token));
        _connections.Verify(x => x.GetConnectionStringAsync(It.IsAny<DatabaseConnection>()), Times.Never);
    }

    [Fact]
    public async Task GlobalDatabasePromptIsHonored()
    {
        _settings.Setup(x => x.GetToolApprovalMode("execute_query")).Returns("prompt");
        _approval.Setup(x => x.RequestApprovalAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>()))
            .ReturnsAsync(new ApprovalRequest { IsRejected = true });
        Assert.Contains("User denied", await Create().ExecuteQueryAsync("SELECT 1"));
        _connections.Verify(x => x.GetConnectionStringAsync(It.IsAny<DatabaseConnection>()), Times.Never);
    }

    [Fact]
    public async Task ToolPassesCancellationToInjectedExecutor()
    {
        using var cancellation = new CancellationTokenSource();
        var executor = new Mock<IDatabaseToolExecutor>();
        executor.Setup(x => x.ExecuteQueryAsync("SELECT 1", cancellation.Token)).ReturnsAsync("result");
        var tool = new ExecuteDatabaseQueryFunction(_output.Object, executor.Object).CreateFunction();
        var result = await tool.InvokeAsync(new AIFunctionArguments { ["queryString"] = "SELECT 1" }, cancellation.Token);
        Assert.Equal("result", result);
        executor.Verify(x => x.ExecuteQueryAsync("SELECT 1", cancellation.Token), Times.Once);
    }
}
