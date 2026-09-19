using System.Reflection;
using AiAssistant.Core.Pipeline;
using AiAssistant.Core.Services;

namespace ApexCode.Pipeline;

public sealed class SystemContextCollector(ISettingsService settings) : IContextCollector
{
    public Task CollectAsync(ContextStateBuilder builder, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        builder.OsPlatform = Environment.OSVersion.ToString();
        builder.AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        builder.UtcNow = DateTime.UtcNow;
        builder.UserTimeZone = TimeZoneInfo.Local.Id;
        builder.UserSettings["approvalMode"] = settings.GetToolApprovalMode("execute_command");
        builder.UserSettings["autoHealEnabled"] = settings.AutoHealEnabled.ToString();
        builder.UserSettings["embeddingIndexingEnabled"] = settings.EmbeddingIndexingEnabled.ToString();
        return Task.CompletedTask;
    }
}
