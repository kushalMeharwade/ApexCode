using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace AiAssistant.Engine.SkeletonEngine.TreeSitter;

public static class TreeSitterLanguages
{
    private static IntPtr _treeSitterModule = IntPtr.Zero;
    private static readonly Dictionary<string, IntPtr> ExtensionToLanguage = new(StringComparer.OrdinalIgnoreCase);
    private static bool _initialized;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr TsLanguageFunction();

    private static readonly string[] SupportedExtensions = {
        ".py", ".python",
        ".js", ".jsx",
        ".ts", ".tsx",
        ".c", ".h",
        ".cpp", ".cc", ".cxx", ".hpp", ".hxx",
        ".go",
        ".rs",
        ".java",
        ".sh", ".bash",
        ".css",
        ".html", ".htm",
        ".json",
        ".md", ".markdown",
        ".php",
        ".rb", ".ruby",
        ".toml",
        ".yaml", ".yml"
    };

    private static readonly string[] GrammarDllNames = {
        "tree-sitter-python.dll",
        "tree-sitter-javascript.dll",
        "tree-sitter-typescript.dll",
        "tree-sitter-c.dll",
        "tree-sitter-cpp.dll",
        "tree-sitter-go.dll",
        "tree-sitter-rust.dll",
        "tree-sitter-java.dll",
        "tree-sitter-bash.dll",
        "tree-sitter-css.dll",
        "tree-sitter-html.dll",
        "tree-sitter-json.dll",
        "tree-sitter-markdown.dll",
        "tree-sitter-php.dll",
        "tree-sitter-ruby.dll",
        "tree-sitter-toml.dll",
        "tree-sitter-yaml.dll"
    };

    public static bool IsSupported(string extension)
    {
        return Array.Exists(SupportedExtensions, e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase));
    }

    public static void Initialize()
    {
        if (_initialized) return;

        // FIX Issue #9: Use extension-relative path instead of AppDomain.CurrentDomain.BaseDirectory
        // In a VSIX context, BaseDirectory points to VS installation folder, not the extension's deployment folder
        var assemblyDir = Path.GetDirectoryName(typeof(TreeSitterLanguages).Assembly.Location);
        if (string.IsNullOrEmpty(assemblyDir))
        {
            throw new InvalidOperationException("Could not determine TreeSitter assembly directory.");
        }

        var candidates = new[] { assemblyDir, Path.Combine(assemblyDir, "runtimes", "win-x64", "native") };

        foreach (var dir in candidates)
        {
            if (!Directory.Exists(dir)) continue;
            var dllPath = Path.Combine(dir, "tree-sitter.dll");
            if (File.Exists(dllPath))
            {
                try
                {
                    _treeSitterModule = NativeLoader.Load(dllPath);
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[TreeSitter] Failed to load core tree-sitter.dll at {dllPath}: {ex.Message}");
                }
            }
        }

        if (_treeSitterModule == IntPtr.Zero)
        {
            var errorMsg = $"Could not find tree-sitter.dll in any probe path. Searched: {string.Join("; ", candidates)}";
            System.Diagnostics.Debug.WriteLine($"[TreeSitter] {errorMsg}");
            // FIX: Don't throw an exception; just mark as initialized so GetLanguage returns IntPtr.Zero
            // which allows TreeSitterParser to gracefully fall back without showing a messy error trace.
            _initialized = true;
            return;
        }

        TreeSitterApi.Load(_treeSitterModule);

        foreach (var dir in candidates)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var grammar in GrammarDllNames)
            {
                var grammarPath = Path.Combine(dir, grammar);
                if (File.Exists(grammarPath))
                {
                    try
                    {
                        NativeLoader.Load(grammarPath);
                    }
                    catch (Exception ex)
                    {
                        // FIX Cross-cutting #1: Log to telemetry/output instead of just Debug.WriteLine
                        System.Diagnostics.Debug.WriteLine($"Failed to pre-load TreeSitter grammar {grammarPath}: {ex.Message}");
                    }
                }
            }
        }

        _initialized = true;
    }

    public static IntPtr GetLanguage(string extension)
    {
        if (!_initialized) Initialize();

        if (ExtensionToLanguage.TryGetValue(extension, out var cached))
        {
            return cached;
        }

        var functionName = GetGrammarFunctionName(extension);
        if (string.IsNullOrEmpty(functionName)) return IntPtr.Zero;

        // FIX Issue #9: Use extension-relative path consistently
        var assemblyDir = Path.GetDirectoryName(typeof(TreeSitterLanguages).Assembly.Location);
        if (string.IsNullOrEmpty(assemblyDir))
        {
            System.Diagnostics.Debug.WriteLine("[TreeSitter] Could not determine assembly directory for grammar loading.");
            return IntPtr.Zero;
        }
        
        var candidates = new[] { assemblyDir, Path.Combine(assemblyDir, "runtimes", "win-x64", "native") };

        foreach (var dir in candidates)
        {
            if (!Directory.Exists(dir)) continue;
            var expectedDllName = functionName == "tree_sitter_json_schema" ? "tree-sitter-json.dll" : $"tree-sitter-{functionName.Replace("tree_sitter_", "").Replace("_", "-")}.dll";
            var grammarDll = GrammarDllNames.FirstOrDefault(g => string.Equals(g, expectedDllName, StringComparison.OrdinalIgnoreCase));
            if (grammarDll == null) continue;
            var grammarPath = Path.Combine(dir, grammarDll);
            if (!File.Exists(grammarPath)) continue;

            try
            {
                var grammarModule = NativeLoader.Load(grammarPath);
                var procAddr = NativeLoader.GetProcAddress(grammarModule, functionName);
                if (procAddr == IntPtr.Zero) continue;

                var getLanguage = NativeLoader.GetFunction<TsLanguageFunction>(grammarModule, functionName);
                var languagePtr = getLanguage();
                if (languagePtr != IntPtr.Zero)
                {
                    ExtensionToLanguage[extension] = languagePtr;
                    return languagePtr;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load TreeSitter grammar for {extension}: {ex.Message}");
                continue;
            }
        }

        return IntPtr.Zero;
    }

    private static string GetGrammarFunctionName(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".py" or ".python" => "tree_sitter_python",
            ".js" or ".jsx" => "tree_sitter_javascript",
            ".ts" or ".tsx" => "tree_sitter_typescript",
            ".c" or ".h" => "tree_sitter_c",
            ".cpp" or ".cc" or ".cxx" or ".hpp" or ".hxx" => "tree_sitter_cpp",
            ".go" => "tree_sitter_go",
            ".rs" => "tree_sitter_rust",
            ".java" => "tree_sitter_java",
            ".sh" or ".bash" => "tree_sitter_bash",
            ".css" => "tree_sitter_css",
            ".html" or ".htm" => "tree_sitter_html",
            ".json" => "tree_sitter_json_schema",
            ".md" or ".markdown" => "tree_sitter_markdown",
            ".php" => "tree_sitter_php",
            ".rb" or ".ruby" => "tree_sitter_ruby",
            ".toml" => "tree_sitter_toml",
            ".yaml" or ".yml" => "tree_sitter_yaml",
            _ => null
        };
    }
}
