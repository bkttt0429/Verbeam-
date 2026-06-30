using Verbeam.Core.Models;

namespace Verbeam.Core.Services;

public sealed class LlamaCppDownloadPausedException : OperationCanceledException
{
    public LlamaCppDownloadPausedException(string message)
        : base(message)
    {
    }
}

public sealed class LlamaCppDownloadTracker
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Entry
    {
        public string Kind = "";
        public string Id = "";
        public string Status = "downloading";
        public string Message = "";
        public long TotalBytes;
        public long ReceivedBytes;
        public double BytesPerSecond;
        public DateTimeOffset UpdatedAt;
        public long SampleBytes;
        public DateTimeOffset SampleAt;
        public CancellationTokenSource? PauseSource;
    }

    public static string BuildKey(string kind, string id)
        => string.IsNullOrWhiteSpace(id) ? kind : $"{kind}:{id}";

    public LlamaCppDownloadSession Begin(string kind, string id, long totalBytes, long initialBytes)
    {
        var key = BuildKey(kind, id);
        lock (_lock)
        {
            _entries.TryGetValue(key, out var existing);
            existing?.PauseSource?.Dispose();

            var now = DateTimeOffset.UtcNow;
            var entry = new Entry
            {
                Kind = kind,
                Id = id,
                Status = "downloading",
                Message = initialBytes > 0 ? "Resuming download." : "Downloading.",
                TotalBytes = totalBytes,
                ReceivedBytes = initialBytes,
                UpdatedAt = now,
                SampleBytes = initialBytes,
                SampleAt = now,
                PauseSource = new CancellationTokenSource()
            };
            _entries[key] = entry;
            return new LlamaCppDownloadSession(this, key, entry.PauseSource.Token);
        }
    }

    public bool RequestPause(string key)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var entry) &&
                entry.Status == "downloading" &&
                entry.PauseSource is { IsCancellationRequested: false })
            {
                entry.PauseSource.Cancel();
                return true;
            }

            return false;
        }
    }

    internal void Report(string key, long receivedBytes)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            entry.ReceivedBytes = receivedBytes;
            entry.UpdatedAt = now;

            var elapsed = (now - entry.SampleAt).TotalSeconds;
            if (elapsed >= 0.4)
            {
                var instant = (receivedBytes - entry.SampleBytes) / elapsed;
                entry.BytesPerSecond = entry.BytesPerSecond <= 0
                    ? instant
                    : (instant * 0.4) + (entry.BytesPerSecond * 0.6);
                entry.SampleBytes = receivedBytes;
                entry.SampleAt = now;
            }
        }
    }

    internal void Finish(string key, string status, string message)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return;
            }

            entry.Status = status;
            entry.Message = message;
            entry.BytesPerSecond = 0;
            entry.UpdatedAt = DateTimeOffset.UtcNow;
            entry.PauseSource?.Dispose();
            entry.PauseSource = null;
            if (status == "completed")
            {
                entry.ReceivedBytes = entry.TotalBytes;
            }
        }
    }

    public IReadOnlyList<LlamaCppDownloadProgress> Snapshot()
    {
        lock (_lock)
        {
            return _entries
                .Select(pair => new LlamaCppDownloadProgress(
                    pair.Key,
                    pair.Value.Kind,
                    pair.Value.Id,
                    pair.Value.Status,
                    pair.Value.TotalBytes,
                    pair.Value.ReceivedBytes,
                    Math.Max(0, Math.Round(pair.Value.BytesPerSecond)),
                    pair.Value.Message,
                    pair.Value.UpdatedAt))
                .OrderBy(item => item.Kind, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }
}

public sealed class LlamaCppDownloadSession
{
    private readonly LlamaCppDownloadTracker _tracker;
    private readonly string _key;

    internal LlamaCppDownloadSession(LlamaCppDownloadTracker tracker, string key, CancellationToken pauseToken)
    {
        _tracker = tracker;
        _key = key;
        PauseToken = pauseToken;
    }

    public CancellationToken PauseToken { get; }

    public void Report(long receivedBytes)
        => _tracker.Report(_key, receivedBytes);

    public void Complete(string message)
        => _tracker.Finish(_key, "completed", message);

    public void Pause(string message)
        => _tracker.Finish(_key, "paused", message);

    public void Fail(string message)
        => _tracker.Finish(_key, "failed", message);
}
