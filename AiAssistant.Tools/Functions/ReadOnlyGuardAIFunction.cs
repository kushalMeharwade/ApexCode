using Microsoft.Extensions.AI;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Core.Services;

namespace AiAssistant.Tools.Functions
{
    /// <summary>
    /// Decorates a tool so that, for the lifetime of a single request, it can only be used for
    /// read-only work.
    /// </summary>
    /// <remarks>
    /// Plan mode removes the always-mutating tools from the request outright, but a tool like
    /// <c>execute_query</c> is genuinely useful while investigating and only mutates depending on
    /// the SQL it is handed. Dropping it would blind the planner on database work; leaving it
    /// unguarded would let Plan mode write. This wrapper keeps the tool visible to the model and
    /// rejects the mutating calls at the boundary, with a message that tells the model what to do
    /// instead rather than just failing.
    /// </remarks>
    public sealed class ReadOnlyGuardAIFunction : AIFunction
    {
        private readonly AIFunction _inner;
        private readonly string[] _guardedArguments;
        private readonly string _rejectionMessage;

        /// <param name="inner">The tool being guarded.</param>
        /// <param name="rejectionMessage">Returned to the model when a mutating call is blocked.</param>
        /// <param name="guardedArguments">
        /// Candidate argument names holding the statement to inspect. Several are accepted because
        /// the guard has to keep working if the wrapped tool renames its parameter — the guard fails
        /// closed, so a name mismatch would otherwise silently block every call.
        /// </param>
        public ReadOnlyGuardAIFunction(AIFunction inner, string rejectionMessage, params string[] guardedArguments)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _rejectionMessage = rejectionMessage ?? throw new ArgumentNullException(nameof(rejectionMessage));

            if (guardedArguments == null || guardedArguments.Length == 0)
                throw new ArgumentException("At least one guarded argument name is required.", nameof(guardedArguments));

            _guardedArguments = guardedArguments;
        }

        public override string Name => _inner.Name;

        public override string Description => _inner.Description;

        public override JsonElement JsonSchema => _inner.JsonSchema;

        /// <summary>
        /// True when <paramref name="statement"/> only reads. Anything that cannot be positively
        /// identified as a read is treated as a write, so an unparseable or unfamiliar statement
        /// fails closed.
        /// </summary>
        public static bool IsReadOnlyStatement(string? statement)
        {
            return DatabaseQueryPolicy.IsReadOnly(statement);
        }

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            string? statement = null;
            if (arguments != null)
            {
                foreach (KeyValuePair<string, object?> argument in arguments)
                {
                    bool isGuarded = false;
                    foreach (var name in _guardedArguments)
                    {
                        if (string.Equals(argument.Key, name, StringComparison.OrdinalIgnoreCase))
                        {
                            isGuarded = true;
                            break;
                        }
                    }

                    if (isGuarded)
                    {
                        statement = argument.Value?.ToString();
                        break;
                    }
                }
            }

            if (!IsReadOnlyStatement(statement))
            {
                return _rejectionMessage;
            }

            return await _inner.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
        }

    }
}


