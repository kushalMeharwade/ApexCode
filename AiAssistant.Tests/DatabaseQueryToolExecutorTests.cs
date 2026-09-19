using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using AiAssistant.Core.Models;
using AiAssistant.Core.Services;
using AiAssistant.Storage.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Moq;
using ApexCode.Tools;
using Xunit;

namespace AiAssistant.Tests;

public class DatabaseQueryToolExecutorTests : IDisposable
{
    private readonly string _dbName;
    private readonly string _masterConnectionString;
    private readonly string _testConnectionString;

    private readonly Mock<IDatabaseConnectionService> _mockDbService;
    private readonly Mock<ILogger<DatabaseQueryToolExecutor>> _mockLogger;
    private readonly Mock<IOutputLogger> _mockOutputLogger;
    private readonly Mock<IApprovalService> _mockApprovalService;
    private readonly Mock<ISettingsService> _mockSettingsService;

    private DatabaseConnection _activeConnection;

    public DatabaseQueryToolExecutorTests()
    {
        _dbName = "AiAssistantTestDB_" + Guid.NewGuid().ToString("N");
        _masterConnectionString = @"Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=master;Integrated Security=True;TrustServerCertificate=True;";
        _testConnectionString = $@"Data Source=(localdb)\MSSQLLocalDB;Initial Catalog={_dbName};Integrated Security=True;TrustServerCertificate=True;";

        CreateTestDatabase();
        PopulateTestDatabase();

        _mockDbService = new Mock<IDatabaseConnectionService>();
        _mockLogger = new Mock<ILogger<DatabaseQueryToolExecutor>>();
        _mockOutputLogger = new Mock<IOutputLogger>();
        _mockApprovalService = new Mock<IApprovalService>();
        _mockSettingsService = new Mock<ISettingsService>();
        _mockSettingsService.Setup(s => s.GetToolApprovalMode(It.IsAny<string>())).Returns("allowall");

        _activeConnection = new DatabaseConnection
        {
            Name = "TestConnection",
            ServerAddress = "(localdb)\\MSSQLLocalDB",
            DatabaseName = _dbName,
            Permission = PermissionLevel.AllowWrite,
            RequireApproval = false
        };

        _mockDbService.Setup(d => d.ActiveConnection).Returns(() => _activeConnection);
        _mockDbService.Setup(d => d.GetConnectionStringAsync(It.IsAny<DatabaseConnection>())).ReturnsAsync(() => _testConnectionString);
    }

    private void SetConnection(PermissionLevel permission, bool requireApproval = false)
    {
        _activeConnection = new DatabaseConnection
        {
            Name = "TestConnection",
            ServerAddress = "(localdb)\\MSSQLLocalDB",
            DatabaseName = _dbName,
            Permission = permission,
            RequireApproval = requireApproval
        };
    }

    private void CreateTestDatabase()
    {
        using var conn = new SqlConnection(_masterConnectionString);
        conn.Open();
        using var cmd = new SqlCommand($"CREATE DATABASE [{_dbName}]", conn);
        cmd.ExecuteNonQuery();
    }

    private void PopulateTestDatabase()
    {
        using var conn = new SqlConnection(_testConnectionString);
        conn.Open();
        
        var sql = @"
            CREATE TABLE Users (
                Id INT PRIMARY KEY IDENTITY(1,1),
                Name NVARCHAR(100) NOT NULL,
                Email NVARCHAR(100) NULL
            );

            CREATE TABLE Orders (
                OrderId INT PRIMARY KEY IDENTITY(1,1),
                UserId INT NOT NULL,
                TotalAmount DECIMAL(18,2) NOT NULL,
                FOREIGN KEY (UserId) REFERENCES Users(Id)
            );

            INSERT INTO Users (Name, Email) VALUES ('Alice', 'alice@test.com'), ('Bob', 'bob@test.com');
        ";

        using var cmd = new SqlCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        try
        {
            using var conn = new SqlConnection(_masterConnectionString);
            conn.Open();
            // Force drop database
            using var cmd = new SqlCommand($@"
                ALTER DATABASE [{_dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{_dbName}];
            ", conn);
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    private DatabaseQueryToolExecutor CreateSut()
    {
        return new DatabaseQueryToolExecutor(
            _mockDbService.Object,
            _mockLogger.Object,
            _mockOutputLogger.Object,
            _mockApprovalService.Object,
            _mockSettingsService.Object
        );
    }

    [Fact]
    public async Task GetSchemaAsync_NoTableName_ReturnsFullSchema()
    {
        // A-DB-001
        var sut = CreateSut();
        var result = await sut.GetSchemaAsync(null);

        Assert.NotNull(result);
        Assert.Contains("Table: [dbo].[Users]", result);
        Assert.Contains("Table: [dbo].[Orders]", result);
        Assert.Contains("[Id] int NOT NULL", result);
        Assert.Contains("[Email] nvarchar(100) NULL", result);
        Assert.Contains("PK: [Id]", result);
        Assert.Contains("FK: [UserId] -> [dbo].[Users].[Id]", result);
    }

    [Fact]
    public async Task GetSchemaAsync_ValidTableName_ReturnsFilteredSchema()
    {
        // A-DB-002
        var sut = CreateSut();
        var result = await sut.GetSchemaAsync("Users");

        Assert.NotNull(result);
        Assert.Contains("Table: [dbo].[Users]", result);
        Assert.DoesNotContain("Table: [dbo].[Orders]", result);
    }

    [Fact]
    public async Task GetSchemaAsync_SqlInjectionAttempt_ReturnsBenignResult()
    {
        // A-DB-003
        var sut = CreateSut();
        var result = await sut.GetSchemaAsync("'; DROP TABLE Users;--");

        Assert.NotNull(result);
        Assert.Contains("No tables found", result); // Parameterized query prevents injection

        // Verify Users table still exists
        var testResult = await sut.GetSchemaAsync("Users");
        Assert.Contains("Table: [dbo].[Users]", testResult);
    }

    [Fact]
    public async Task ExecuteQueryAsync_NoActiveConnection_ReturnsError()
    {
        // A-DB-004
        _activeConnection = null!;
        var sut = CreateSut();
        var result = await sut.ExecuteQueryAsync("SELECT 1");

        Assert.Contains("No active database connection", result);
    }

    [Fact]
    public async Task ExecuteQueryAsync_SelectOnReadOnly_ExecutesSuccessfully()
    {
        // A-DB-005
        SetConnection(PermissionLevel.ReadOnly);
        var sut = CreateSut();
        var result = await sut.ExecuteQueryAsync("SELECT * FROM Users");

        using var json = JsonDocument.Parse(result);
        Assert.Equal(2, json.RootElement.GetProperty("returnedRowCount").GetInt32());
        Assert.False(json.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Contains("Alice", result);
        Assert.Contains("alice@test.com", result);
    }

    [Fact]
    public async Task ExecuteQueryAsync_UpdateOnReadOnly_Rejected()
    {
        // A-DB-006
        SetConnection(PermissionLevel.ReadOnly);
        var sut = CreateSut();
        var result = await sut.ExecuteQueryAsync("UPDATE Users SET Name = 'Changed'");

        Assert.Contains("Permission denied", result);
        Assert.Contains("read-only", result);
    }

    [Fact]
    public async Task ExecuteQueryAsync_UpdateOnAllowWrite_IsStillReadOnly()
    {
        // A-DB-007
        SetConnection(PermissionLevel.AllowWrite);
        var sut = CreateSut();
        var result = await sut.ExecuteQueryAsync("UPDATE Users SET Name = 'Changed' WHERE Id = 1");

        Assert.Contains("Permission denied", result);
        Assert.Contains("Alice", await sut.ExecuteQueryAsync("SELECT Name FROM Users WHERE Id=1"));
    }

    [Fact]
    public async Task ExecuteQueryAsync_CTEOnReadOnly_PassesGate()
    {
        // A-DB-008
        SetConnection(PermissionLevel.ReadOnly);
        var sut = CreateSut();
        var result = await sut.ExecuteQueryAsync("WITH cte AS (SELECT 1 as Num) SELECT * FROM cte");

        using var json = JsonDocument.Parse(result);
        Assert.Equal(1, json.RootElement.GetProperty("returnedRowCount").GetInt32());
    }

    [Fact]
    public async Task ExecuteQueryAsync_DDLOnReadOnly_Rejected()
    {
        // A-DB-009
        SetConnection(PermissionLevel.ReadOnly);
        var sut = CreateSut();
        var result = await sut.ExecuteQueryAsync("DROP TABLE Users");

        Assert.Contains("Permission denied", result);
    }

    [Fact]
    public async Task ExecuteQueryAsync_MalformedSql_IsRejectedBeforeExecution()
    {
        // A-DB-010
        SetConnection(PermissionLevel.AllowWrite);
        var sut = CreateSut();
        var result = await sut.ExecuteQueryAsync("SELECT * FROXX Users");

        Assert.Contains("could not be parsed", result);
    }

    [Fact]
    public async Task ExecuteQueryAsync_LargeResultSet_CapsAt100Rows()
    {
        // A-DB-011
        var sut = CreateSut();
        
        // Test fixture setup uses its own connection, never the read-only agent tool.
        using var conn = new SqlConnection(_testConnectionString);
        await conn.OpenAsync();
        using var seed = new SqlCommand(@"
            DECLARE @i INT = 0;
            WHILE @i < 150
            BEGIN
                INSERT INTO Users (Name) VALUES ('User' + CAST(@i AS VARCHAR));
                SET @i = @i + 1;
            END
        ", conn);
        await seed.ExecuteNonQueryAsync();

        var result = await sut.ExecuteQueryAsync("SELECT * FROM Users");

        using var json = JsonDocument.Parse(result);
        Assert.Equal(100, json.RootElement.GetProperty("returnedRowCount").GetInt32());
        Assert.True(json.RootElement.GetProperty("rowsTruncated").GetBoolean());
    }

    [Fact]
    public async Task ExecuteQueryAsync_WaitForBatch_IsRejected()
    {
        // A-DB-012
        var sut = CreateSut();
        // Wait for 35 seconds, command timeout is hardcoded to 30s
        var result = await sut.ExecuteQueryAsync("WAITFOR DELAY '00:00:35'; SELECT 1;");

        Assert.Contains("Permission denied", result);
    }

    [Fact]
    public async Task ExecuteQueryAsync_ApprovalRequired_RejectedByUser()
    {
        // A-DB-013
        SetConnection(PermissionLevel.AllowWrite, requireApproval: true);

        var request = new ApprovalRequest { Id = Guid.NewGuid().ToString(), IsApproved = false, IsRejected = true };
        _mockApprovalService.Setup(a => a.RequestApprovalAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>()))
                            .ReturnsAsync(request);

        _mockApprovalService.Setup(a => a.WaitForApprovalAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                            .Returns(TestHelper(request));

        var sut = CreateSut();
        var result = await sut.ExecuteQueryAsync("SELECT Name FROM Users");

        Assert.Contains("User denied", result);
        
        // Verify DB unchanged
        SetConnection(PermissionLevel.AllowWrite, requireApproval: false); // Turn off for verification query
        var verifyResult = await sut.ExecuteQueryAsync("SELECT Name FROM Users WHERE Id = 1");
        Assert.Contains("Alice", verifyResult);
        Assert.DoesNotContain("Hacked", verifyResult);
    }
    
    [Fact]
    public async Task SchemaFilterSeparatesSchemasAndRecognizesCustomPrimaryKeyName()
    {
        using var conn = new SqlConnection(_testConnectionString);
        await conn.OpenAsync();
        using (var schema = new SqlCommand("CREATE SCHEMA sales", conn)) await schema.ExecuteNonQueryAsync();
        using (var table = new SqlCommand("CREATE TABLE sales.Users (OtherId int NOT NULL CONSTRAINT CustomKey PRIMARY KEY)", conn))
            await table.ExecuteNonQueryAsync();
        var result = await CreateSut().GetSchemaAsync("sales.Users");
        Assert.Contains("Table: [sales].[Users]", result);
        Assert.Contains("PK: [OtherId]", result);
        Assert.DoesNotContain("[Email]", result);
        Assert.DoesNotContain("[dbo].[Users]", result);
    }

    [Fact]
    public async Task StructuredResultsPreserveNullsTypesAndDelimiters()
    {
        var result = await CreateSut().ExecuteQueryAsync("SELECT 42 AS Number, CAST(NULL AS nvarchar(5)) AS Missing, N'pipe|value' + CHAR(10) + 'next line' AS Text");
        using var json = JsonDocument.Parse(result);
        var row = json.RootElement.GetProperty("rows")[0];
        Assert.Equal(42, row[0].GetInt32());
        Assert.Equal(JsonValueKind.Null, row[1].ValueKind);
        Assert.Equal("pipe|value\nnext line", row[2].GetString());
        Assert.Equal(_dbName, json.RootElement.GetProperty("database").GetString());
    }

    [Fact]
    public async Task LongCellsAreBoundedAndMarkedTruncated()
    {
        var result = await CreateSut().ExecuteQueryAsync("SELECT REPLICATE(CAST(N'x' AS nvarchar(max)), 100000) AS LargeValue, 7 AS AfterValue");
        using var json = JsonDocument.Parse(result);
        Assert.Equal(DatabaseQueryToolExecutor.MaximumCellCharacters, json.RootElement.GetProperty("rows")[0][0].GetString().Length);
        Assert.Equal(7, json.RootElement.GetProperty("rows")[0][1].GetInt32());
        Assert.True(json.RootElement.GetProperty("cellsTruncated").GetBoolean());
        Assert.True(json.RootElement.GetProperty("truncated").GetBoolean());
        Assert.True(result.Length <= DatabaseQueryToolExecutor.MaximumResultCharacters);
    }

    [Fact]
    public async Task ResultBudgetAppliesBeforeRowLimit()
    {
        var result = await CreateSut().ExecuteQueryAsync("SELECT TOP (100) REPLICATE(N'x', 2048) AS LargeValue FROM sys.all_objects");
        using var json = JsonDocument.Parse(result);
        Assert.True(json.RootElement.GetProperty("rowsTruncated").GetBoolean());
        Assert.InRange(json.RootElement.GetProperty("returnedRowCount").GetInt32(), 1, 99);
        Assert.True(result.Length <= DatabaseQueryToolExecutor.MaximumResultCharacters);
    }

    [Fact]
    public async Task Exactly100RowsAreNotMarkedTruncated()
    {
        var result = await CreateSut().ExecuteQueryAsync("SELECT TOP (100) object_id FROM sys.all_objects ORDER BY object_id");
        using var json = JsonDocument.Parse(result);
        Assert.Equal(100, json.RootElement.GetProperty("returnedRowCount").GetInt32());
        Assert.False(json.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Theory]
    [InlineData("SELECT * INTO dbo.Copy FROM dbo.Users")]
    [InlineData("WITH x AS (SELECT * FROM dbo.Users) DELETE FROM x")]
    [InlineData("EXEC(N'DELETE FROM dbo.Users')")]
    public async Task BypassAttemptsLeaveDataUnchanged(string sql)
    {
        var executor = CreateSut();
        Assert.Contains("Permission denied", await executor.ExecuteQueryAsync(sql));
        using var json = JsonDocument.Parse(await executor.ExecuteQueryAsync("SELECT COUNT(*) FROM dbo.Users"));
        Assert.Equal(2, json.RootElement.GetProperty("rows")[0][0].GetInt32());
    }

    // Async yield helper for IAsyncEnumerable mock
    private async IAsyncEnumerable<ApprovalRequest> TestHelper(ApprovalRequest req)
    {
        yield return req;
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ExecuteQueryAsync_BatchedSmugglingAttempt_RejectedOnReadOnly()
    {
        // A-DB-014
        SetConnection(PermissionLevel.ReadOnly);
        var sut = CreateSut();
        
        // Try to sneak a write query after a select
        var result = await sut.ExecuteQueryAsync("SELECT 1; DROP TABLE Users;");

        Assert.Contains("Permission denied", result);
        Assert.Contains("read-only", result);
        
        // Verify DB unchanged (Users table still exists)
        var verifyResult = await sut.ExecuteQueryAsync("SELECT * FROM Users");
        Assert.Contains("Alice", verifyResult);
    }
}
