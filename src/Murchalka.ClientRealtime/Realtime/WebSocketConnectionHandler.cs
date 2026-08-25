using System.Net.WebSockets;
using System.Text.Json;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.ClientRealtime.Runtime;

namespace Murchalka.ClientRealtime.Realtime;

internal sealed class WebSocketConnectionHandler
{
    private const int MaximumMessageBytes = 65536;
    private readonly IModuleDependencyInvoker _dependencies;
    private readonly TimeProvider _timeProvider;

    public WebSocketConnectionHandler(IModuleDependencyInvoker dependencies, TimeProvider? timeProvider = null)
    {
        _dependencies = dependencies;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task RunAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        AuthenticatedSession? session = null;
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var request = await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
                if (request.ValueKind == JsonValueKind.Undefined) break;
                var type = RealtimeRequestValidator.RequiredString(request, "type", 32);
                try
                {
                    JsonElement response;
                    if (type == "authenticate")
                    {
                        if (session is not null) throw new InvalidDataException("The WebSocket is already authenticated.");
                        var authentication = await _dependencies.InvokeDependencyAsync(
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
                        if (session is null) throw new UnauthorizedAccessException("The WebSocket is not authenticated.");
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
        finally
        {
            if (session is not null) await CloseSessionsAsync(session).ConfigureAwait(false);
        }
    }

    private async ValueTask<JsonElement> TurnAsync(AuthenticatedSession session, JsonElement request, CancellationToken cancellationToken)
    {
        var conversationId = RealtimeRequestValidator.RequiredString(request, "conversationId", 64);
        var personId = session.PersonId;
        var sessionId = await EnsureSessionAsync(session, conversationId, cancellationToken).ConfigureAwait(false);
        var result = await _dependencies.InvokeDependencyAsync(
            "agent",
            session.Subject,
            new InvocationScope(null, null, personId, null, sessionId, null),
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
        return JsonSerializer.SerializeToElement(new { type = "turn.completed", sessionId, result });
    }

    private async ValueTask<JsonElement> GetUiAsync(AuthenticatedSession session, JsonElement request, CancellationToken cancellationToken)
    {
        var conversationId = RealtimeRequestValidator.RequiredString(request, "conversationId", 64);
        var sessionId = await EnsureSessionAsync(session, conversationId, cancellationToken).ConfigureAwait(false);
        var result = await _dependencies.InvokeDependencyAsync(
            "agent-ui",
            session.Subject,
            new InvocationScope(null, null, session.PersonId, null, sessionId, null),
            "agent-ui-document",
            JsonSerializer.SerializeToElement(new { operation = "get", conversationId }),
            "agent-ui.document.request@1",
            null,
            _timeProvider.GetUtcNow().AddSeconds(10),
            cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new { type = "ui.document", document = result });
    }

    private async ValueTask<string> EnsureSessionAsync(AuthenticatedSession session, string conversationId, CancellationToken cancellationToken)
    {
        if (session.TryGetSession(conversationId, out var existing)) return existing;
        var sessionId = $"session-{Guid.NewGuid():N}";
        var result = await _dependencies.InvokeDependencyAsync(
            "sessions",
            session.Subject,
            new InvocationScope(null, null, session.PersonId, null, sessionId, null),
            "realtime-session-open",
            JsonSerializer.SerializeToElement(new { operation = "open", sessionId, conversationId, personId = session.PersonId }),
            "sessions.manage.request@1",
            $"open:{sessionId}",
            _timeProvider.GetUtcNow().AddSeconds(10),
            cancellationToken).ConfigureAwait(false);
        var openedId = result.GetProperty("session").GetProperty("sessionId").GetString();
        if (openedId != sessionId) throw new ModuleDependencyException("session-response-invalid", "Sessions returned a different identifier.");
        session.AddSession(conversationId, sessionId);
        return sessionId;
    }

    private async Task CloseSessionsAsync(AuthenticatedSession session)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        foreach (var sessionId in session.Sessions.Values)
        {
            try
            {
                await _dependencies.InvokeDependencyAsync(
                    "sessions",
                    session.Subject,
                    new InvocationScope(null, null, session.PersonId, null, sessionId, null),
                    "realtime-session-close",
                    JsonSerializer.SerializeToElement(new { operation = "close", sessionId }),
                    "sessions.manage.request@1",
                    $"close:{sessionId}",
                    _timeProvider.GetUtcNow().AddSeconds(10),
                    timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is ModuleDependencyException or OperationCanceledException)
            {
                // Connection teardown is best-effort; the durable session remains available for administrative recovery.
            }
        }
    }

    private static async ValueTask<JsonElement> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaximumMessageBytes];
        var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (result.MessageType == WebSocketMessageType.Close) return default;
        if (!result.EndOfMessage || result.MessageType != WebSocketMessageType.Text || result.Count == 0)
        {
            throw new InvalidDataException("Realtime messages must be one bounded UTF-8 text frame.");
        }

        return JsonSerializer.Deserialize<JsonElement>(buffer.AsSpan(0, result.Count));
    }

    private static Task SendAsync(WebSocket socket, JsonElement response, CancellationToken cancellationToken) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(response), WebSocketMessageType.Text, true, cancellationToken);
}
