using System.Text.Json;
using Murchalka.ModuleProtocol.Contracts;

namespace Murchalka.ClientRealtime.Runtime;

internal interface IModuleDependencyInvoker
{
    ValueTask<JsonElement> InvokeDependencyAsync(
        string requirementId,
        string? actorReference,
        InvocationScope scope,
        string purpose,
        JsonElement payload,
        string payloadSchema,
        string? idempotencyKey,
        DateTimeOffset deadline,
        CancellationToken cancellationToken);

    ValueTask<JsonElement> InvokeSelectedDependencyAsync(
        string requirementId,
        ModuleId providerModule,
        string? actorReference,
        InvocationScope scope,
        string purpose,
        JsonElement payload,
        string payloadSchema,
        string? idempotencyKey,
        DateTimeOffset deadline,
        CancellationToken cancellationToken);
}
