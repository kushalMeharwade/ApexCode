using Microsoft.Extensions.AI;

namespace AiAssistant.Tools.Services;

public interface IToolProvider
{
    AIFunction CreateFunction();
}
