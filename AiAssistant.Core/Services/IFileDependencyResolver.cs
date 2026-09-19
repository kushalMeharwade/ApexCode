using System.Collections.Generic;
using System.Threading.Tasks;

namespace AiAssistant.Core.Services;

public interface IFileDependencyResolver
{
    Task<IEnumerable<string>> ResolveDependenciesAsync(IEnumerable<string> sourceFiles);
}
