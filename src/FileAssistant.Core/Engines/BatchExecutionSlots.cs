namespace FileAssistant.Core.Engines;

/// <summary>Shares the processing budget across independent conversions and ordered file operations.</summary>
public sealed class BatchExecutionSlots(int maximum = 0)
{
    private readonly object gate = new();
    private int limit = Resolve(maximum);
    private int active;
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Limit { get { lock (gate) return limit; } }

    public void SetMaximum(int maximum)
    {
        var next = Resolve(maximum);
        lock (gate)
        {
            if (limit == next) return;
            limit = next;
            WakeWaiters();
        }
    }

    public async ValueTask<IDisposable> EnterAsync(CancellationToken token)
    {
        while (true)
        {
            Task wait;
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                if (active < limit)
                {
                    active++;
                    return new Lease(this);
                }
                wait = changed.Task;
            }
            await wait.WaitAsync(token).ConfigureAwait(false);
        }
    }

    private static int Resolve(int maximum) => maximum == 0 ? Math.Clamp(Environment.ProcessorCount, 1, 8)
        : maximum is >= 1 and <= 8 ? maximum : throw new ArgumentOutOfRangeException(nameof(maximum));

    private void Release()
    {
        lock (gate)
        {
            active--;
            WakeWaiters();
        }
    }

    private void WakeWaiters()
    {
        var previous = changed;
        changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }

    private sealed class Lease(BatchExecutionSlots owner) : IDisposable
    {
        private BatchExecutionSlots? current = owner;
        public void Dispose() => Interlocked.Exchange(ref current, null)?.Release();
    }
}
