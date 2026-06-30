using System.Collections.Concurrent;
using System.Threading.Channels;
using Verbeam.Core.Models;

namespace Verbeam.Core.Services;

/// <summary>
/// In-process pub/sub for video speech session events. Publishing never
/// blocks: each subscriber has a small bounded channel that drops the oldest
/// entry when full. The SQLite event log remains the source of truth -
/// subscribers treat channel items as payload-carrying wake-up signals and
/// re-read the log from their cursor to self-heal any drops.
/// </summary>
public sealed class VideoSpeechEventBroker
{
    private const int SubscriberChannelCapacity = 64;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<VideoSpeechSessionEvent>>> _subscriptions =
        new(StringComparer.Ordinal);

    public IDisposable Subscribe(string sessionId, out ChannelReader<VideoSpeechSessionEvent> reader)
    {
        var channel = Channel.CreateBounded<VideoSpeechSessionEvent>(new BoundedChannelOptions(SubscriberChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        var subscriberId = Guid.NewGuid();
        var subscribers = _subscriptions.GetOrAdd(
            sessionId,
            _ => new ConcurrentDictionary<Guid, Channel<VideoSpeechSessionEvent>>());
        subscribers[subscriberId] = channel;
        reader = channel.Reader;
        return new Subscription(this, sessionId, subscriberId);
    }

    public void Publish(VideoSpeechSessionEvent sessionEvent)
    {
        if (!_subscriptions.TryGetValue(sessionEvent.SessionId, out var subscribers))
        {
            return;
        }

        foreach (var channel in subscribers.Values)
        {
            channel.Writer.TryWrite(sessionEvent);
        }
    }

    private void Unsubscribe(string sessionId, Guid subscriberId)
    {
        if (!_subscriptions.TryGetValue(sessionId, out var subscribers))
        {
            return;
        }

        if (subscribers.TryRemove(subscriberId, out var channel))
        {
            channel.Writer.TryComplete();
        }

        if (subscribers.IsEmpty)
        {
            _subscriptions.TryRemove(new KeyValuePair<string, ConcurrentDictionary<Guid, Channel<VideoSpeechSessionEvent>>>(sessionId, subscribers));
        }
    }

    private sealed class Subscription(VideoSpeechEventBroker broker, string sessionId, Guid subscriberId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
            {
                broker.Unsubscribe(sessionId, subscriberId);
            }
        }
    }
}
