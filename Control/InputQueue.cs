namespace Bot.Control;

public sealed class InputQueue : IDisposable
{
    private readonly record struct QueueEntry(
        InputAction Action, object? Owner, ActionHandle Handle, TaskCompletionSource<bool>? Tcs);

    private readonly LinkedList<QueueEntry> _queue = new();
    private readonly object _sync = new();
    private (QueueEntry Entry, CancellationTokenSource Cts)? _running;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _drainTask;
    private bool _disposed;

    public InputQueue()
    {
        _drainTask = Task.Run(() => DrainLoop(_disposeCts.Token));
    }

    /// <summary>
    /// Enqueue an action. Returns a handle to track completion.
    /// </summary>
    public ActionHandle Enqueue(InputAction action, object? owner = null)
    {
        var handle = new ActionHandle();
        var entry = new QueueEntry(action, owner, handle, null);
        lock (_sync)
            _queue.AddLast(entry);
        return handle;
    }

    /// <summary>
    /// Enqueue at front of queue. Used by UH (emergency) actions.
    /// </summary>
    public ActionHandle EnqueueFront(InputAction action, object? owner = null)
    {
        var handle = new ActionHandle();
        var entry = new QueueEntry(action, owner, handle, null);
        lock (_sync)
            _queue.AddFirst(entry);
        return handle;
    }

    /// <summary>
    /// Enqueue and return a Task that completes when the action finishes.
    /// Used by chat response handler for sequential ordering.
    /// </summary>
    public Task<bool> EnqueueAsync(InputAction action, object? owner = null, CancellationToken ct = default)
    {
        var handle = new ActionHandle();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (ct.CanBeCanceled)
            ct.Register(() => tcs.TrySetCanceled(ct));
        var entry = new QueueEntry(action, owner, handle, tcs);
        lock (_sync)
            _queue.AddLast(entry);
        return tcs.Task;
    }

    /// <summary>
    /// Remove all queued actions for the given owner.
    /// The currently running action (if any) is allowed to finish naturally
    /// to avoid mid-action interrupts (e.g. ctrl+drag releasing at wrong position).
    /// Called by TaskOrchestrator when a root task is replaced.
    /// </summary>
    public void RemoveByOwner(object owner)
    {
        lock (_sync)
        {
            var node = _queue.First;
            while (node != null)
            {
                var next = node.Next;
                if (node.Value.Owner == owner)
                {
                    node.Value.Handle.MarkCancelled();
                    node.Value.Tcs?.TrySetResult(false);
                    _queue.Remove(node);
                }
                node = next;
            }
        }
    }

    /// <summary>
    /// Check if any actions (running or queued) belong to the given owner.
    /// </summary>
    public bool HasPendingForOwner(object owner)
    {
        lock (_sync)
        {
            if (_running?.Entry.Owner == owner)
                return true;

            foreach (var entry in _queue)
                if (entry.Owner == owner) return true;
        }
        return false;
    }

    private async Task DrainLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(5, ct);

                QueueEntry entry;
                CancellationTokenSource cts;

                lock (_sync)
                {
                    if (_queue.First == null)
                        continue;

                    entry = _queue.First.Value;
                    _queue.RemoveFirst();
                    cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    _running = (entry, cts);
                }

                try
                {
                    await entry.Action.RunAsync(cts.Token);
                    entry.Handle.MarkCompleted();
                    entry.Tcs?.TrySetResult(true);
                }
                catch (OperationCanceledException)
                {
                    entry.Handle.MarkCancelled();
                    entry.Tcs?.TrySetResult(false);
                }
                finally
                {
                    lock (_sync)
                        _running = null;
                    cts.Dispose();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void ClearAll()
    {
        lock (_sync)
        {
            foreach (var entry in _queue)
            {
                entry.Handle.MarkCancelled();
                entry.Tcs?.TrySetResult(false);
            }
            _queue.Clear();

            if (_running != null)
            {
                _running.Value.Cts.Cancel();
                _running.Value.Entry.Handle.MarkCancelled();
                _running.Value.Entry.Tcs?.TrySetResult(false);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _disposeCts.Cancel();
        try { _drainTask.Wait(500); } catch { }
        _disposeCts.Dispose();
    }
}
