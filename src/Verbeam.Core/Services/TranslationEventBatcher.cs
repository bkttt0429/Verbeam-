using System.Threading.Channels;
using Verbeam.Core.Models;
using Verbeam.Core.Storage;

namespace Verbeam.Core.Services;

/// <summary>
/// Buffers realtime translation events and flushes them to each game's event store in
/// batches — a region loop otherwise pays one SQLite INSERT per frame. A batch is written
/// when 32 events accumulate or 5 seconds after the first buffered event, whichever comes
/// first; disposal flushes the remainder. Buffered events can span games, so each flush
/// buckets them by profile (≡ game) and writes each bucket to that game's store.
/// </summary>
public sealed class TranslationEventBatcher : IAsyncDisposable
{
    private const int BatchSize = 32;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    private readonly Func<string, CancellationToken, Task<ITranslationEventStore>> _storeResolver;
    private readonly Channel<TranslationEvent> _channel;
    private readonly Task _pump;
    private readonly CancellationTokenSource _shutdown = new();

    /// <param name="storeResolver">Resolves the per-game event store for a gameId
    /// (≡ profileId), e.g. <c>GameScopedStores.EventsFor</c>.</param>
    public TranslationEventBatcher(Func<string, CancellationToken, Task<ITranslationEventStore>> storeResolver)
    {
        _storeResolver = storeResolver;
        _channel = Channel.CreateUnbounded<TranslationEvent>(new UnboundedChannelOptions
        {
            SingleReader = true
        });
        _pump = Task.Run(PumpAsync);
    }

    public void Enqueue(TranslationEvent entry)
    {
        if (!_channel.Writer.TryWrite(entry))
        {
            // Channel closed during shutdown; drop rather than block the caller.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _shutdown.Cancel();
        try
        {
            await _pump;
        }
        catch (OperationCanceledException)
        {
        }

        _shutdown.Dispose();
    }

    private async Task PumpAsync()
    {
        var batch = new List<TranslationEvent>(BatchSize);
        var reader = _channel.Reader;
        while (true)
        {
            try
            {
                if (!await reader.WaitToReadAsync(_shutdown.Token))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // First event of the batch arrived; collect until full or the window closes.
            var windowClosesAt = DateTime.UtcNow + FlushInterval;
            while (batch.Count < BatchSize)
            {
                if (reader.TryRead(out var entry))
                {
                    batch.Add(entry);
                    continue;
                }

                var remaining = windowClosesAt - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                using var window = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                window.CancelAfter(remaining);
                try
                {
                    if (!await reader.WaitToReadAsync(window.Token))
                    {
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            await FlushAsync(batch);
        }

        // Drain whatever is left after the writer completed or shutdown began.
        while (reader.TryRead(out var entry))
        {
            batch.Add(entry);
        }

        await FlushAsync(batch);
    }

    private async Task FlushAsync(List<TranslationEvent> batch)
    {
        if (batch.Count == 0)
        {
            return;
        }

        try
        {
            // Buffered events can span games; write each game's events to its own store.
            foreach (var perGame in batch.GroupBy(entry => entry.ProfileId, StringComparer.OrdinalIgnoreCase))
            {
                var store = await _storeResolver(perGame.Key, CancellationToken.None);
                await store.AddEventsAsync(perGame.ToArray(), CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[translate] failed to flush {batch.Count} buffered events: {ex.Message}");
        }
        finally
        {
            batch.Clear();
        }
    }
}
