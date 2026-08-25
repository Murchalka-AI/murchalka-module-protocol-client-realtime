using System.Text.Json;
using Murchalka.ModuleProtocol.Json;

namespace Murchalka.ClientRealtime.Protocol;

internal static class GatewayFrameCodec
{
    public static ValueTask WriteAsync<T>(Stream stream, string kind, T payload, CancellationToken cancellationToken) =>
        LengthPrefixedJson.WriteAsync(stream, new GatewayFrame(kind, JsonSerializer.SerializeToElement(payload, ProtocolJson.Options)), cancellationToken: cancellationToken);

    public static ValueTask<GatewayFrame> ReadAsync(Stream stream, CancellationToken cancellationToken) =>
        LengthPrefixedJson.ReadAsync<GatewayFrame>(stream, cancellationToken: cancellationToken);

    public static T PayloadAs<T>(GatewayFrame frame) =>
        frame.Payload.Deserialize<T>(ProtocolJson.Options) ?? throw new InvalidDataException($"Frame '{frame.Kind}' payload is invalid.");
}

