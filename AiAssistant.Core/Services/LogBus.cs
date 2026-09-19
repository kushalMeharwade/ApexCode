using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;

namespace AiAssistant.Core.Services;

public class LogBus : ILogBus
{
    public event EventHandler<LogEntry>? EntryPublished;
    public event EventHandler<LlmTransaction>? TransactionPublished;

    private readonly ConcurrentQueue<LogEntry> _history = new();
    private readonly ConcurrentQueue<LlmTransaction> _transactions = new();
    private const int MaxHistory = 1000;
    private const int MaxTransactions = 200;

    public IReadOnlyList<LogEntry> History => _history.ToArray();
    public IReadOnlyList<LlmTransaction> Transactions => _transactions.ToArray();

    public void Publish(LogEntry entry)
    {
        _history.Enqueue(entry);
        while (_history.Count > MaxHistory)
        {
            _history.TryDequeue(out _);
        }

        if (EntryPublished != null)
        {
            foreach (EventHandler<LogEntry> handler in EntryPublished.GetInvocationList())
            {
                try
                {
                    handler(this, entry);
                }
                catch
                {
                    // Ignore subscriber exceptions to prevent crashing the publisher
                }
            }
        }
    }

    public void Publish(LlmTransaction transaction)
    {
        _transactions.Enqueue(transaction);
        while (_transactions.Count > MaxTransactions)
        {
            _transactions.TryDequeue(out _);
        }

        if (TransactionPublished != null)
        {
            foreach (EventHandler<LlmTransaction> handler in TransactionPublished.GetInvocationList())
            {
                try
                {
                    handler(this, transaction);
                }
                catch
                {
                    // Ignore subscriber exceptions to prevent crashing the publisher
                }
            }
        }
    }

    public void ClearHistory()
    {
        while (_history.TryDequeue(out _)) { }
    }

    public void ClearTransactions()
    {
        while (_transactions.TryDequeue(out _)) { }
    }
}
