using System.Text.Json;

namespace Murchalka.ClientRealtime.Protocol;

internal sealed record GatewayFrame(string Kind, JsonElement Payload);

