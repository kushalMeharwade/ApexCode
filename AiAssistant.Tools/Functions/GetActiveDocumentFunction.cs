using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using AiAssistant.Tools.Services;
using AiAssistant.Core.Services;
using Microsoft.Extensions.AI;

namespace AiAssistant.Tools.Functions;

public class GetActiveDocumentFunction : IToolProvider
{
    private readonly IVisualStudioEnvironmentService _vsEnvService;
    private readonly IOutputLogger? _outputLogger;

    private readonly IFileReadTracker _fileReadTracker;

    public GetActiveDocumentFunction(IVisualStudioEnvironmentService vsEnvService, IFileReadTracker fileReadTracker, IOutputLogger? outputLogger = null)
    {
        _vsEnvService = vsEnvService;
        _fileReadTracker = fileReadTracker;
        _outputLogger = outputLogger;
    }

    [Description("Gets the absolute path of the currently active (focused) document in the editor.")]
    public AIFunction CreateFunction() => new GetActiveDocumentCustomFunction(_vsEnvService, _fileReadTracker, _outputLogger);

    private class GetActiveDocumentCustomFunction : CustomAIFunction
    {
        private readonly IVisualStudioEnvironmentService _vsEnvService;
        private readonly IFileReadTracker _fileReadTracker;
        private readonly IOutputLogger? _outputLogger;

        public GetActiveDocumentCustomFunction(IVisualStudioEnvironmentService vsEnvService, IFileReadTracker fileReadTracker, IOutputLogger? outputLogger)
            : base("get_active_document",
                   "Gets the absolute path of the currently active (focused) document in the Visual Studio editor.",
                   @"{
                        ""type"": ""object"",
                        ""properties"": {}
                   }")
        {
            _vsEnvService = vsEnvService;
            _fileReadTracker = fileReadTracker;
            _outputLogger = outputLogger;
        }

        protected override async Task<object?> InvokeCoreImplAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
        {
            _outputLogger?.Log(LogCategory.Tool, $"► {Name}()");

            try
            {
                var activeDoc = await _vsEnvService.GetActiveDocumentAsync();
                
                if (string.IsNullOrEmpty(activeDoc))
                {
                    _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → No active document found.");
                    return "No document is currently active or focused.";
                }

                _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → {activeDoc}");
                _fileReadTracker.MarkFileRead(activeDoc, DateTime.UtcNow);
                return activeDoc;
            }
            catch (Exception ex)
            {
                var errMsg = $"Error getting active document: {ex.Message}";
                _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → {errMsg}");
                return errMsg;
            }
        }
    }
}


