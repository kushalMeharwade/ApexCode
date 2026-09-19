using System.Collections.Generic;
using System.Threading.Tasks;

namespace AiAssistant.Core.Services;

public interface ITransactionSnapshotService
{
    string BeginTransaction(string workspaceRoot, Dictionary<string, string> originalContents);
    string BeginTransaction(string workspaceRoot, IEnumerable<string> filePaths);
    string BeginTransaction(string workspaceRoot, Dictionary<string, string> inMemoryContents, IEnumerable<string> onDiskFiles);
    void CompleteTransaction(string workspaceRoot, string transactionId);
    bool HasOrphanedTransactions(string workspaceRoot);
    void CleanupOrphanedTransactions(string workspaceRoot);
    Task RevertOrphanedTransactionsAsync(string workspaceRoot);
    Task RevertTransactionAsync(string workspaceRoot, string transactionId);
}
