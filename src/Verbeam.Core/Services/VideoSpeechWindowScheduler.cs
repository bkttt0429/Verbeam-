using System.Collections.Concurrent;
using Verbeam.Core.Models;

namespace Verbeam.Core.Services;

/// <summary>
/// Bounded worker pool that processes video speech windows in priority order
/// (high priority first, FIFO within a priority). Duplicate windows are
/// rejected while queued or running, and each session owns a cancellation
/// token that stops queued and in-flight work.
/// </summary>
public sealed class VideoSpeechWindowScheduler : IAsyncDisposable
{
    public sealed record WindowWorkItem(VideoSpeechWindowTask Task, int Attempt, bool IsBackfill);

    private readonly object _gate = new();
    private readonly PriorityQueue<WindowWorkItem, (int NegatedPriority, long Sequence)> _queue = new();
    private readonly HashSet<string> _activeKeys = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _available = new(0);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _sessionTokens = new(StringComparer.Ordinal);
    private readonly Func<WindowWorkItem, CancellationToken, Task> _processor;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly int _workerCount;
    private readonly List<Task> _workers = [];
    private long _sequence;
    private int _workersStarted;

    public VideoSpeechWindowScheduler(int workerCount, Func<WindowWorkItem, CancellationToken, Task> processor)
    {
        _workerCount = Math.Max(1, workerCount);
        _processor = processor;
    }

    public bool TryEnqueue(VideoSpeechWindowTask task, bool isBackfill = false, int attempt = 0)
    {
        var key = WindowKeyOf(task);
        lock (_gate)
        {
            if (!_activeKeys.Add(key))
            {
                return false;
            }

            _queue.Enqueue(new WindowWorkItem(task, attempt, isBackfill), (-task.Priority, _sequence++));
        }

        EnsureWorkers();
        _available.Release();
        return true;
    }

    public void ScheduleRetry(WindowWorkItem item, TimeSpan delay)
    {
        var token = GetOrCreateSessionToken(item.Task.SessionId);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            TryEnqueue(item.Task, item.IsBackfill, item.Attempt);
        }, CancellationToken.None);
    }

    public CancellationToken GetOrCreateSessionToken(string sessionId)
        => _sessionTokens.GetOrAdd(sessionId, _ => new CancellationTokenSource()).Token;

    public void CancelSession(string sessionId)
    {
        if (_sessionTokens.TryRemove(sessionId, out var source))
        {
            source.Cancel();
            source.Dispose();
        }
    }

    public int PendingCount(string sessionId)
    {
        lock (_gate)
        {
            var prefix = sessionId + ":";
            return _activeKeys.Count(key => key.StartsWith(prefix, StringComparison.Ordinal));
        }
    }

    private static string WindowKeyOf(VideoSpeechWindowTask task)
        => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{task.SessionId}:{task.StartSeconds:F3}:{task.EndSeconds:F3}");

    private void EnsureWorkers()
    {
        if (Interlocked.CompareExchange(ref _workersStarted, 1, 0) != 0)
        {
            return;
        }

        for (var i = 0; i < _workerCount; i++)
        {
            _workers.Add(Task.Run(WorkerLoopAsync, CancellationToken.None));
        }
    }

    private async Task WorkerLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await _available.WaitAsync(_shutdown.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            WindowWorkItem? item;
            lock (_gate)
            {
                if (!_queue.TryDequeue(out item, out _))
                {
                    continue;
                }
            }

            var key = WindowKeyOf(item.Task);
            try
            {
                // Sessions restored after a restart have no token yet; create
                // one lazily. Canceled sessions are also rejected by the
                // processor's own session-status check.
                var sessionToken = GetOrCreateSessionToken(item.Task.SessionId);
                if (sessionToken.IsCancellationRequested)
                {
                    continue;
                }

                await _processor(item, sessionToken);
            }
            catch
            {
                // The processor is responsible for its own error handling;
                // a worker must never die on a stray exception.
            }
            finally
            {
                lock (_gate)
                {
                    _activeKeys.Remove(key);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Cancel session tokens first so in-flight work aborts quickly,
        // then stop the worker loops and wait for them to drain.
        foreach (var source in _sessionTokens.Values)
        {
            source.Cancel();
        }

        _shutdown.Cancel();
        try
        {
            await Task.WhenAll(_workers).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Workers stuck in external tools are abandoned on shutdown.
        }

        foreach (var source in _sessionTokens.Values)
        {
            source.Dispose();
        }

        _sessionTokens.Clear();
        _shutdown.Dispose();
    }
}
