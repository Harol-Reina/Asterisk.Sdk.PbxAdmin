using FluentAssertions;
using PbxAdmin.LoadTests.Metrics;

namespace PbxAdmin.Tests.LoadTests;

public sealed class DockerStatsSampleTests
{
    // ── ParseByteSize ───────────────────────────────────────────────────────

    [Fact]
    public void ParseByteSize_ShouldReturn0_ForZeroBytes()
    {
        DockerStatsSample.ParseByteSize("0B").Should().Be(0);
    }

    [Fact]
    public void ParseByteSize_ShouldParseKilobytes()
    {
        DockerStatsSample.ParseByteSize("573kB").Should().Be(573_000);
    }

    [Fact]
    public void ParseByteSize_ShouldParseMegabytes()
    {
        DockerStatsSample.ParseByteSize("56.8MB").Should().Be(56_800_000);
    }

    [Fact]
    public void ParseByteSize_ShouldParseMebibytes()
    {
        long expected = (long)(83.71 * 1_048_576);
        DockerStatsSample.ParseByteSize("83.71MiB").Should().Be(expected);
    }

    [Fact]
    public void ParseByteSize_ShouldParseGibibytes()
    {
        long expected = (long)(60.49 * 1_073_741_824);
        DockerStatsSample.ParseByteSize("60.49GiB").Should().Be(expected);
    }

    [Fact]
    public void ParseByteSize_ShouldReturn0_ForEmptyString()
    {
        DockerStatsSample.ParseByteSize("").Should().Be(0);
    }

    [Fact]
    public void ParseByteSize_ShouldReturn0_ForNull()
    {
        DockerStatsSample.ParseByteSize(null).Should().Be(0);
    }

    // ── ParsePercent ────────────────────────────────────────────────────────

    [Fact]
    public void ParsePercent_ShouldParseZero()
    {
        DockerStatsSample.ParsePercent("0.00%").Should().Be(0.0);
    }

    [Fact]
    public void ParsePercent_ShouldParseDecimal()
    {
        DockerStatsSample.ParsePercent("0.19%").Should().Be(0.19);
    }

    [Fact]
    public void ParsePercent_ShouldReturn0_ForEmpty()
    {
        DockerStatsSample.ParsePercent("").Should().Be(0.0);
    }

    // ── TryParse ────────────────────────────────────────────────────────────

    private const string RealtimeJson =
        """{"BlockIO":"0B / 1.09MB","CPUPerc":"0.19%","Container":"demo-pbx-realtime","ID":"54ef7e3feec8","MemPerc":"0.14%","MemUsage":"83.71MiB / 60.49GiB","Name":"demo-pbx-realtime","NetIO":"56.8MB / 111MB","PIDs":"79"}""";

    private const string PostgresJson =
        """{"BlockIO":"0B / 1.62MB","CPUPerc":"0.00%","Container":"demo-postgres","ID":"4321676ac58d","MemPerc":"0.05%","MemUsage":"33.64MiB / 60.49GiB","Name":"demo-postgres","NetIO":"5.93MB / 31.9MB","PIDs":"10"}""";

    [Fact]
    public void TryParse_ShouldParseRealtimePbx()
    {
        var timestamp = new DateTime(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc);

        var sample = DockerStatsSample.TryParse(RealtimeJson, timestamp, concurrentCalls: 5);

        sample.Should().NotBeNull();
        sample!.ContainerName.Should().Be("demo-pbx-realtime");
        sample.CpuPercent.Should().Be(0.19);
        sample.MemoryUsageMb.Should().BeApproximately(83.71, 0.01);
        sample.MemoryLimitMb.Should().BeApproximately(60.49 * 1024, 0.01); // GiB → MiB
        sample.MemoryPercent.Should().Be(0.14);
        sample.NetworkRxBytes.Should().Be(56_800_000);
        sample.NetworkTxBytes.Should().Be(111_000_000);
        sample.Pids.Should().Be(79);
        sample.ConcurrentCalls.Should().Be(5);
    }

    [Fact]
    public void TryParse_ShouldParsePostgres()
    {
        var timestamp = new DateTime(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc);

        var sample = DockerStatsSample.TryParse(PostgresJson, timestamp);

        sample.Should().NotBeNull();
        sample!.ContainerName.Should().Be("demo-postgres");
        sample.CpuPercent.Should().Be(0.0);
        sample.MemoryUsageMb.Should().BeApproximately(33.64, 0.01);
        sample.MemoryPercent.Should().Be(0.05);
        sample.NetworkRxBytes.Should().Be(5_930_000);
        sample.NetworkTxBytes.Should().Be(31_900_000);
        sample.Pids.Should().Be(10);
        sample.ConcurrentCalls.Should().Be(0);
    }

    [Fact]
    public void TryParse_ShouldReturnNull_ForInvalidJson()
    {
        var result = DockerStatsSample.TryParse("not json", DateTime.UtcNow);

        result.Should().BeNull();
    }

    [Fact]
    public void TryParse_ShouldReturnNull_ForEmptyString()
    {
        var result = DockerStatsSample.TryParse("", DateTime.UtcNow);

        result.Should().BeNull();
    }

    [Fact]
    public void TryParse_ShouldSetTimestampAndConcurrentCalls()
    {
        var timestamp = new DateTime(2026, 1, 15, 8, 30, 0, DateTimeKind.Utc);

        var sample = DockerStatsSample.TryParse(RealtimeJson, timestamp, concurrentCalls: 42);

        sample.Should().NotBeNull();
        sample!.Timestamp.Should().Be(timestamp);
        sample.ConcurrentCalls.Should().Be(42);
    }
}
