using System;

namespace AiAssistant.Core.Services;

public class ProviderChangeNotifier : IProviderChangeNotifier
{
    public event EventHandler? ProvidersChanged;
    
    public void NotifyProvidersChanged()
    {
        ProvidersChanged?.Invoke(this, EventArgs.Empty);
    }
}
