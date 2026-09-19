#nullable enable
using System.Collections.Generic;

namespace AiAssistant.Core.Models;

public record ToolCallInfo(string CallId, string ToolName, IDictionary<string, object?>? Arguments);

