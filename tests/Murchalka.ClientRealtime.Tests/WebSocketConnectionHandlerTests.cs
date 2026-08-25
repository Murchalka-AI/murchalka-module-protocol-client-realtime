using Murchalka.ClientRealtime.Realtime;
using Xunit;

namespace Murchalka.ClientRealtime.Tests;

/// <summary>Verifies authenticated realtime session orchestration.</summary>
public sealed class WebSocketConnectionHandlerTests
{
    /// <summary>Verifies that a durable session scopes the agent turn and is closed on disconnect.</summary>
    [Fact]
    public async Task AuthenticatedTurnUsesAndClosesDurableSession()
    {
        var dependencies = new RecordingDependencyInvoker();
        var socket = new ScriptedWebSocket();
        socket.QueueText(new { type = "authenticate", username = "owner", password = "VeryStrong123" });
        socket.QueueText(new { type = "turn", conversationId = "conversation-test", text = "Hello", idempotencyKey = "turn-test" });
        socket.QueueClose();
        var handler = new WebSocketConnectionHandler(dependencies, TimeProvider.System);

        await handler.RunAsync(socket, TestContext.Current.CancellationToken);

        Assert.Equal(["authentication", "sessions", "agent", "sessions"], dependencies.Calls.Select(value => value.RequirementId));
        var openedSession = dependencies.Calls[1].Scope.SessionId;
        Assert.NotNull(openedSession);
        Assert.Equal(openedSession, dependencies.Calls[2].Scope.SessionId);
        Assert.Equal(openedSession, dependencies.Calls[3].Scope.SessionId);
        Assert.Equal("person-owner", dependencies.Calls[2].Scope.PersonId);
        Assert.Equal("authenticated", socket.Sent[0].GetProperty("type").GetString());
        Assert.Equal("turn.completed", socket.Sent[1].GetProperty("type").GetString());
        Assert.Equal(openedSession, socket.Sent[1].GetProperty("sessionId").GetString());
    }
}
