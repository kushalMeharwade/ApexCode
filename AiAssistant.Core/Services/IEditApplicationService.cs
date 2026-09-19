using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Core.Models;

namespace AiAssistant.Core.Services
{
    public interface IEditApplicationService
    {
        Task<EditResult> ApplyEditsAsync(EditIntent intent, object preEditState, CancellationToken ct = default);
    }
}
