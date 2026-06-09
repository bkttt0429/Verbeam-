using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace YomiBridge.Api.Broadcast;

public sealed class TranslationBroadcastHub
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<Guid, BroadcastClient> _clients = new();
    private TranslationBroadcastMessage? _latest;

    public int ClientCount => _clients.Count;

    public TranslationBroadcastMessage? Latest => _latest;

    public async Task AcceptAsync(HttpContext context, CancellationToken cancellationToken)
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var clientId = Guid.NewGuid();
        var client = new BroadcastClient(socket);
        _clients[clientId] = client;

        var latest = _latest;
        if (latest is not null)
        {
            await SendAsync(client, latest, cancellationToken);
        }

        try
        {
            await KeepOpenUntilClientDisconnectsAsync(socket, cancellationToken);
        }
        finally
        {
            _clients.TryRemove(clientId, out _);
        }
    }

    public async Task BroadcastAsync(TranslationBroadcastMessage message, CancellationToken cancellationToken)
    {
        _latest = message;

        foreach (var (clientId, client) in _clients)
        {
            try
            {
                await SendAsync(client, message, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                _clients.TryRemove(clientId, out _);
            }
        }
    }

    private static async Task SendAsync(
        BroadcastClient client,
        TranslationBroadcastMessage message,
        CancellationToken cancellationToken)
    {
        if (client.Socket.State != WebSocketState.Open)
        {
            return;
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        await client.SendLock.WaitAsync(cancellationToken);
        try
        {
            if (client.Socket.State == WebSocketState.Open)
            {
                await client.Socket.SendAsync(
                    payload,
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken);
            }
        }
        finally
        {
            client.SendLock.Release();
        }
    }

    private static async Task KeepOpenUntilClientDisconnectsAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[256];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }
        }

        if (socket.State is WebSocketState.CloseReceived or WebSocketState.Open)
        {
            await socket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "Closing",
                CancellationToken.None);
        }
    }

    private sealed record BroadcastClient(WebSocket Socket)
    {
        public SemaphoreSlim SendLock { get; } = new(1, 1);
    }
}
