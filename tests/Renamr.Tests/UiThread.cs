using System.Collections.Concurrent;

namespace Renamr.Tests;

/// <summary>
/// Simula il thread UI di WinUI: un unico thread con coda di messaggi. Così i test dei ViewModel
/// verificano anche che Progress&lt;T&gt; e le continuation tornino sul "thread UI".
/// </summary>
public static class UiThread
{
    public static void Run(Func<Task> action)
    {
        var previous = SynchronizationContext.Current;
        var context = new QueueContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var task = action();
            task.ContinueWith(_ => context.Complete(), TaskScheduler.Default);
            context.Pump();
            task.GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private sealed class QueueContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback, object?)> _queue = [];

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public void Complete() => _queue.CompleteAdding();

        public void Pump()
        {
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            {
                callback(state);
            }
        }
    }
}
