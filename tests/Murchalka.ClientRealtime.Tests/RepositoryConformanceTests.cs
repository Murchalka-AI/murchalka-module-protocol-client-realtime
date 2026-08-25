using Murchalka.ModuleSdk.Testing;
using Xunit;

namespace Murchalka.ClientRealtime.Tests;

/// <summary>Verifies repository and capability conformance.</summary>
public sealed class RepositoryConformanceTests
{
    /// <summary>Verifies the canonical module schemas and dependency permissions.</summary>
    [Fact]
    public void RepositoryConforms()
    {
        var report = new ModuleRepositoryConformance().Validate(RepositoryRootLocator.Find());
        Assert.True(report.Passed, string.Join(Environment.NewLine, report.Findings.Select(value => value.Message)));
    }
}

