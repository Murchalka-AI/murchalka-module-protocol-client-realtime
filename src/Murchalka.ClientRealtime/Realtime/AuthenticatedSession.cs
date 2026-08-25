using System.Text.Json;

namespace Murchalka.ClientRealtime.Realtime;

internal sealed class AuthenticatedSession
{
    private readonly Dictionary<string, string> _sessions = new(StringComparer.Ordinal);

    public AuthenticatedSession(string subject, string personId, JsonElement roles)
    {
        Subject = subject;
        PersonId = personId;
        Roles = roles;
    }

    public string Subject { get; }

    public string PersonId { get; }

    public JsonElement Roles { get; }

    public IReadOnlyDictionary<string, string> Sessions => _sessions;

    public bool TryGetSession(string conversationId, out string sessionId) =>
        _sessions.TryGetValue(conversationId, out sessionId!);

    public void AddSession(string conversationId, string sessionId) =>
        _sessions.Add(conversationId, sessionId);
}
