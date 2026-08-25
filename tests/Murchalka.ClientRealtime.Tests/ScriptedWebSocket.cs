using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Murchalka.ClientRealtime.Tests;

internal sealed class ScriptedWebSocket : WebSocket
{
    private readonly Queue<(byte[] Payload, WebSocketMessageType Type)> _incoming = new();
    private WebSocketState _state = WebSocketState.Open;
    private WebSocketCloseStatus? _closeStatus;
    private string? _closeStatusDescription;

    public List<JsonElement> Sent { get; } = [];

    public override WebSocketCloseStatus? CloseStatus => _closeStatus;

    public override string? CloseStatusDescription => _closeStatusDescription;

    public override WebSocketState State => _state;

    public override string? SubProtocol => null;

    public void QueueText(object value) =>
        _incoming.Enqueue((JsonSerializer.SerializeToUtf8Bytes(value), WebSocketMessageType.Text));

    public void QueueClose() => _incoming.Enqueue(([], WebSocketMessageType.Close));

    public override void Abort() => _state = WebSocketState.Aborted;

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        _closeStatus = closeStatus;
        _closeStatusDescription = statusDescription;
        _state = WebSocketState.Closed;
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
        CloseAsync(closeStatus, statusDescription, cancellationToken);

    public override void Dispose() => _state = WebSocketState.Closed;

    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var next = _incoming.Dequeue();
        Buffer.BlockCopy(next.Payload, 0, buffer.Array!, buffer.Offset, next.Payload.Length);
        if (next.Type == WebSocketMessageType.Close) _state = WebSocketState.CloseReceived;
        return Task.FromResult(new WebSocketReceiveResult(next.Payload.Length, next.Type, true));
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (messageType != WebSocketMessageType.Text || !endOfMessage) throw new InvalidOperationException();
        Sent.Add(JsonSerializer.Deserialize<JsonElement>(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count)));
        return Task.CompletedTask;
    }
}
