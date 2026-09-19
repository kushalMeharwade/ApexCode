using Microsoft.Extensions.AI;
using Microsoft.Extensions.FileSystemGlobbing;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Tools.Services;
using AiAssistant.Core.Services;

using AiAssistant.Storage.Repositories;
using AiAssistant.Engine.Services;

namespace AiAssistant.Tools.Functions;

public class SearchCodeFunction : IToolProvider
{
    private readonly IVisualStudioEnvironmentService _vsEnvService;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly IEmbeddingSearchRepository _repository;
    private readonly IndexingService _indexingService;
    private readonly IOutputLogger? _outputLogger;

    public SearchCodeFunction(
        IVisualStudioEnvironmentService vsEnvService,
        IEmbeddingProvider embeddingProvider,
        IEmbeddingSearchRepository repository,
        IndexingService indexingService,
        IOutputLogger? outputLogger = null)
    {
        _vsEnvService = vsEnvService;
        _embeddingProvider = embeddingProvider;
        _repository = repository;
        _indexingService = indexingService;
        _outputLogger = outputLogger;
    }

    [Description("Searches for text in files within the workspace using a ripgrep-class engine: supports regex, negation globs, case-sensitivity, and line vs. file-only output. Hybrid: also surfaces semantic matches from the embedding index when the exact pass returns few or no hits.")]
    public AIFunction CreateFunction() => new SearchCodeCustomFunction(_vsEnvService, _embeddingProvider, _repository, _indexingService, _outputLogger);

    private class SearchCodeCustomFunction : CustomAIFunction
    {
        private const long MaxFileSizeBytes = 10 * 1024 * 1024;
        private const int TotalResultCap = 200;
        private const int PerFileResultCap = 50;
        private const int MaxLineOutputChars = 4000;
        private const int MaxLineScanChars = 102400; // 100 KB; protects regex from huge minified single-line files
        private const int BinarySniffBytes = 8192;
        private static readonly TimeSpan PerFileTimeout = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(60);
        private static readonly string[] AlwaysExcludedDirs = { "bin", "obj", ".git", "node_modules", "packages", ".vs" };

        private readonly IVisualStudioEnvironmentService _vsEnvService;
        private readonly IEmbeddingProvider _embeddingProvider;
        private readonly IEmbeddingSearchRepository _repository;
        private readonly IndexingService _indexingService;
        private readonly IOutputLogger? _outputLogger;

        public SearchCodeCustomFunction(
            IVisualStudioEnvironmentService vsEnvService,
            IEmbeddingProvider embeddingProvider,
            IEmbeddingSearchRepository repository,
            IndexingService indexingService,
            IOutputLogger outputLogger)
            : base("search_codebase",
                   "Searches for text INSIDE code files. Mirrors Antigravity's grep_search: `pattern` defaults to a case-insensitive substring, but you can pass `isRegex: true` for .NET regex. If the pattern contains an unescaped '|' and `isRegex` is omitted, alternation is auto-detected. Use `includes` (e.g. ['*.cs', '!**/bin/**']) to scope. Set `matchPerLine: false` to return file paths only. If `directory` points to a single file, that file is scanned (includes filter still applies to its name). Lines longer than 4000 chars are truncated in output. Files with NUL bytes in the first 8 KB are treated as binary and skipped. Hidden files are skipped. Falls back to the semantic embedding index when the exact pass yields few results.",
                   @"{
                         ""type"": ""object"",
                         ""properties"": {
                             ""directory"": { ""type"": ""string"", ""description"": ""The directory or file path to search in, relative to workspace root. Use forward slashes. Use '.' for the solution root. If this resolves to a file, only that file is scanned."" },
                             ""pattern"": { ""type"": ""string"", ""description"": ""Pattern to search for. Must be non-empty. Default: plain substring (case-insensitive). With isRegex: true: .NET regex. With isRegex omitted and '|' present in the pattern: auto-detected as alternation regex. If a line contains multiple matches, only the first is reported."" },
                             ""includes"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Glob patterns to include. Supports negation with leading '!'. Examples: ['*.cs', '!**/bin/**', '!**/obj/**']. Defaults to ['*'] (all files including dotfiles and extensionless files like 'Dockerfile') when omitted."" },
                             ""isRegex"": { ""type"": ""string"", ""description"": ""Treat `pattern` as a .NET regular expression. Accepts 'true'/'false' (string form for JSON compatibility); native booleans also work. Default: false. When omitted and the pattern contains an unescaped '|' outside a character class, auto-detect as alternation regex."" },
                             ""caseSensitive"": { ""type"": ""boolean"", ""description"": ""If true, match case-sensitively. Default: false."" },
                             ""matchPerLine"": { ""type"": ""boolean"", ""description"": ""If true (default), return one result per matching line as 'path:line: text'. If false, return one result per matching file as 'path'. In both modes the first line of the output is a header describing mode, pattern, scan count, hit count, and skip count."" }
                         },
                         ""required"": [""directory"", ""pattern""]
                    }")
        {
            _vsEnvService = vsEnvService;
            _embeddingProvider = embeddingProvider;
            _repository = repository;
            _indexingService = indexingService;
            _outputLogger = outputLogger;
        }

        protected override async Task<object> InvokeCoreImplAsync(IReadOnlyDictionary<string, object> arguments, CancellationToken cancellationToken)
        {
            try
            {
                if (!arguments.TryGetValue("directory", out var dirObj) || dirObj?.ToString() is not string directory)
                {
                    var receivedKeys = string.Join(", ", arguments.Keys);
                    System.Diagnostics.Debug.WriteLine($"[SearchCode] Missing 'directory'. Received keys: {receivedKeys}");
                    return $"Error: directory argument is missing. Received: {receivedKeys}";
                }

                if (!arguments.TryGetValue("pattern", out var patObj) || patObj?.ToString() is not string pattern)
                {
                    var receivedKeys = string.Join(", ", arguments.Keys);
                    System.Diagnostics.Debug.WriteLine($"[SearchCode] Missing 'pattern'. Received keys: {receivedKeys}");
                    return $"Error: pattern argument is missing. Received: {receivedKeys}";
                }

                if (string.IsNullOrWhiteSpace(pattern))
                    return "Error: pattern cannot be empty or whitespace.";

                var includes = ParseStringArray(arguments, "includes") ?? new List<string> { "*" };
                bool? isRegexExplicit = ParseBool(arguments, "isRegex");
                bool caseSensitive = ParseBool(arguments, "caseSensitive") ?? false;
                bool matchPerLine = ParseBool(arguments, "matchPerLine") ?? true;

                bool isRegex = isRegexExplicit ?? ContainsUnescapedPipe(pattern);

                _outputLogger?.Log(LogCategory.Tool, $"► {Name}({directory}, '{pattern}', isRegex={isRegex}, caseSensitive={caseSensitive}, matchPerLine={matchPerLine}, includes=[{string.Join(",", includes)}])");

                using var queryCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                queryCts.CancelAfter(QueryTimeout);
                var queryCt = queryCts.Token;

                var workspacePath = await _vsEnvService.GetWorkspaceRootAsync(cancellationToken);
                var rootDir = string.IsNullOrEmpty(workspacePath) ? "." : workspacePath;
                string targetPath;
                try
                {
                    targetPath = directory == "." ? rootDir : WorkspacePathResolver.ResolveWorkspacePath(directory, rootDir);
                }
                catch (Exception ex)
                {
                    return $"Error: Path resolution failed or traversal denied. {ex.Message}";
                }

                bool isFile;
                try
                {
                    isFile = File.Exists(targetPath);
                }
                catch
                {
                    isFile = false;
                }

                if (!isFile && !Directory.Exists(targetPath))
                    return $"Directory not found: {targetPath}";

                var matcher = BuildMatcher(includes);

                Regex regex;
                try
                {
                    var options = RegexOptions.Compiled | RegexOptions.CultureInvariant;
                    if (!caseSensitive) options |= RegexOptions.IgnoreCase;
                    var effectivePattern = isRegex ? pattern : Regex.Escape(pattern);
                    regex = new Regex(effectivePattern, options, PerFileTimeout);
                }
                catch (ArgumentException ex)
                {
                    return $"Error: Invalid regex pattern. {ex.Message}";
                }

                ExactPassResult exact;
                try
                {
                    exact = await Task.Run(() =>
                    {
                        if (isFile)
                            return RunExactPassSingleFile(targetPath, rootDir, matcher, regex, matchPerLine, queryCt);
                        return RunExactPass(targetPath, rootDir, includes, regex, matchPerLine, queryCt);
                    }, queryCt).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (queryCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    return $"Error: search timed out after {(int)QueryTimeout.TotalSeconds}s. Refine pattern or scope with `includes`.";
                }

                bool truncated = exact.Truncated;
                var sb = new StringBuilder();
                var header = BuildHeader(isRegex, pattern, caseSensitive, matchPerLine, exact);
                sb.AppendLine(header);
                foreach (var line in exact.Output)
                    sb.AppendLine(line);

                if (exact.Output.Count == 0 || exact.Output.Count < 5)
                {
                    try
                    {
                        var semantic = await RunSemanticPassAsync(pattern, targetPath, rootDir, isFile, queryCt).ConfigureAwait(false);
                        if (semantic.Count > 0)
                        {
                            sb.AppendLine("// Semantic matches:");
                            foreach (var line in semantic) sb.AppendLine(line);
                        }
                    }
                    catch (OperationCanceledException) when (queryCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        if (sb.Length > 0) sb.AppendLine();
                        sb.AppendLine($"// Error: semantic search timed out after {(int)QueryTimeout.TotalSeconds}s.");
                    }
                    catch (Exception ex)
                    {
                        _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → semantic pass skipped: {ex.Message}");
                    }
                }

                if (truncated)
                {
                    sb.AppendLine("// truncated: more results exist, refine pattern or scope (use `includes` to narrow).");
                }

                if (exact.Output.Count == 0 && sb.ToString().Split('\n').All(l => l.StartsWith("//") || string.IsNullOrWhiteSpace(l)))
                {
                    var hint = isRegex
                        ? $"No matches found. Regex '{pattern}' scanned {exact.ScannedFiles} file(s) (caseSensitive={caseSensitive})."
                        : $"No matches found. Substring '{pattern}' scanned {exact.ScannedFiles} file(s) (case-insensitive). Note: regex metacharacters (\\, *, (, [, .) are matched literally.";
                    _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → {hint}");
                    return hint;
                }

                _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → {exact.Output.Count} results across {exact.ScannedFiles} file(s), skipped={exact.SkippedFiles}");
                return sb.ToString().TrimEnd();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → Unexpected error: {ex.GetType().Name}: {ex.Message}");
                return $"Error: {ex.GetType().Name}: {ex.Message}";
            }
        }

        private static string BuildHeader(bool isRegex, string pattern, bool caseSensitive, bool matchPerLine, ExactPassResult exact)
        {
            var mode = matchPerLine ? "perLine" : "perFile";
            var pat = pattern.Replace("\r", "").Replace("\n", "\\n");
            if (pat.Length > 200) pat = pat.Substring(0, 200) + "…";
            return $"// mode={mode} pattern='{pat}' isRegex={isRegex} caseSensitive={caseSensitive} scanned={exact.ScannedFiles} hits={exact.Output.Count} skipped={exact.SkippedFiles} truncated={exact.Truncated}";
        }

        private static List<string>? ParseStringArray(IReadOnlyDictionary<string, object> arguments, string key)
        {
            if (!arguments.TryGetValue(key, out var obj) || obj is null) return null;
            if (obj is string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return null;
                return new List<string> { s };
            }
            if (obj is System.Text.Json.JsonElement elem && elem.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var item in elem.EnumerateArray())
                {
                    var v = item.ValueKind == System.Text.Json.JsonValueKind.String ? item.GetString() : item.ToString();
                    if (!string.IsNullOrWhiteSpace(v)) list.Add(v!);
                }
                return list;
            }
            if (obj is IEnumerable<object> enumerable)
            {
                var list = new List<string>();
                foreach (var item in enumerable)
                {
                    var v = item?.ToString();
                    if (!string.IsNullOrWhiteSpace(v)) list.Add(v!);
                }
                return list;
            }
            return null;
        }

        private static bool? ParseBool(IReadOnlyDictionary<string, object> arguments, string key)
        {
            if (!arguments.TryGetValue(key, out var obj) || obj is null) return null;
            if (obj is bool b) return b;
            if (obj is string s && !string.IsNullOrWhiteSpace(s))
            {
                if (bool.TryParse(s, out var parsed)) return parsed;
                if (s == "1") return true;
                if (s == "0") return false;
            }
            if (obj is System.Text.Json.JsonElement elem)
            {
                if (elem.ValueKind == System.Text.Json.JsonValueKind.True) return true;
                if (elem.ValueKind == System.Text.Json.JsonValueKind.False) return false;
                if (elem.ValueKind == System.Text.Json.JsonValueKind.String &&
                    bool.TryParse(elem.GetString(), out var parsedStr)) return parsedStr;
            }
            if (obj is IConvertible conv)
            {
                try { return conv.ToBoolean(null); } catch { return null; }
            }
            return null;
        }

        private static bool ContainsUnescapedPipe(string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return false;
            bool inClass = false;
            for (int i = 0; i < pattern.Length; i++)
            {
                char c = pattern[i];
                if (c == '\\' && i + 1 < pattern.Length) { i++; continue; }
                if (c == '[') inClass = true;
                else if (c == ']') inClass = false;
                else if (c == '|' && !inClass) return true;
            }
            return false;
        }

        private static Matcher BuildMatcher(IEnumerable<string> includes)
        {
            var matcher = new Matcher(System.StringComparison.OrdinalIgnoreCase);
            var positive = new List<string>();
            var negative = new List<string>();
            foreach (var raw in includes)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var pattern = raw.Trim();
                if (pattern.StartsWith("!"))
                {
                    var p = pattern.Substring(1).TrimStart('/');
                    if (p.Length > 0) negative.Add(p);
                }
                else
                {
                    positive.Add(pattern);
                }
            }
            if (positive.Count == 0) positive.AddRange(new[] { "*", "**/*" });
            foreach (var p in positive.Distinct()) matcher.AddInclude(p);
            foreach (var n in negative.Distinct()) matcher.AddExclude(n);
            return matcher;
        }

        private static bool IsInExcludedDir(string fullPath)
        {
            var segments = fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var frag in AlwaysExcludedDirs)
            {
                foreach (var seg in segments)
                {
                    if (seg.Equals(frag, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }

        private void EnumerateFilesWithMatcher(string dir, string rootDir, List<string> positive, List<string> negative, HashSet<string> visitedDirs, ConcurrentBag<string> output, CancellationToken ct, Action onSkip)
        {
            if (ct.IsCancellationRequested) return;
            string fullDir;
            try { fullDir = Path.GetFullPath(dir); }
            catch { return; }
            if (IsInExcludedDir(fullDir)) return;
            if (!visitedDirs.Add(fullDir)) return;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(fullDir);
            }
            catch
            {
                return;
            }

            foreach (var f in files)
            {
                if (ct.IsCancellationRequested) return;
                string full;
                try { full = Path.GetFullPath(f); }
                catch { continue; }
                if (IsInExcludedDir(full)) continue;

                var relPath = WorkspacePathResolver.ToRelativePath(full, dir).Replace('\\', '/');
                if (relPath.StartsWith("./")) relPath = relPath.Substring(2);
                if (!MatchesPatterns(relPath, positive, negative))
                {
                    continue;
                }

                try
                {
                    var fi = new FileInfo(full);
                    if (!fi.Exists || fi.Length > MaxFileSizeBytes) { onSkip(); continue; }
                }
                catch { onSkip(); continue; }

                if (IsProbablyBinary(full))
                {
                    onSkip();
                    _outputLogger?.Log(LogCategory.Tool, $"[SearchCode] Skipping binary: {WorkspacePathResolver.ToRelativePath(full, rootDir)}");
                    continue;
                }
                output.Add(full);
            }

            IEnumerable<string> subdirs;
            try { subdirs = Directory.EnumerateDirectories(fullDir); }
            catch { return; }
            foreach (var sub in subdirs)
            {
                if (ct.IsCancellationRequested) return;
                DirectoryInfo di;
                try { di = new DirectoryInfo(sub); }
                catch { continue; }
                try
                {
                    if ((di.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((di.Attributes & FileAttributes.Hidden) != 0) continue;
                }
                catch { continue; }
                EnumerateFilesWithMatcher(sub, rootDir, positive, negative, visitedDirs, output, ct, onSkip);
            }
        }

        private static bool MatchesPatterns(string relPath, List<string> positive, List<string> negative)
        {
            var matcher = new Matcher(System.StringComparison.OrdinalIgnoreCase);
            foreach (var p in positive) matcher.AddInclude(p);
            foreach (var n in negative) matcher.AddExclude(n);
            try
            {
                return matcher.Match(relPath).HasMatches;
            }
            catch
            {
                return true;
            }
        }

        private static bool IsProbablyBinary(string fullPath)
        {
            try
            {
                using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var buf = new byte[BinarySniffBytes];
                int read = fs.Read(buf, 0, buf.Length);
                for (int i = 0; i < read; i++)
                {
                    if (buf[i] == 0) return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsFileAllowedByMatcher(string fullPath, string basePath, Matcher matcher)
        {
            try
            {
                var dirInfo = new DirectoryInfo(Path.GetDirectoryName(fullPath) ?? basePath);
                var wrapper = new Microsoft.Extensions.FileSystemGlobbing.Abstractions.DirectoryInfoWrapper(dirInfo);
                var matchResult = matcher.Execute(wrapper);
                foreach (var m in matchResult.Files)
                {
                    var matchedFull = Path.GetFullPath(Path.Combine(dirInfo.FullName, m.Path));
                    if (string.Equals(matchedFull, fullPath, StringComparison.OrdinalIgnoreCase)) return true;
                }
                return false;
            }
            catch
            {
                return true; // fail open to preserve previous behavior on matcher errors
            }
        }

        private static string TruncateForOutput(string line)
        {
            if (line.Length <= MaxLineOutputChars) return line.TrimEnd();
            return line.Substring(0, MaxLineOutputChars) + " …[line truncated]";
        }

        private sealed class ExactPassResult
        {
            public List<string> Output { get; set; } = new List<string>();
            public int ScannedFiles { get; set; }
            public int SkippedFiles { get; set; }
            public bool Truncated { get; set; }
        }

        private ExactPassResult RunExactPass(string targetDir, string rootDir, IEnumerable<string> includes, Regex regex, bool matchPerLine, CancellationToken ct)
        {
            var result = new ExactPassResult();
            int totalMatches = 0;
            bool truncated = false;
            int skipped = 0;

            var positive = new List<string>();
            var negative = new List<string>();
            foreach (var raw in includes)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var pattern = raw.Trim();
                if (pattern.StartsWith("!"))
                {
                    var p = pattern.Substring(1).TrimStart('/');
                    if (p.Length > 0) negative.Add(p);
                }
                else
                {
                    positive.Add(pattern);
                }
            }
            if (positive.Count == 0) positive.Add("*");

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var filesBag = new ConcurrentBag<string>();
            try
            {
                EnumerateFilesWithMatcher(targetDir, rootDir, positive, negative, visited, filesBag, ct, () => Interlocked.Increment(ref skipped));
            }
            catch (Exception ex)
            {
                _outputLogger?.Log(LogCategory.Tool, $"[SearchCode] Enumeration error: {ex.Message}");
            }

            var filesArr = filesBag.ToArray();
            Array.Sort(filesArr, System.StringComparer.OrdinalIgnoreCase);
            result.ScannedFiles = filesArr.Length;

            var matchLock = new object();
            var perFileFiles = new ConcurrentDictionary<string, bool>();

            try
            {
                Parallel.ForEach(filesArr, new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, 8)),
                    CancellationToken = ct
                }, file =>
                {
                    int fileMatches = 0;
                    bool fileHadMatch = false;
                    try
                    {
                        var rel = WorkspacePathResolver.ToRelativePath(file, rootDir);
                        int lineNo = 0;
                        foreach (var rawLine in File.ReadLines(file))
                        {
                            lineNo++;
                            var text = rawLine.Length > MaxLineScanChars ? rawLine.Substring(0, MaxLineScanChars) : rawLine;
                            Match? m;
                            try { m = regex.Match(text); }
                            catch (RegexMatchTimeoutException) { return; }
                            if (m.Success)
                            {
                                fileHadMatch = true;
                                fileMatches++;
                                lock (matchLock)
                                {
                                    if (matchPerLine && totalMatches < TotalResultCap)
                                    {
                                        result.Output.Add($"{rel}:{lineNo}: {TruncateForOutput(rawLine)}");
                                        totalMatches++;
                                    }
                                }
                                if (fileMatches >= PerFileResultCap) break;
                            }
                        }
                        if (!matchPerLine && fileHadMatch)
                        {
                            perFileFiles[rel] = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref skipped);
                        System.Diagnostics.Debug.WriteLine($"[SearchCode] Read error on {file}: {ex.Message}");
                    }
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            if (!matchPerLine)
            {
                var paths = perFileFiles.Keys.OrderBy(p => p, System.StringComparer.OrdinalIgnoreCase).ToList();
                if (paths.Count > TotalResultCap)
                {
                    truncated = true;
                    paths = paths.Take(TotalResultCap).ToList();
                }
                result.Output.AddRange(paths);
            }
            else if (totalMatches >= TotalResultCap)
            {
                truncated = true;
            }

            result.Truncated = truncated;
            result.SkippedFiles = skipped;
            return result;
        }

        private ExactPassResult RunExactPassSingleFile(string targetFile, string rootDir, Matcher matcher, Regex regex, bool matchPerLine, CancellationToken ct)
        {
            var result = new ExactPassResult { ScannedFiles = 1 };
            try
            {
                var fi = new FileInfo(targetFile);
                if (!fi.Exists || fi.Length > MaxFileSizeBytes)
                {
                    result.SkippedFiles = 1;
                    return result;
                }
                if (IsInExcludedDir(targetFile))
                {
                    result.SkippedFiles = 1;
                    return result;
                }
                // Respect include/exclude filters by checking against the matcher's include patterns.
                if (!IsFileAllowedByMatcher(targetFile, targetFile, matcher))
                {
                    result.SkippedFiles = 1;
                    return result;
                }
                if (IsProbablyBinary(targetFile))
                {
                    result.SkippedFiles = 1;
                    _outputLogger?.Log(LogCategory.Tool, $"[SearchCode] Skipping binary: {WorkspacePathResolver.ToRelativePath(targetFile, rootDir)}");
                    return result;
                }

                var rel = WorkspacePathResolver.ToRelativePath(targetFile, rootDir);
                int lineNo = 0;
                int totalMatches = 0;
                bool fileHadMatch = false;
                foreach (var rawLine in File.ReadLines(targetFile))
                {
                    if (ct.IsCancellationRequested) break;
                    lineNo++;
                    var text = rawLine.Length > MaxLineScanChars ? rawLine.Substring(0, MaxLineScanChars) : rawLine;
                    Match? m;
                    try { m = regex.Match(text); }
                    catch (RegexMatchTimeoutException) { break; }
                    if (m.Success)
                    {
                        fileHadMatch = true;
                        if (matchPerLine && totalMatches < TotalResultCap)
                        {
                            result.Output.Add($"{rel}:{lineNo}: {TruncateForOutput(rawLine)}");
                            totalMatches++;
                        }
                        if (!matchPerLine) break;
                    }
                }
                if (!matchPerLine && fileHadMatch)
                {
                    result.Output.Add(rel);
                }
                if (totalMatches >= TotalResultCap) result.Truncated = true;
            }
            catch (Exception ex)
            {
                result.SkippedFiles = 1;
                System.Diagnostics.Debug.WriteLine($"[SearchCode] Single-file read error: {ex.Message}");
            }
            return result;
        }

        private async Task<List<string>> RunSemanticPassAsync(string pattern, string targetPath, string rootDir, bool isFile, CancellationToken cancellationToken)
        {
            var output = new List<string>();
            var totalChunks = await _repository.GetTotalChunksAsync().ConfigureAwait(false);
            if (totalChunks <= 0) return output;

            var queryEmbedding = await _embeddingProvider.EmbedQueryAsync(pattern, cancellationToken).ConfigureAwait(false);
            var semanticResults = await _repository.SearchAsync(queryEmbedding, limit: 20, cancellationToken).ConfigureAwait(false);

            foreach (var res in semanticResults)
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (isFile)
                {
                    if (!string.Equals(res.Chunk.FilePath, targetPath, StringComparison.OrdinalIgnoreCase)) continue;
                }
                else if (!res.Chunk.FilePath.StartsWith(targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var rel = WorkspacePathResolver.ToRelativePath(res.Chunk.FilePath, rootDir);
                var header = string.IsNullOrEmpty(res.Chunk.SymbolName) ? "" : $"// Symbol: {res.Chunk.SymbolName}\n";
                var snippet = string.Join("\n", res.Chunk.ChunkText.Split('\n').Take(5));
                if (snippet.Length > MaxLineOutputChars) snippet = snippet.Substring(0, MaxLineOutputChars) + " …[line truncated]";
                output.Add($"{header}{rel}:{res.Chunk.StartLine}:\n{snippet}\n// Relevance: {(1 - res.Distance) * 100:F1}%");
            }
            return output;
        }
    }
}
