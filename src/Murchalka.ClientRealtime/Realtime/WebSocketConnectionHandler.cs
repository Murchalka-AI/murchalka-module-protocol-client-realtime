using System.Net.WebSockets;
using System.Text.Json;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.ClientRealtime.Runtime;

namespace Murchalka.ClientRealtime.Realtime;

internal sealed class WebSocketConnectionHandler
{
    private const int MaximumMessageBytes = 65536;
    private readonly ModuleConnection _connection;
    private readonly TimeProvider _timeProvider;

    public WebSocketConnectionHandler(ModuleConnection connection, TimeProvider? timeProvider = null)
    {
        _connection = connection;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task RunAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        AuthenticatedSession? session = null;
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var request = await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
            var type = RealtimeRequestValidator.RequiredString(request, "type", 32);
            try
            {
                JsonElement response;
                if (type == "authenticate")
                {
                    var authentication = await _connection.InvokeDependencyAsync(
                        "authentication",
                        null,
                        new InvocationScope(null, null, null, null, null, null),
                        "client-authentication",
                        JsonSerializer.SerializeToElement(new
                        {
                            operation = "authenticate",
                            username = RealtimeRequestValidator.RequiredString(request, "username", 128),
                            password = RealtimeRequestValidator.RequiredString(request, "password", 1024)
                        }),
                        "auth.local.request@1",
                        null,
                        _timeProvider.GetUtcNow().AddSeconds(15),
                        cancellationToken).ConfigureAwait(false);
                    session = new AuthenticatedSession(
                        authentication.GetProperty("subject").GetString()!,
                        authentication.GetProperty("personId").GetString()!,
                        authentication.GetProperty("roles").Clone());
                    response = JsonSerializer.SerializeToElement(new
                    {
                        type = "authenticated",
                        subject = session.Subject,
                        personId = session.PersonId,
                        roles = session.Roles
                    });
                }
                else
                {
                    if (session is null)
                    {
                        throw new UnauthorizedAccessException("The WebSocket is not authenticated.");
                    }

                    response = type switch
                    {
                        "turn" => await TurnAsync(session, request, cancellationToken).ConfigureAwait(false),
                        "ui.get" => await GetUiAsync(session, request, cancellationToken).ConfigureAwait(false),
                        _ => throw new InvalidDataException("Unknown realtime message type.")
                    };
                }

                await SendAsync(socket, response, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidDataException or UnauthorizedAccessException or ModuleDependencyException)
            {
                var code = exception is ModuleDependencyException dependency ? dependency.Code : exception is UnauthorizedAccessException ? "authentication-required" : "request-invalid";
                await SendAsync(socket, JsonSerializer.SerializeToElement(new { type = "error", code, message = exception.Message }), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<JsonElement> TurnAsync(AuthenticatedSession session, JsonElement request, CancellationToken cancellationToken)
    {
        var conversationId = RealtimeRequestValidator.RequiredString(request, "conversationId", 64);
        var personId = session.PersonId;
        var result = await _connection.InvokeDependencyAsync(
            "agent",
            session.Subject,
            new InvocationScope(null, null, personId, null, null, null),
            "interactive-agent-turn",
            JsonSerializer.SerializeToElement(new
            {
                operation = "turn",
                conversationId,
                text = RealtimeRequestValidator.RequiredString(request, "text", 32768)
            }),
            "agent.turn.request@1",
            request.TryGetProperty("idempotencyKey", out var key) ? key.GetString() : Guid.NewGuid().ToString("N"),
            _timeProvider.GetUtcNow().AddMinutes(3),
            cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new { type = "turn.completed", result });
    }

    private async ValueTask<JsonElement> GetUiAsync(AuthenticatedSession session, JsonElement request, CancellationToken cancellationToken)
    {
        var conversationId = RealtimeRequestValidator.RequiredString(request, "conversationId", 64);
        var result = await _connection.InvokeDependencyAsync(
            "agent-ui",
            session.Subject,
            new InvocationScope(null, null, null, null, null, null),
            "agent-ui-document",
            JsonSerializer.SerializeToElement(new { operation = "get", conversationId }),
            "agent-ui.document.request@1",
            null,
            _timeProvider.GetUtcNow().AddSeconds(10),
            cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new { type = "ui.document", document = result });
    }

    private static async ValueTask<JsonElement> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaximumMessageBytes];
        var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (!result.EndOfMessage || result.MessageType != WebSocketMessageType.Text || result.Count == 0)
        {
            throw new InvalidDataException("Realtime messages must be one bounded UTF-8 text frame.");
        }

        return JsonSerializer.Deserialize<JsonElement>(buffer.AsSpan(0, result.Count));
    }

    private static Task SendAsync(WebSocket socket, JsonElement response, CancellationToken cancellationToken) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(response), WebSocketMessageType.Text, true, cancellationToken);
}
