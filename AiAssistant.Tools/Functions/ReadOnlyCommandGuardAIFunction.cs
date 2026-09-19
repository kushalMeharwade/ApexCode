using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace AiAssistant.Tools.Functions
{
    /// <summary>
    /// Decorates the execute_command tool so that, for the lifetime of a single request, it can only be used for
    /// read-only work.
    /// </summary>
    public sealed class ReadOnlyCommandGuardAIFunction : AIFunction
    {
        private static readonly Regex RestrictedOperators = new Regex(
            @"(>|>>|&|;|\|\|)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex DestructiveCommands = new Regex(
            @"\b(Set-|New-|Remove-|Add-|Clear-|Enable-|Disable-|Update-|Stop-|Restart-|Start-|Register-|Unregister-|Move-|Rename-|Copy-|Out-File|Set-Content|Add-Content|del|erase|rmdir|rd|mkdir|md|copy|xcopy|robocopy|move|ren|rename|format|diskpart|mklink|Invoke-WebRequest|Invoke-RestMethod|iwr|irm)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly HashSet<string> AllowedCommandRoots = new(StringComparer.OrdinalIgnoreCase)
        {
            "git", "grep", "dir", "ls", "find", "findstr", "cat", "type", "echo", "wc", "tree", "rg", "ag", "ack", "powershell", "pwsh", "cmd"
        };

        private readonly AIFunction _inner;
        private readonly string _rejectionMessage;
        private readonly string _commandArgumentName;

        public ReadOnlyCommandGuardAIFunction(AIFunction inner, string rejectionMessage, string commandArgumentName = "command")
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _rejectionMessage = rejectionMessage ?? throw new ArgumentNullException(nameof(rejectionMessage));
            _commandArgumentName = commandArgumentName ?? throw new ArgumentNullException(nameof(commandArgumentName));
        }

        public override string Name => _inner.Name;

        public override string Description => _inner.Description;

        public override JsonElement JsonSchema => _inner.JsonSchema;

        public static bool IsReadOnlyCommand(string? command)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;
            var cmd = command!.Trim();

            // Block redirections and chaining
            if (RestrictedOperators.IsMatch(cmd)) return false;

            // Block known modifying commands within the string (acting as a blacklist)
            if (DestructiveCommands.IsMatch(cmd)) return false;

            // Allow known read-only base commands (and shells, relying on the blacklist above)
            var firstWord = cmd.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (firstWord == null) return false;

            return AllowedCommandRoots.Contains(firstWord);
        }

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            string? command = null;
            if (arguments != null && arguments.TryGetValue(_commandArgumentName, out var cmdObj))
            {
                command = cmdObj?.ToString();
            }

            if (!IsReadOnlyCommand(command))
            {
                return _rejectionMessage;
            }

            return await _inner.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
        }
    }
}


