using System;

namespace AiAssistant.Core.Services;

public interface IProviderChangeNotifier
{
    event EventHandler ProvidersChanged;
    void NotifyProvidersChanged();
}
