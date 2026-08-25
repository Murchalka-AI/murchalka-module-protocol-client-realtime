using System.Text.Json;

namespace Murchalka.ClientRealtime.Realtime;

internal static class RealtimeRequestValidator
{
    public static string RequiredString(JsonElement request, string property, int maximumLength)
    {
        if (!request.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"Property '{property}' is required.");
        }

        var result = value.GetString()!;
        if (result.Length > maximumLength)
        {
            throw new InvalidDataException($"Property '{property}' exceeds {maximumLength} characters.");
        }

        return result;
    }
}

