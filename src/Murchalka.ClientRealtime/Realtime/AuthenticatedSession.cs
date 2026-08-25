using System.Text.Json;

namespace Murchalka.ClientRealtime.Realtime;

internal sealed record AuthenticatedSession(string Subject, string PersonId, JsonElement Roles);
