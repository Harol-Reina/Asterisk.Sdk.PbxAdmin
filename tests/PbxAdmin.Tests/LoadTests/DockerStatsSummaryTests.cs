using FluentAssertions;
using PbxAdmin.LoadTests.Metrics;

namespace PbxAdmin.Tests.LoadTests;

public sealed class DockerStatsSummaryTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static DockerStatsSample BuildSample(
        string container = "demo-pbx-realtime",
        double cpu = 1.0, double memMb = 100, double memLimitMb = 61000,
        long rxBytes = 0, long txBytes = 0, int pids = 10,
        int concurrentCalls = 0, DateTime? timestamp = null) => new()
    {
        Timestamp = timestamp ?? DateTime.UtcNow,
        ContainerName = container,
        CpuPercent = cpu,
        MemoryUsageMb = memMb,
        MemoryLimitMb = memLimitMb,
        MemoryPercent = memMb / memLimitMb * 100,
        NetworkRxBytes = rxBytes,
        NetworkTxBytes = txBytes,
        Pids = pids,
        ConcurrentCalls = concurrentCalls
    };

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public void Compute_ShouldReturnEmpty_WhenNoSamples()
    {
        var result = DockerStatsSummary.Compute([]);

        result.Containers.Should().BeEmpty();
        result.CapacityEstimate.Should().BeNull();
    }

    [Fact]
    public void Compute_ShouldComputeMinAvgMax_ForSingleContainer()
    {
        var samples = new[]
        {
            BuildSample(cpu: 1.0, memMb: 50),
            BuildSample(cpu: 2.0, memMb: 100),
            BuildSample(cpu: 3.0, memMb: 150)
        };

        var result = DockerStatsSummary.Compute(samples);

        result.Containers.Should().ContainKey("demo-pbx-realtime");
        var summary = result.Containers["demo-pbx-realtime"];

        summary.SampleCount.Should().Be(3);
        summary.CpuMin.Should().Be(1.0);
        summary.CpuAvg.Should().Be(2.0);
        summary.CpuMax.Should().Be(3.0);
        summary.MemoryMinMb.Should().Be(50);
        summary.MemoryAvgMb.Should().Be(100);
        summary.MemoryMaxMb.Should().Be(150);
    }

    [Fact]
    public void Compute_ShouldGroupByContainer()
    {
        var samples = new[]
        {
            BuildSample(container: "demo-pbx-realtime", cpu: 5.0),
            BuildSample(container: "demo-postgres", cpu: 1.0),
            BuildSample(container: "demo-pbx-realtime", cpu: 10.0),
            BuildSample(container: "demo-postgres", cpu: 2.0)
        };

        var result = DockerStatsSummary.Compute(samples);

        result.Containers.Should().HaveCount(2);
        result.Containers.Should().ContainKey("demo-pbx-realtime");
        result.Containers.Should().ContainKey("demo-postgres");

        result.Containers["demo-pbx-realtime"].CpuAvg.Should().Be(7.5);
        result.Containers["demo-postgres"].CpuAvg.Should().Be(1.5);
    }

    [Fact]
    public void Compute_ShouldTrackPeakPids()
    {
        var samples = new[]
        {
            BuildSample(pids: 10),
            BuildSample(pids: 20),
            BuildSample(pids: 15)
        };

        var result = DockerStatsSummary.Compute(samples);

        result.Containers["demo-pbx-realtime"].PeakPids.Should().Be(20);
    }

    [Fact]
    public void Compute_ShouldUseMaxNetworkBytes()
    {
        var samples = new[]
        {
            BuildSample(rxBytes: 1000, txBytes: 500),
            BuildSample(rxBytes: 5000, txBytes: 2500),
            BuildSample(rxBytes: 3000, txBytes: 1500)
        };

        var result = DockerStatsSummary.Compute(samples);

        var summary = result.Containers["demo-pbx-realtime"];
        summary.NetworkRxBytes.Should().Be(5000);
        summary.NetworkTxBytes.Should().Be(2500);
    }

    [Fact]
    public void Compute_ShouldCorrelateAtPeakCalls()
    {
        var t1 = new DateTime(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 3, 26, 12, 1, 0, DateTimeKind.Utc);
        var t3 = new DateTime(2026, 3, 26, 12, 2, 0, DateTimeKind.Utc);

        var samples = new[]
        {
            BuildSample(cpu: 1.0, memMb: 50, concurrentCalls: 0, timestamp: t1),
            BuildSample(cpu: 5.0, memMb: 200, concurrentCalls: 10, timestamp: t2),
            BuildSample(cpu: 3.0, memMb: 120, concurrentCalls: 5, timestamp: t3)
        };

        var result = DockerStatsSummary.Compute(samples);

        var summary = result.Containers["demo-pbx-realtime"];
        summary.CpuAtPeakCalls.Should().Be(5.0);
        summary.MemoryAtPeakCallsMb.Should().Be(200);
    }

    [Fact]
    public void Compute_ShouldBuildCapacityEstimate_ForPrimaryTarget()
    {
        var t1 = new DateTime(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 3, 26, 12, 1, 0, DateTimeKind.Utc);

        var samples = new[]
        {
            BuildSample(cpu: 2.0, memMb: 80, concurrentCalls: 5, timestamp: t1),
            BuildSample(cpu: 8.0, memMb: 250, concurrentCalls: 20, timestamp: t2)
        };

        var result = DockerStatsSummary.Compute(samples);

        result.CapacityEstimate.Should().NotBeNull();
        result.CapacityEstimate!.PeakConcurrentCalls.Should().Be(20);
        result.CapacityEstimate.AsteriskCpuPercent.Should().Be(8.0);
        result.CapacityEstimate.AsteriskMemoryMb.Should().Be(250);
    }
}
