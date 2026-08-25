using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.ModuleProtocol.Json;
using Murchalka.ClientRealtime.Protocol;
using Murchalka.ClientRealtime.Realtime;

namespace Murchalka.ClientRealtime.Runtime;

internal sealed class ModuleConnection : IModuleDependencyInvoker, IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly ModuleId _moduleId;
    private readonly InstanceId _instanceId;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<ResultEnvelope>> _pending = new();
    private readonly RealtimeServer _server;
    private ConfigurationSnapshot _configuration;
    private DependencyEndpointsSnapshot _dependencies;
    private bool _active;
    private bool _disposed;

    private ModuleConnection(
        Stream stream,
        ModuleId moduleId,
        InstanceId instanceId,
        ConfigurationSnapshot configuration,
        DependencyEndpointsSnapshot dependencies)
    {
        _stream = stream;
        _moduleId = moduleId;
        _instanceId = instanceId;
        _configuration = configuration;
        _dependencies = dependencies;
        _server = new RealtimeServer(this);
    }

    public static async Task<ModuleConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        var moduleId = new ModuleId(Required("MURCHALKA_MODULE_ID"));
        var instanceId = new InstanceId(Required("MURCHALKA_INSTANCE_ID"));
        var proofKey = Convert.FromBase64String(Required("MURCHALKA_PROOF_KEY"));
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(Required("MURCHALKA_SOCKET")), cancellationToken).ConfigureAwait(false);
            var stream = new NetworkStream(socket, ownsSocket: true);
            var hello = new ModuleHello(
                moduleId,
                SemanticVersion.Parse(Required("MURCHALKA_MODULE_VERSION")),
                Required("MURCHALKA_BUNDLE_DIGEST"),
                instanceId,
                [1],
                Required("MURCHALKA_ARTIFACT_ID"),
                ModuleTarget.Runtime,
                Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Required("MURCHALKA_CAPABILITIES_DIGEST"),
                Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
            await GatewayFrameCodec.WriteAsync(stream, "moduleHello", hello, cancellationToken).ConfigureAwait(false);
            var challenge = GatewayFrameCodec.PayloadAs<RuntimeChallenge>(await GatewayFrameCodec.ReadAsync(stream, cancellationToken).ConfigureAwait(false));
            if (challenge.ModuleNonce != hello.Nonce || challenge.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                throw new InvalidDataException("Runtime challenge is invalid.");
            }

            var transcript = string.Join(
                '\n',
                "murchalka-module-proof-v1",
                hello.ModuleId.Value,
                hello.ModuleVersion.ToString(),
                hello.BundleDigest,
                hello.InstanceId.Value,
                hello.ArtifactId,
                hello.DeclaredCapabilitiesDigest,
                challenge.SelectedProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                challenge.ModuleNonce,
                challenge.RuntimeNonce);
            var proof = new ModuleProof(
                moduleId,
                instanceId,
                challenge.RuntimeNonce,
                challenge.ModuleNonce,
                Convert.ToBase64String(HMACSHA256.HashData(proofKey, Encoding.UTF8.GetBytes(transcript))));
            CryptographicOperations.ZeroMemory(proofKey);
            await GatewayFrameCodec.WriteAsync(stream, "moduleProof", proof, cancellationToken).ConfigureAwait(false);
            var configuration = GatewayFrameCodec.PayloadAs<ConfigurationSnapshot>(await GatewayFrameCodec.ReadAsync(stream, cancellationToken).ConfigureAwait(false));
            _ = await GatewayFrameCodec.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
            var dependencies = GatewayFrameCodec.PayloadAs<DependencyEndpointsSnapshot>(await GatewayFrameCodec.ReadAsync(stream, cancellationToken).ConfigureAwait(false));
            await GatewayFrameCodec.WriteAsync(
                stream,
                "moduleReady",
                new ModuleReady(moduleId, instanceId, hello.DeclaredCapabilitiesDigest, DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);
            return new ModuleConnection(stream, moduleId, instanceId, configuration, dependencies);
        }
        catch
        {
            socket.Dispose();
            CryptographicOperations.ZeroMemory(proofKey);
            throw;
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var frame = await GatewayFrameCodec.ReadAsync(_stream, cancellationToken).ConfigureAwait(false);
            switch (frame.Kind)
            {
                case "control":
                    if (!await HandleControlAsync(GatewayFrameCodec.PayloadAs<ControlMessage>(frame), cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }

                    break;
                case "invocation":
                    await HandleStatusInvocationAsync(GatewayFrameCodec.PayloadAs<InvocationEnvelope>(frame), cancellationToken).ConfigureAwait(false);
                    break;
                case "capabilityResult":
                    var result = GatewayFrameCodec.PayloadAs<ResultEnvelope>(frame);
                    if (_pending.TryRemove(result.InvocationId, out var completion))
                    {
                        completion.TrySetResult(result);
                    }

                    break;
                default:
                    throw new InvalidDataException($"Unexpected protocol frame '{frame.Kind}'.");
            }
        }
    }

    public async ValueTask<JsonElement> InvokeDependencyAsync(
        string requirementId,
        string? actorReference,
        InvocationScope scope,
        string purpose,
        JsonElement payload,
        string payloadSchema,
        string? idempotencyKey,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        if (!_active)
        {
            throw new ModuleDependencyException("module-inactive", "Realtime module is not active.");
        }

        var endpoint = _dependencies.Endpoints.SingleOrDefault(value => value.RequirementId == requirementId)
            ?? throw new ModuleDependencyException("dependency-not-granted", $"Dependency '{requirementId}' is not granted.");
        var invocation = new InvocationEnvelope(
            Guid.NewGuid(),
            endpoint.Capability,
            endpoint.CapabilityVersion,
            endpoint.ProviderInstance,
            _moduleId,
            actorReference,
            scope,
            purpose,
            endpoint.AuthorizationReference,
            Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"),
            null,
            deadline,
            idempotencyKey,
            payloadSchema,
            payload,
            null);
        var completion = new TaskCompletionSource<ResultEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(invocation.InvocationId, completion))
        {
            throw new InvalidOperationException("Invocation identifier collision.");
        }

        try
        {
            await WriteAsync("capabilityInvocation", invocation, cancellationToken).ConfigureAwait(false);
            using var deadlineSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadlineSource.CancelAfter(deadline - DateTimeOffset.UtcNow);
            var result = await completion.Task.WaitAsync(deadlineSource.Token).ConfigureAwait(false);
            if (result.Status == InvocationStatus.Succeeded && result.Payload is { } response)
            {
                return response;
            }

            throw new ModuleDependencyException(result.Error?.Code ?? "dependency-failed", result.Error?.Message ?? "Dependency invocation failed.");
        }
        finally
        {
            _pending.TryRemove(invocation.InvocationId, out _);
        }
    }

    private async Task<bool> HandleControlAsync(ControlMessage control, CancellationToken cancellationToken)
    {
        if (control.Kind == ControlMessageKind.HealthProbe)
        {
            await WriteAsync("health", new ModuleHealth(_active ? ModuleHealthStatus.Ready : ModuleHealthStatus.NotReady, DateTimeOffset.UtcNow, _active ? [] : ["inactive"]), cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (control.Kind == ControlMessageKind.ReloadConfiguration)
        {
            _configuration = control.Payload.Deserialize<ConfigurationSnapshot>(ProtocolJson.Options) ?? throw new InvalidDataException("Configuration snapshot is invalid.");
        }
        else if (control.Kind == ControlMessageKind.UpdateBindings)
        {
            _dependencies = control.Payload.Deserialize<DependencyEndpointsSnapshot>(ProtocolJson.Options) ?? throw new InvalidDataException("Dependency snapshot is invalid.");
        }
        else if (control.Kind == ControlMessageKind.Activate)
        {
            await _server.StartAsync(ReadEndpoint(_configuration.Values), cancellationToken).ConfigureAwait(false);
            _active = true;
        }
        else if (control.Kind is ControlMessageKind.Drain or ControlMessageKind.Stop)
        {
            _active = false;
            await _server.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        await WriteAsync("controlResult", new ControlResult(control.OperationId, true, null, null, null), cancellationToken).ConfigureAwait(false);
        return control.Kind != ControlMessageKind.Stop;
    }

    private async Task HandleStatusInvocationAsync(InvocationEnvelope invocation, CancellationToken cancellationToken)
    {
        ResultEnvelope result;
        if (!_active || invocation.Payload is null)
        {
            result = Failure(invocation.InvocationId, "module-inactive", ErrorCategory.Unavailable, "Realtime endpoint is unavailable.");
        }
        else
        {
            result = new ResultEnvelope(
                invocation.InvocationId,
                InvocationStatus.Succeeded,
                JsonSerializer.SerializeToElement(new { endpoint = new Uri(ReadEndpoint(_configuration.Values), "/v1/realtime"), protocol = "murchalka.realtime.v1" }),
                null,
                null,
                [],
                [],
                null);
        }

        await WriteAsync("result", result, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteAsync<T>(string kind, T payload, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await GatewayFrameCodec.WriteAsync(_stream, kind, payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static Uri ReadEndpoint(JsonElement configuration)
    {
        var value = configuration.TryGetProperty("endpoint", out var endpoint) ? endpoint.GetString() : "http://127.0.0.1:5080";
        return Uri.TryCreate(value, UriKind.Absolute, out var result) ? result : throw new InvalidDataException("Realtime endpoint configuration is invalid.");
    }

    private static ResultEnvelope Failure(Guid invocationId, string code, ErrorCategory category, string message) =>
        new(invocationId, InvocationStatus.Failed, null, new ProtocolError(code, category, false, message, null), null, [], [], null);

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"Required environment variable '{name}' is missing.");

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _server.DisposeAsync().ConfigureAwait(false);
        _stream.Dispose();
        _writeGate.Dispose();
        foreach (var completion in _pending.Values)
        {
            completion.TrySetCanceled();
        }
    }
}
