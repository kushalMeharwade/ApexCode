using System;
using System.IO;
using System.Threading.Tasks;
using AiAssistant.Engine.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiAssistant.Tests
{
    public class CheckpointGitOperationsTests : IDisposable
    {
        private readonly string _testWorkspace;
        private readonly string _shadowGitDir;
        private readonly CheckpointGitOperations _ops;

        public CheckpointGitOperationsTests()
        {
            _testWorkspace = Path.Combine(Path.GetTempPath(), "AiAssistantTests", Guid.NewGuid().ToString());
            _shadowGitDir = Path.Combine(_testWorkspace, ".shadow_git");
            Directory.CreateDirectory(_testWorkspace);
            _ops = new CheckpointGitOperations(NullLogger.Instance);
        }

        [Fact]
        public async Task Test_ShadowGitOperations_EndToEnd()
        {
            // 1. Check Git available
            bool isGit = await _ops.IsGitAvailableAsync();
            Assert.True(isGit, "Git should be available on the system");

            // 2. Init Shadow Repo
            await _ops.InitShadowRepoAsync(_shadowGitDir, _testWorkspace);
            Assert.True(Directory.Exists(_shadowGitDir), "Shadow git dir should be created");

            // 3. Create a file and commit
            string testFilePath = Path.Combine(_testWorkspace, "test.txt");
            File.WriteAllText(testFilePath, "Hello, Checkpoints!");

            string hash1 = await _ops.AddAndCommitAsync(_shadowGitDir, _testWorkspace, "First commit");
            Assert.False(string.IsNullOrEmpty(hash1), "Should return a commit hash");

            // 4. Modify file and commit
            File.WriteAllText(testFilePath, "Hello, Modified Checkpoints!");
            string hash2 = await _ops.AddAndCommitAsync(_shadowGitDir, _testWorkspace, "Second commit");
            Assert.False(string.IsNullOrEmpty(hash2), "Should return a second commit hash");

            // 5. Get Diff
            var diffs = await _ops.GetDiffSummaryAsync(_shadowGitDir, _testWorkspace, hash1, hash2);
            Assert.Single(diffs);
            Assert.Equal("modified", diffs[0].ChangeType);

            // Also cover a newly created file that has not been committed yet.
            string laterFilePath = Path.Combine(_testWorkspace, "test2.txt");
            File.WriteAllText(laterFilePath, "Created after the checkpoint");

            // 6. Reset to first commit
            await _ops.ResetHardAsync(_shadowGitDir, _testWorkspace, hash1);
            string restoredContent = File.ReadAllText(testFilePath);
            Assert.Equal("Hello, Checkpoints!", restoredContent);
            Assert.False(File.Exists(laterFilePath));
        }

        [Theory]
        [InlineData(".vs")]
        [InlineData(".VS")]
        public async Task RestorePreservesLockedVisualStudioStateAndRemovesNewUserFiles(string ideDirectory)
        {
            await _ops.InitShadowRepoAsync(_shadowGitDir, _testWorkspace);
            var first = Path.Combine(_testWorkspace, "test1.txt");
            File.WriteAllText(first, "original");
            var checkpoint = await _ops.AddAndCommitAsync(_shadowGitDir, _testWorkspace, "before test2");
            var cache = Path.Combine(_testWorkspace, ideDirectory, "test_folder.slnx", "FileContentIndex", "locked.vsidx");
            Directory.CreateDirectory(Path.GetDirectoryName(cache));
            File.WriteAllText(cache, "IDE state");
            var second = Path.Combine(_testWorkspace, "test2.txt");
            File.WriteAllText(second, "new file");
            using (File.Open(cache, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await _ops.AddAndCommitAsync(_shadowGitDir, _testWorkspace, "after test2");
                await _ops.ResetHardAsync(_shadowGitDir, _testWorkspace, checkpoint);
                Assert.False(File.Exists(second));
                Assert.Equal("original", File.ReadAllText(first));
            }
            Assert.Equal("IDE state", File.ReadAllText(cache));
        }

        [Fact]
        public async Task RestoreOfLegacyCheckpointDoesNotOverwriteTrackedIdeFiles()
        {
            await _ops.InitShadowRepoAsync(_shadowGitDir, _testWorkspace);
            var cache = Path.Combine(_testWorkspace, "nested", ".vs", "state.dat");
            Directory.CreateDirectory(Path.GetDirectoryName(cache));
            File.WriteAllText(cache, "old IDE state");
            var first = Path.Combine(_testWorkspace, "test1.txt");
            File.WriteAllText(first, "original");
            await _ops.AddAndCommitAsync(_shadowGitDir, _testWorkspace, "initial");
            // Simulate a checkpoint created by the version that tracked .vs files.
            await RunGitAsync("add -f -- nested/.vs/state.dat");
            await RunGitAsync("commit -m legacy");
            var checkpoint = (await RunGitAsync("rev-parse HEAD")).Trim();
            File.WriteAllText(cache, "current IDE state");
            File.WriteAllText(first, "changed");
            using (File.Open(cache, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                await _ops.ResetHardAsync(_shadowGitDir, _testWorkspace, checkpoint);
            Assert.Equal("current IDE state", File.ReadAllText(cache));
            Assert.Equal("original", File.ReadAllText(first));
            Assert.DoesNotContain(".vs", await RunGitAsync("ls-files"));
        }

        [Fact]
        public async Task RestoreEmptyCheckpointPreservesIdeFilesAndExistingCustomExcludes()
        {
            await _ops.InitShadowRepoAsync(_shadowGitDir, _testWorkspace);
            var checkpoint = await _ops.AddAndCommitAsync(_shadowGitDir, _testWorkspace, "empty");
            // Existing shadow repos must be upgraded at restore time, not just initialization.
            File.WriteAllText(Path.Combine(_shadowGitDir, "info", "exclude"), ".shadow_git/\ncustom-cache/\n");
            var cache = Path.Combine(_testWorkspace, ".vs", "state.dat");
            Directory.CreateDirectory(Path.GetDirectoryName(cache));
            File.WriteAllText(cache, "keep");
            var second = Path.Combine(_testWorkspace, "test2.txt");
            File.WriteAllText(second, "remove");
            await _ops.ResetHardAsync(_shadowGitDir, _testWorkspace, checkpoint);
            Assert.False(File.Exists(second));
            Assert.Equal("keep", File.ReadAllText(cache));
            Assert.Contains("custom-cache/", File.ReadAllText(Path.Combine(_shadowGitDir, "info", "exclude")));
        }

        private async Task<string> RunGitAsync(string arguments)
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"--git-dir=\"{_shadowGitDir}\" --work-tree=\"{_testWorkspace}\" " + arguments,
                WorkingDirectory = _testWorkspace,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            });
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await Task.Run(() => process.WaitForExit());
            Assert.True(process.ExitCode == 0, await error);
            return await output;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_testWorkspace))
                {
                    // Remove read-only attributes from git files before deleting
                    foreach (var file in Directory.EnumerateFiles(_testWorkspace, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                    Directory.Delete(_testWorkspace, true);
                }
            }
            catch { }
        }
    }
}
