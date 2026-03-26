namespace PbxAdmin.LoadTests.CallGeneration;

public sealed record SchedulerStats
{
    public int ActiveCalls { get; init; }
    public int TargetConcurrent { get; init; }
    public int TotalGenerated { get; init; }
    public int TotalCompleted { get; init; }
    public double CallsPerMinuteActual { get; init; }
    public TimeSpan Elapsed { get; init; }
    public TimeSpan Remaining { get; init; }
    public Dictionary<string, int> ScenarioCounts { get; init; } = new();
    public DateTime Timestamp { get; init; }
}
