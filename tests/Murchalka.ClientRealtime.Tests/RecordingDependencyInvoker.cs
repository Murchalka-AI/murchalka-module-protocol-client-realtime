using System.Text.Json;
using Murchalka.ClientRealtime.Runtime;
using Murchalka.ModuleProtocol.Contracts;

namespace Murchalka.ClientRealtime.Tests;

internal sealed class RecordingDependencyInvoker : IModuleDependencyInvoker
{
    private static readonly string[] AdministratorRoles = ["admin"];

    public List<DependencyCall> Calls { get; } = [];

    public ValueTask<JsonElement> InvokeDependencyAsync(
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
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add(new DependencyCall(requirementId, scope, idempotencyKey));
        return ValueTask.FromResult(requirementId switch
        {
            "authentication" => JsonSerializer.SerializeToElement(new
            {
                authenticated = true,
                subject = "local:owner",
                personId = "person-owner",
                roles = AdministratorRoles
            }),
            "sessions" => Session(payload),
            "agent" => JsonSerializer.SerializeToElement(new
            {
                conversationId = "conversation-test",
                userMessageId = "message-user",
                assistantMessageId = "message-assistant",
                message = new { role = "assistant", content = "Hello back." },
                model = "test-model"
            }),
            _ => throw new InvalidOperationException($"Unexpected dependency '{requirementId}'.")
        });
    }

    public ValueTask<JsonElement> InvokeSelectedDependencyAsync(
        string requirementId,
        ModuleId providerModule,
        string? actorReference,
        InvocationScope scope,
        string purpose,
        JsonElement payload,
        string payloadSchema,
        string? idempotencyKey,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add(new DependencyCall(requirementId, scope, idempotencyKey));
        return ValueTask.FromResult(JsonSerializer.SerializeToElement(new { accepted = true, provider = providerModule.Value }));
    }

    private static JsonElement Session(JsonElement payload)
    {
        var sessionId = payload.GetProperty("sessionId").GetString();
        var isOpen = payload.GetProperty("operation").GetString() == "open";
        return JsonSerializer.SerializeToElement(new
        {
            session = new
            {
                sessionId,
                conversationId = isOpen ? payload.GetProperty("conversationId").GetString() : "conversation-test",
                personId = isOpen ? payload.GetProperty("personId").GetString() : "person-owner",
                state = isOpen ? "open" : "closed",
                openedAt = DateTimeOffset.UnixEpoch,
                closedAt = isOpen ? (DateTimeOffset?)null : DateTimeOffset.UnixEpoch.AddMinutes(1)
            },
            version = isOpen ? 1 : 2
        });
    }
}
