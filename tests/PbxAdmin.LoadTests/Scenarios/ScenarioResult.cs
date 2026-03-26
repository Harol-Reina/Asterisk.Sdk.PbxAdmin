using PbxAdmin.LoadTests.Metrics;
using PbxAdmin.LoadTests.Validation;

namespace PbxAdmin.LoadTests.Scenarios;

public sealed record ScenarioResult
{
    public required string ScenarioName { get; init; }
    public required bool Passed { get; init; }
    public required ValidationReport ValidationReport { get; init; }
    public required MetricsSummary Metrics { get; init; }
    public TimeSpan Duration { get; init; }
    public string? ErrorMessage { get; init; }
}
