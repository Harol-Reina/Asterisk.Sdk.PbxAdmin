using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PbxAdmin.LoadTests.Metrics;

namespace PbxAdmin.Tests.LoadTests;

public sealed class MetricsCollectorTests
{
    private static MetricsCollector Create() =>
        new(NullLogger<MetricsCollector>.Instance);

    // ── RecordCallOriginated ──────────────────────────────────────────────────

    [Fact]
    public void RecordCallOriginated_ShouldIncrement()
    {
        var collector = Create();

        collector.RecordCallOriginated();
        collector.RecordCallOriginated();

        collector.CallsOriginated.Should().Be(2);
    }

    // ── RecordCallAnswered ────────────────────────────────────────────────────

    [Fact]
    public void RecordCallAnswered_ShouldIncrement()
    {
        var collector = Create();

        collector.RecordCallAnswered();

        collector.CallsAnswered.Should().Be(1);
    }

    // ── RecordCallFailed ──────────────────────────────────────────────────────

    [Fact]
    public void RecordCallFailed_ShouldIncrement()
    {
        var collector = Create();

        collector.RecordCallFailed();
        collector.RecordCallFailed();
        collector.RecordCallFailed();

        collector.CallsFailed.Should().Be(3);
    }

    // ── PeakConcurrentCalls ───────────────────────────────────────────────────

    [Fact]
    public void PeakConcurrent_ShouldTrackMaximum()
    {
        var collector = Create();

        collector.RecordCallStarted(); // active=1, peak=1
        collector.RecordCallStarted(); // active=2, peak=2
        collector.RecordCallStarted(); // active=3, peak=3
        collector.RecordCallEnded();   // active=2, peak stays 3
        collector.RecordCallEnded();   // active=1, peak stays 3
        collector.RecordCallStarted(); // active=2, peak stays 3

        collector.PeakConcurrentCalls.Should().Be(3);
        collector.CurrentActiveCalls.Should().Be(2);
    }

    [Fact]
    public void PeakConcurrent_ShouldBeZero_WhenNoCallsStarted()
    {
        var collector = Create();

        collector.PeakConcurrentCalls.Should().Be(0);
    }

    // ── GetSummary: AnswerRate ────────────────────────────────────────────────

    [Fact]
    public void GetSummary_ShouldCalculateAnswerRate()
    {
        var collector = Create();

        collector.RecordCallOriginated();
        collector.RecordCallOriginated();
        collector.RecordCallOriginated();
        collector.RecordCallOriginated();
        collector.RecordCallAnswered();
        collector.RecordCallAnswered();
        collector.RecordCallAnswered();

        var summary = collector.GetSummary(TimeSpan.FromMinutes(1));

        summary.AnswerRate.Should().BeApproximately(75.0, precision: 0.01);
    }

    [Fact]
    public void GetSummary_ShouldReturnZeroAnswerRate_WhenNoCallsOriginated()
    {
        var collector = Create();

        var summary = collector.GetSummary(TimeSpan.FromMinutes(1));

        summary.AnswerRate.Should().Be(0);
    }

    // ── GetSummary: CallsPerMinute ────────────────────────────────────────────

    [Fact]
    public void GetSummary_ShouldCalculateCallsPerMinute()
    {
        var collector = Create();

        for (int i = 0; i < 60; i++)
            collector.RecordCallOriginated();

        var summary = collector.GetSummary(TimeSpan.FromMinutes(2));

        summary.CallsPerMinute.Should().BeApproximately(30.0, precision: 0.01);
    }

    [Fact]
    public void GetSummary_ShouldReturnZeroCallsPerMinute_WhenElapsedIsZero()
    {
        var collector = Create();
        collector.RecordCallOriginated();

        var summary = collector.GetSummary(TimeSpan.Zero);

        summary.CallsPerMinute.Should().Be(0);
    }

    // ── GetSummary: snapshot fields ───────────────────────────────────────────

    [Fact]
    public void GetSummary_ShouldSnapshotAllCounters()
    {
        var collector = Create();

        collector.RecordCallOriginated();
        collector.RecordCallOriginated();
        collector.RecordCallAnswered();
        collector.RecordCallFailed();
        collector.RecordCallStarted();
        collector.RecordCallStarted();
        collector.RecordCallEnded();

        var elapsed = TimeSpan.FromMinutes(1);
        var summary = collector.GetSummary(elapsed);

        summary.CallsOriginated.Should().Be(2);
        summary.CallsAnswered.Should().Be(1);
        summary.CallsFailed.Should().Be(1);
        summary.PeakConcurrentCalls.Should().Be(2);
        summary.Elapsed.Should().Be(elapsed);
    }
}
