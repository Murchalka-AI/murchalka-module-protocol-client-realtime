using Murchalka.ModuleProtocol.Contracts;

namespace Murchalka.ClientRealtime.Tests;

internal sealed record DependencyCall(string RequirementId, InvocationScope Scope, string? IdempotencyKey);
