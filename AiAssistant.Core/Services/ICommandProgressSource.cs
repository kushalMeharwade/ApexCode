using System;

namespace AiAssistant.Core.Services;

public interface ICommandProgressSource
{
    event EventHandler<CommandProgress>? CommandProgress;
}

public sealed class CommandProgress : EventArgs
{
    public string Owner { get; set; } = "";
    public string CallId { get; set; } = "";
    public string Result { get; set; } = "";
}
