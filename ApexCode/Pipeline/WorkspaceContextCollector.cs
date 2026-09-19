using AiAssistant.Core.Pipeline;
using AiAssistant.Core.Services;

namespace ApexCode.Pipeline;

public sealed class WorkspaceContextCollector(IVisualStudioEnvironmentService workspace) : IContextCollector
{
    public async Task CollectAsync(ContextStateBuilder builder, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        builder.ActiveWorkspacePath = await workspace.GetWorkspaceRootAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var openFiles = await workspace.GetOpenDocumentPathsAsync().ConfigureAwait(false);
        foreach (var file in openFiles.Where(path => !string.IsNullOrWhiteSpace(path)))
            builder.OpenFiles.Add(file);
    }
}

