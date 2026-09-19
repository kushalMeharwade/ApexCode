using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AiAssistant.Tools.Functions
{
    public class InlineCustomAIFunction : CustomAIFunction
    {
        private readonly Func<IReadOnlyDictionary<string, object?>, Task<object?>> _invokeImpl;

        public InlineCustomAIFunction(
            string name, 
            string description, 
            string jsonSchemaString, 
            Func<IReadOnlyDictionary<string, object?>, Task<object?>> invokeImpl)
            : base(name, description, jsonSchemaString)
        {
            _invokeImpl = invokeImpl;
        }

        protected override Task<object?> InvokeCoreImplAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
        {
            return _invokeImpl(arguments);
        }
    }
}


