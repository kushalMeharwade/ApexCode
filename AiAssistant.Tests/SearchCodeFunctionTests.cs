using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Core.Services;
using AiAssistant.Storage.Models;
using AiAssistant.Storage.Repositories;
using AiAssistant.Tools.Functions;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

namespace AiAssistant.Tests;

public class SearchCodeFunctionTests : IDisposable
{
    private readonly Mock<IVisualStudioEnvironmentService> _mockVsEnv;
    private readonly Mock<IEmbeddingProvider> _mockEmbeddingProvider;
    private readonly Mock<IEmbeddingSearchRepository> _mockRepository;
    private readonly Mock<IOutputLogger> _mockLogger;
    private readonly string _testWorkspace;

    public SearchCodeFunctionTests()
    {
        _mockVsEnv = new Mock<IVisualStudioEnvironmentService>();
        _mockEmbeddingProvider = new Mock<IEmbeddingProvider>();
        _mockRepository = new Mock<IEmbeddingSearchRepository>();
        _mockLogger = new Mock<IOutputLogger>();

        _testWorkspace = Path.Combine(Path.GetTempPath(), "AiAssistantSearchTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testWorkspace);

        _mockVsEnv.Setup(v => v.GetWorkspaceRootAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(_testWorkspace);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testWorkspace))
        {
            try { Directory.Delete(_testWorkspace, true); } catch { }
        }
    }

    private AIFunction CreateSut()
    {
        var provider = new SearchCodeFunction(
            _mockVsEnv.Object,
            _mockEmbeddingProvider.Object,
            _mockRepository.Object,
            null,
            _mockLogger.Object);
        return provider.CreateFunction();
    }

    private string CreateTempFile(string name, string content)
    {
        var filePath = Path.Combine(_testWorkspace, name);
        File.WriteAllText(filePath, content);
        return filePath;
    }

    [Fact]
    public async Task SearchCodeAsync_SubstringMatch_ReturnsCorrectResults()
    {
        var sut = CreateSut();
        CreateTempFile("test1.cs", "public class Foo { }");
        CreateTempFile("test2.cs", "public class Bar { public string TargetText => \"Found Me\"; }");

        _mockRepository.Setup(r => r.GetTotalChunksAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var args = new AIFunctionArguments
        {
            { "directory", "." },
            { "pattern", "TargetText" }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("test2.cs:1: public class Bar { public string TargetText => \"Found Me\"; }", result);
        Assert.DoesNotContain("test1.cs", result);
    }

    [Fact]
    public async Task SearchCodeAsync_SemanticSearch_ReturnsHitsWhenIndexPopulated()
    {
        var sut = CreateSut();
        CreateTempFile("real_match.cs", "SemanticQuery should be in a real file too");

        var fakeEmbedding = new float[] { 0.1f, 0.2f };
        _mockEmbeddingProvider.Setup(p => p.EmbedQueryAsync("SemanticQuery", It.IsAny<CancellationToken>()))
                              .ReturnsAsync(fakeEmbedding);

        _mockRepository.Setup(r => r.GetTotalChunksAsync(It.IsAny<CancellationToken>())).ReturnsAsync(100);

        var fakeHits = new List<EmbeddingSearchResult>
        {
            new EmbeddingSearchResult
            {
                Distance = 0.1f,
                Chunk = new EmbeddingChunk
                {
                    FilePath = Path.Combine(_testWorkspace, "semantic.cs"),
                    StartLine = 10,
                    SymbolName = "MySymbol",
                    ChunkText = "Fake semantic result"
                }
            }
        };

        _mockRepository.Setup(r => r.SearchAsync(fakeEmbedding, 20, It.IsAny<CancellationToken>()))
                       .ReturnsAsync(fakeHits);

        var args = new AIFunctionArguments
        {
            { "directory", "." },
            { "pattern", "SemanticQuery" }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("real_match.cs:1: SemanticQuery should be in a real file too", result);
        Assert.Contains("semantic.cs:10:", result);
        Assert.Contains("// Symbol: MySymbol", result);
        Assert.Contains("Fake semantic result", result);
        Assert.Contains("Relevance: 90.0%", result);
    }

    [Fact]
    public async Task SearchCodeAsync_EmptySemanticDB_FallsBackToSubstring()
    {
        var sut = CreateSut();
        CreateTempFile("fallback.txt", "Fallback string present");

        _mockRepository.Setup(r => r.GetTotalChunksAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var args = new AIFunctionArguments
        {
            { "directory", "." },
            { "pattern", "Fallback string" }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("fallback.txt:1: Fallback string present", result);
        _mockEmbeddingProvider.Verify(p => p.EmbedQueryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SearchCodeAsync_IncludesFilter_AppliedCorrectly()
    {
        var sut = CreateSut();
        CreateTempFile("match.cs", "public class Match { }");
        CreateTempFile("match.txt", "Match text file");

        _mockRepository.Setup(r => r.GetTotalChunksAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var args = new AIFunctionArguments
        {
            { "directory", "." },
            { "pattern", "Match" },
            { "includes", new[] { "*.cs" } }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("match.cs", result);
        Assert.DoesNotContain("match.txt", result);
    }

    [Fact]
    public async Task SearchCodeAsync_InvalidDirectory_ReturnsGracefulError()
    {
        var sut = CreateSut();

        var args = new AIFunctionArguments
        {
            { "directory", "NonExistentDirectory" },
            { "pattern", "foo" }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("Directory not found", result);
    }

    [Fact]
    public async Task SearchCodeAsync_DirectoryWithManyFiles_NoTruncationUnder200()
    {
        var sut = CreateSut();

        for (int i = 0; i < 60; i++)
        {
            CreateTempFile($"file{i:D2}.cs", "FindMe");
        }

        _mockRepository.Setup(r => r.GetTotalChunksAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var args = new AIFunctionArguments
        {
            { "directory", "." },
            { "pattern", "FindMe" }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        var fileHits = result.Split('\n').Count(l => l.Contains("file") && l.Contains(":1:"));
        Assert.Equal(60, fileHits);
        Assert.DoesNotContain("// truncated:", result);
    }

    [Fact]
    public async Task SearchCodeAsync_FileOver10MB_Skipped()
    {
        var sut = CreateSut();

        CreateTempFile("normal.cs", "FindThis");

        var largeFilePath = Path.Combine(_testWorkspace, "large.cs");
        using (var fs = new FileStream(largeFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.SetLength(11 * 1024 * 1024);
            var bytes = System.Text.Encoding.UTF8.GetBytes("FindThis");
            fs.Write(bytes, 0, bytes.Length);
        }

        _mockRepository.Setup(r => r.GetTotalChunksAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var args = new AIFunctionArguments
        {
            { "directory", "." },
            { "pattern", "FindThis" }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("normal.cs", result);
        Assert.DoesNotContain("large.cs", result);
    }

    [Fact]
    public async Task SearchCodeAsync_UnreadableFile_IgnoredQuietly()
    {
        var sut = CreateSut();

        CreateTempFile("readable.cs", "Target");
        var lockedFile = Path.Combine(_testWorkspace, "locked.cs");

        using (var fs = new FileStream(lockedFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes("Target");
            fs.Write(bytes, 0, bytes.Length);

            _mockRepository.Setup(r => r.GetTotalChunksAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

            var args = new AIFunctionArguments
            {
                { "directory", "." },
                { "pattern", "Target" }
            };

            var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

            Assert.NotNull(result);
            Assert.Contains("readable.cs", result);
            Assert.DoesNotContain("locked.cs", result);
        }
    }

    [Fact]
    public async Task SearchCodeAsync_MissingRequiredArgs_ReturnsValidationError()
    {
        var sut = CreateSut();

        var args = new AIFunctionArguments();

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("Error: directory argument is missing", result);
    }

    [Fact]
    public async Task SearchCodeAsync_PipePattern_AutoDetectsRegex()
    {
        var sut = CreateSut();
        CreateTempFile("pipe.cs", "public class RoleService { }");
        CreateTempFile("pipe2.cs", "public class CatalogRepo { }");

        _mockRepository.Setup(r => r.GetTotalChunksAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var args = new AIFunctionArguments
        {
            { "directory", "." },
            { "pattern", "Role|catalog" }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("pipe.cs", result);
        Assert.Contains("pipe2.cs", result);
    }

    [Fact]
    public async Task SearchCodeAsync_LiteralPattern_DoesNotInterpretMetachars()
    {
        var sut = CreateSut();
        CreateTempFile("attr.cs", "[HttpGet]\npublic class C { }");
        CreateTempFile("other.cs", "HttpGet without brackets");

        _mockRepository.Setup(r => r.GetTotalChunksAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var args = new AIFunctionArguments
        {
            { "directory", "." },
            { "pattern", "[HttpGet]" }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("attr.cs", result);
        Assert.DoesNotContain("other.cs", result);
    }

    [Fact]
    public async Task SearchCodeAsync_CaseSensitive_RespectsCase()
    {
        var sut = CreateSut();
        CreateTempFile("case1.cs", "public class Upper { }");
        CreateTempFile("case2.cs", "public class upper { }");

        _mockRepository.Setup(r => r.GetTotalChunksAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var args = new AIFunctionArguments
        {
            { "directory", "." },
            { "pattern", "Upper" },
            { "caseSensitive", true }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("case1.cs", result);
        Assert.DoesNotContain("case2.cs", result);
    }

    [Fact]
    public async Task SearchCodeAsync_MatchPerLineFalse_ReturnsFilesOnly()
    {
        var sut = CreateSut();
        CreateTempFile("a.cs", "Target on line 1");
        CreateTempFile("b.cs", "Target on line 1\nTarget on line 2");

        _mockRepository.Setup(r => r.GetTotalChunksAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var args = new AIFunctionArguments
        {
            { "directory", "." },
            { "pattern", "Target" },
            { "matchPerLine", false }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("a.cs", result);
        Assert.Contains("b.cs", result);
        Assert.DoesNotContain(":1:", result);
        Assert.DoesNotContain(":2:", result);
    }

    [Fact]
    public async Task SearchCodeAsync_IncludesNegation_ExcludesBin()
    {
        var sut = CreateSut();
        CreateTempFile("src.cs", "FindMe");
        var binDir = Path.Combine(_testWorkspace, "bin");
        Directory.CreateDirectory(binDir);
        File.WriteAllText(Path.Combine(binDir, "ignored.cs"), "FindMe");

        _mockRepository.Setup(r => r.GetTotalChunksAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var args = new AIFunctionArguments
        {
            { "directory", "." },
            { "pattern", "FindMe" },
            { "includes", new[] { "*.cs", "!**/bin/**" } }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("src.cs", result);
        Assert.DoesNotContain("ignored.cs", result);
    }

    [Fact]
    public async Task SearchCodeAsync_BinObjExcludedByDefault()
    {
        var sut = CreateSut();
        CreateTempFile("good.cs", "FindMeHere");
        var binDir = Path.Combine(_testWorkspace, "bin");
        Directory.CreateDirectory(binDir);
        File.WriteAllText(Path.Combine(binDir, "bad.cs"), "FindMeHere");
        var objDir = Path.Combine(_testWorkspace, "obj");
        Directory.CreateDirectory(objDir);
        File.WriteAllText(Path.Combine(objDir, "bad2.cs"), "FindMeHere");

        _mockRepository.Setup(r => r.GetTotalChunksAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var args = new AIFunctionArguments
        {
            { "directory", "." },
            { "pattern", "FindMeHere" }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("good.cs", result);
        Assert.DoesNotContain("bad.cs", result);
        Assert.DoesNotContain("bad2.cs", result);
    }

    [Fact]
    public async Task SearchCodeAsync_RegexWithIsRegexTrue_RespectsPattern()
    {
        var sut = CreateSut();
        CreateTempFile("reg.cs", "foo123\nfoo\nfoo456bar");

        _mockRepository.Setup(r => r.GetTotalChunksAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var args = new AIFunctionArguments
        {
            { "directory", "." },
            { "pattern", "foo\\d+" },
            { "isRegex", true }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("reg.cs:1: foo123", result);
        Assert.Contains("reg.cs:3: foo456bar", result);
        Assert.DoesNotContain("reg.cs:2: foo", result);
    }

    [Fact]
    public async Task SearchCodeAsync_NoMatches_ReturnsFriendlyMessage()
    {
        var sut = CreateSut();
        CreateTempFile("x.cs", "no match here");

        _mockRepository.Setup(r => r.GetTotalChunksAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var args = new AIFunctionArguments
        {
            { "directory", "." },
            { "pattern", "ZZZZNotPresentZZZZ" }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("No matches found", result);
    }

    // ---- Sprint 1: H1 - directory is a file path ----

    [Fact]
    public async Task SearchCodeAsync_DirectoryIsFilePath_ScansThatFile()
    {
        var sut = CreateSut();
        var file = CreateTempFile("single.cs", "public class SingleFileMarker { }");

        _mockRepository.Setup(r => r.GetTotalChunksAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var args = new AIFunctionArguments
        {
            { "directory", "single.cs" },
            { "pattern", "SingleFileMarker" }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("single.cs:1: public class SingleFileMarker { }", result);
    }

    [Fact]
    public async Task SearchCodeAsync_DirectoryIsNonExistent_ReturnsError()
    {
        var sut = CreateSut();

        var args = new AIFunctionArguments
        {
            { "directory", "does-not-exist.cs" },
            { "pattern", "foo" }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("Directory not found", result);
    }

    // ---- Sprint 1: H2 - line truncation ----

    [Fact]
    public async Task SearchCodeAsync_MinifiedSingleLineFile_TruncatesOutput()
    {
        var sut = CreateSut();
        var bigLine = new string('a', 10000) + "NEEDLE" + new string('b', 10000);
        CreateTempFile("big.cs", bigLine);

        _mockRepository.Setup(r => r.GetTotalChunksAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var args = new AIFunctionArguments
        {
            { "directory", "." },
            { "pattern", "NEEDLE" }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("[line truncated]", result);
        Assert.DoesNotContain(new string('b', 100), result); // tail should be gone
    }

    // ---- Sprint 2: M2 - binary detection ----

    [Fact]
    public async Task SearchCodeAsync_BinaryFileWithNullBytes_IsSkipped()
    {
        var sut = CreateSut();
        CreateTempFile("ok.cs", "FindThisHere");
        var binPath = Path.Combine(_testWorkspace, "bin.cs");
        File.WriteAllBytes(binPath, new byte[] { 0x00, 0x01, 0x02, 0x00, (byte)'F', (byte)'i', (byte)'n', (byte)'d', 0x00 });

        _mockRepository.Setup(r => r.GetTotalChunksAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var args = new AIFunctionArguments
        {
            { "directory", "." },
            { "pattern", "Find" }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("ok.cs", result);
        Assert.DoesNotContain("bin.cs", result);
    }

    // ---- Sprint 2: M3 - empty pattern ----

    [Fact]
    public async Task SearchCodeAsync_EmptyPattern_ReturnsError()
    {
        var sut = CreateSut();

        var args = new AIFunctionArguments
        {
            { "directory", "." },
            { "pattern", "" }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("pattern cannot be empty", result);
    }

    [Fact]
    public async Task SearchCodeAsync_WhitespaceOnlyPattern_ReturnsError()
    {
        var sut = CreateSut();

        var args = new AIFunctionArguments
        {
            { "directory", "." },
            { "pattern", "   " }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("pattern cannot be empty", result);
    }

    // ---- Sprint 2: M5 - output header ----

    [Fact]
    public async Task SearchCodeAsync_OutputAlwaysStartsWithHeader()
    {
        var sut = CreateSut();
        CreateTempFile("h.cs", "MarkerText");

        _mockRepository.Setup(r => r.GetTotalChunksAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var args = new AIFunctionArguments
        {
            { "directory", "." },
            { "pattern", "MarkerText" }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        var firstLine = result.Split('\n')[0];
        Assert.StartsWith("// mode=perLine", firstLine);
        Assert.Contains("pattern='MarkerText'", firstLine);
        Assert.Contains("isRegex=False", firstLine);
        Assert.Contains("caseSensitive=False", firstLine);
    }

    // ---- Sprint 2: M6 - skipped count surfaces in header ----

    [Fact]
    public async Task SearchCodeAsync_BinaryFile_SkippedCountSurfaced()
    {
        var sut = CreateSut();
        CreateTempFile("ok.cs", "MarkerText");
        var binPath = Path.Combine(_testWorkspace, "binaryblob.cs");
        File.WriteAllBytes(binPath, new byte[8192]); // all zeros -> binary

        _mockRepository.Setup(r => r.GetTotalChunksAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var args = new AIFunctionArguments
        {
            { "directory", "." },
            { "pattern", "MarkerText" }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        var firstLine = result.Split('\n')[0];
        Assert.Contains("skipped=1", firstLine);
    }

    // ---- Sprint 2: M7 - multiple matches per line ----

    [Fact]
    public async Task SearchCodeAsync_LineWithMultipleMatches_ReportsFirstMatchOnly()
    {
        var sut = CreateSut();
        CreateTempFile("multi.cs", "MarkerText and more MarkerText here MarkerText again");

        _mockRepository.Setup(r => r.GetTotalChunksAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var args = new AIFunctionArguments
        {
            { "directory", "." },
            { "pattern", "MarkerText" }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        // Should report the line only once (one match per line)
        var occurrences = result.Split('\n').Count(l => l.Contains("multi.cs:1:"));
        Assert.Equal(1, occurrences);
    }

    // ---- Sprint 0: safety net ----

    [Fact]
    public async Task SearchCodeAsync_InternalError_ReturnsErrorString()
    {
        var sut = CreateSut();

        // Force a directory enumeration failure by mocking GetWorkspaceRootAsync to throw
        _mockVsEnv.Setup(v => v.GetWorkspaceRootAsync(It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new InvalidOperationException("synthetic failure"));

        var args = new AIFunctionArguments
        {
            { "directory", "." },
            { "pattern", "foo" }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("Error", result);
        Assert.Contains("synthetic failure", result);
    }

    // ---- Sprint 3: L2 - default includes matches dotfiles ----

    [Fact]
    public async Task SearchCodeAsync_DotfileIncluded_ByDefault()
    {
        var sut = CreateSut();
        CreateTempFile(".editorconfig", "MarkerText = yes");

        _mockRepository.Setup(r => r.GetTotalChunksAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var args = new AIFunctionArguments
        {
            { "directory", "." },
            { "pattern", "MarkerText" }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains(".editorconfig", result);
    }

    // ---- Sprint 3: L3 - segment-based exclusion (node_modules_backup not excluded) ----

    [Fact]
    public async Task SearchCodeAsync_DirNamedNodeModulesBackup_IsNotExcluded()
    {
        var sut = CreateSut();
        var keepDir = Path.Combine(_testWorkspace, "node_modules_backup");
        Directory.CreateDirectory(keepDir);
        File.WriteAllText(Path.Combine(keepDir, "kept.cs"), "MarkerText present");

        _mockRepository.Setup(r => r.GetTotalChunksAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var args = new AIFunctionArguments
        {
            { "directory", "." },
            { "pattern", "MarkerText" }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("kept.cs", result);
    }

    // ---- Sprint 1: H1 - file outside includes filter ----

    [Fact]
    public async Task SearchCodeAsync_DirectoryIsFile_RespectsIncludesFilter()
    {
        var sut = CreateSut();
        CreateTempFile("notcs.txt", "MarkerText");

        _mockRepository.Setup(r => r.GetTotalChunksAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var args = new AIFunctionArguments
        {
            { "directory", "notcs.txt" },
            { "pattern", "MarkerText" },
            { "includes", new[] { "*.cs" } }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("No matches found", result);
    }
}
