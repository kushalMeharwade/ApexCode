using Microsoft.Extensions.AI;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AiAssistant.Tools.Functions
{
    /// <summary>
    /// A custom implementation of AIFunction that manually parses its JSON schema.
    /// This bypasses AIFunctionFactory.Create(), which relies on System.Text.Json reflection 
    /// that fails due to version mismatches (DLL Hell) in the Visual Studio AppDomain.
    /// </summary>
    public abstract class CustomAIFunction : AIFunction, AiAssistant.Tools.Services.IToolProvider
    {
        public AIFunction CreateFunction() => this;
        private readonly string _name;
        private readonly string _description;
        private readonly JsonElement _jsonSchema;

        public override string Name => _name;
        public override string Description => _description;
        public override JsonElement JsonSchema => _jsonSchema;

        protected CustomAIFunction(string name, string description, string jsonSchemaString)
        {
            _name = name;
            _description = description;
            _jsonSchema = JsonDocument.Parse(jsonSchemaString).RootElement;
        }

        // MEAI 10.x changed the parameter type from IEnumerable<KeyValuePair<string, object?>>
        // to AIFunctionArguments, which implements IDictionary<string, object?>.
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (arguments != null)
            {
                foreach (var arg in arguments)
                {
                    dict[arg.Key] = arg.Value;
                }
            }

            try 
            {
                return await InvokeCoreImplAsync(dict, cancellationToken);
            }
            catch (InvalidOperationException ex) when (ex.Message == "No workspace is open.")
            {
                return "Error: No workspace is open.";
            }
        }

        /// <summary>
        /// Implement this method to handle the actual tool invocation.
        /// </summary>
        protected abstract Task<object?> InvokeCoreImplAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken);
    }
}


