namespace PbxAdmin.LoadTests.Metrics;

/// <summary>
/// Aggregated stats for one container across all collected samples.
/// </summary>
public sealed record DockerStatsContainerSummary
{
    public required string ContainerName { get; init; }
    public int SampleCount { get; init; }
    public double CpuMin { get; init; }
    public double CpuAvg { get; init; }
    public double CpuMax { get; init; }
    public double CpuAtPeakCalls { get; init; }
    public double MemoryMinMb { get; init; }
    public double MemoryAvgMb { get; init; }
    public double MemoryMaxMb { get; init; }
    public double MemoryAtPeakCallsMb { get; init; }
    public double MemoryLimitMb { get; init; }
    public long NetworkRxBytes { get; init; }
    public long NetworkTxBytes { get; init; }
    public int PeakPids { get; init; }
}

/// <summary>
/// Capacity insight at the moment of peak concurrent calls, drawn from the primary
/// target container (<see cref="DockerContainerNames.PrimaryTarget"/>).
/// </summary>
public sealed record DockerStatsCapacityEstimate
{
    public int PeakConcurrentCalls { get; init; }
    public double AsteriskCpuPercent { get; init; }
    public double AsteriskMemoryMb { get; init; }
}

/// <summary>
/// Top-level aggregate of Docker resource usage across all containers and samples.
/// Use <see cref="Compute"/> to build from a list of <see cref="DockerStatsSample"/>.
/// </summary>
public sealed record DockerStatsSummary
{
    public required IReadOnlyDictionary<string, DockerStatsContainerSummary> Containers { get; init; }
    public DockerStatsCapacityEstimate? CapacityEstimate { get; init; }

    /// <summary>
    /// Computes per-container min/avg/max statistics and correlates peak resource usage
    /// with peak concurrent calls. Pure computation, no side effects.
    /// </summary>
    public static DockerStatsSummary Compute(IReadOnlyList<DockerStatsSample> samples)
    {
        if (samples.Count == 0)
        {
            return new DockerStatsSummary
            {
                Containers = new Dictionary<string, DockerStatsContainerSummary>(),
                CapacityEstimate = null
            };
        }

        // Find the timestamp of peak concurrent calls across ALL samples.
        var peakSample = samples.MaxBy(s => s.ConcurrentCalls)!;
        var peakTimestamp = peakSample.Timestamp;

        // Group by container and compute aggregates.
        var containers = samples
            .GroupBy(s => s.ContainerName)
            .ToDictionary(
                g => g.Key,
                g => BuildContainerSummary(g.Key, g.ToList(), peakTimestamp));

        // Build capacity estimate from the primary target container.
        DockerStatsCapacityEstimate? capacity = null;
        if (containers.TryGetValue(DockerContainerNames.PrimaryTarget, out var primary))
        {
            capacity = new DockerStatsCapacityEstimate
            {
                PeakConcurrentCalls = peakSample.ConcurrentCalls,
                AsteriskCpuPercent = primary.CpuAtPeakCalls,
                AsteriskMemoryMb = primary.MemoryAtPeakCallsMb
            };
        }

        return new DockerStatsSummary
        {
            Containers = containers,
            CapacityEstimate = capacity
        };
    }

    private static DockerStatsContainerSummary BuildContainerSummary(
        string containerName,
        List<DockerStatsSample> group,
        DateTime peakTimestamp)
    {
        // Find the sample closest to the peak-calls timestamp for correlation.
        var atPeak = group.MinBy(s => Math.Abs((s.Timestamp - peakTimestamp).Ticks))!;

        return new DockerStatsContainerSummary
        {
            ContainerName = containerName,
            SampleCount = group.Count,
            CpuMin = group.Min(s => s.CpuPercent),
            CpuAvg = group.Average(s => s.CpuPercent),
            CpuMax = group.Max(s => s.CpuPercent),
            CpuAtPeakCalls = atPeak.CpuPercent,
            MemoryMinMb = group.Min(s => s.MemoryUsageMb),
            MemoryAvgMb = group.Average(s => s.MemoryUsageMb),
            MemoryMaxMb = group.Max(s => s.MemoryUsageMb),
            MemoryAtPeakCallsMb = atPeak.MemoryUsageMb,
            MemoryLimitMb = group[0].MemoryLimitMb,
            NetworkRxBytes = group.Max(s => s.NetworkRxBytes),
            NetworkTxBytes = group.Max(s => s.NetworkTxBytes),
            PeakPids = group.Max(s => s.Pids)
        };
    }
}
