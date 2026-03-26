using FluentAssertions;
using NSubstitute;
using PbxAdmin.LoadTests.Validation.Layer2;
using PbxAdmin.LoadTests.Validation.Layer2.Repositories;

namespace PbxAdmin.Tests.LoadTests;

public sealed class QueueLogReaderTests
{
    private static readonly DateTime BaseTime = new(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
    private const string QueueName = "ventas";
    private const string CallId1 = "loadtest-000001-20260326100000";

    // -------------------------------------------------------------------------
    // GetQueueEventsForCall
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetQueueEvents_ShouldReturnAllEventsForCall()
    {
        var repo = Substitute.For<IQueueLogRepository>();
        var events = new List<QueueLogRecord>
        {
            new() { Id = 1, CallId = CallId1, Event = "ENTERQUEUE", Time = BaseTime },
            new() { Id = 2, CallId = CallId1, Event = "CONNECT",    Time = BaseTime.AddSeconds(8), Data1 = "8" },
            new() { Id = 3, CallId = CallId1, Event = "COMPLETECALLER", Time = BaseTime.AddSeconds(65) }
        };
        repo.GetByCallIdAsync(CallId1, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(events));

        var reader = new QueueLogReader(repo);
        var result = await reader.GetQueueEventsForCallAsync(CallId1);

        result.Should().HaveCount(3);
        result.Should().BeEquivalentTo(events);
    }

    [Fact]
    public async Task GetQueueEvents_ShouldReturnEmpty_WhenCallNotFound()
    {
        var repo = Substitute.For<IQueueLogRepository>();
        repo.GetByCallIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<QueueLogRecord>()));

        var reader = new QueueLogReader(repo);
        var result = await reader.GetQueueEventsForCallAsync("nonexistent");

        result.Should().BeEmpty();
    }

    // -------------------------------------------------------------------------
    // GetQueueSla
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetQueueSla_ShouldReturnZero_WhenNoEvents()
    {
        var repo = Substitute.For<IQueueLogRepository>();
        repo.GetByQueueAndTimeRangeAsync(QueueName, BaseTime, BaseTime.AddHours(1), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<QueueLogRecord>()));

        var reader = new QueueLogReader(repo);
        var stats = await reader.GetQueueSlaAsync(QueueName, BaseTime, BaseTime.AddHours(1), slaThresholdSecs: 20);

        stats.QueueName.Should().Be(QueueName);
        stats.Offered.Should().Be(0);
        stats.Answered.Should().Be(0);
        stats.Abandoned.Should().Be(0);
        stats.WithinSla.Should().Be(0);
        stats.SlaPercent.Should().Be(0.0);
        stats.AvgWaitSecs.Should().Be(0.0);
    }

    [Fact]
    public async Task GetQueueSla_ShouldCalculateCorrectPercentage()
    {
        var repo = Substitute.For<IQueueLogRepository>();
        // 4 calls offered, 3 answered (wait: 5s, 15s, 25s), 1 abandoned
        // SLA threshold = 20s → 2 within SLA (5s and 15s)
        var events = new List<QueueLogRecord>
        {
            new() { Id = 1, QueueName = QueueName, Event = "ENTERQUEUE",      Time = BaseTime },
            new() { Id = 2, QueueName = QueueName, Event = "ENTERQUEUE",      Time = BaseTime.AddSeconds(1) },
            new() { Id = 3, QueueName = QueueName, Event = "ENTERQUEUE",      Time = BaseTime.AddSeconds(2) },
            new() { Id = 4, QueueName = QueueName, Event = "ENTERQUEUE",      Time = BaseTime.AddSeconds(3) },
            new() { Id = 5, QueueName = QueueName, Event = "CONNECT",         Time = BaseTime.AddSeconds(10), Data1 = "5" },
            new() { Id = 6, QueueName = QueueName, Event = "CONNECT",         Time = BaseTime.AddSeconds(20), Data1 = "15" },
            new() { Id = 7, QueueName = QueueName, Event = "CONNECT",         Time = BaseTime.AddSeconds(30), Data1 = "25" },
            new() { Id = 8, QueueName = QueueName, Event = "ABANDON",         Time = BaseTime.AddSeconds(60) }
        };
        repo.GetByQueueAndTimeRangeAsync(QueueName, BaseTime, BaseTime.AddHours(1), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(events));

        var reader = new QueueLogReader(repo);
        var stats = await reader.GetQueueSlaAsync(QueueName, BaseTime, BaseTime.AddHours(1), slaThresholdSecs: 20);

        stats.QueueName.Should().Be(QueueName);
        stats.Offered.Should().Be(4);
        stats.Answered.Should().Be(3);
        stats.Abandoned.Should().Be(1);
        stats.WithinSla.Should().Be(2);
        stats.SlaPercent.Should().BeApproximately(50.0, 0.001);
        stats.AvgWaitSecs.Should().BeApproximately(15.0, 0.001); // (5+15+25)/3
    }

    [Fact]
    public async Task GetQueueSla_ShouldCountWithinSla_WhenData1BelowThreshold()
    {
        var repo = Substitute.For<IQueueLogRepository>();
        // All 3 calls answered within SLA (threshold = 30s, wait times: 5s, 10s, 20s)
        var events = new List<QueueLogRecord>
        {
            new() { Id = 1, QueueName = QueueName, Event = "ENTERQUEUE", Time = BaseTime },
            new() { Id = 2, QueueName = QueueName, Event = "ENTERQUEUE", Time = BaseTime.AddSeconds(1) },
            new() { Id = 3, QueueName = QueueName, Event = "ENTERQUEUE", Time = BaseTime.AddSeconds(2) },
            new() { Id = 4, QueueName = QueueName, Event = "CONNECT",    Time = BaseTime.AddSeconds(10), Data1 = "5" },
            new() { Id = 5, QueueName = QueueName, Event = "CONNECT",    Time = BaseTime.AddSeconds(15), Data1 = "10" },
            new() { Id = 6, QueueName = QueueName, Event = "CONNECT",    Time = BaseTime.AddSeconds(25), Data1 = "20" }
        };
        repo.GetByQueueAndTimeRangeAsync(QueueName, BaseTime, BaseTime.AddHours(1), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(events));

        var reader = new QueueLogReader(repo);
        var stats = await reader.GetQueueSlaAsync(QueueName, BaseTime, BaseTime.AddHours(1), slaThresholdSecs: 30);

        stats.WithinSla.Should().Be(3);
        stats.SlaPercent.Should().BeApproximately(100.0, 0.001);
        stats.AvgWaitSecs.Should().BeApproximately(11.666, 0.01); // (5+10+20)/3
    }

    [Fact]
    public async Task GetQueueSla_ShouldIgnoreConnect_WhenData1IsNotNumeric()
    {
        var repo = Substitute.For<IQueueLogRepository>();
        var events = new List<QueueLogRecord>
        {
            new() { Id = 1, QueueName = QueueName, Event = "ENTERQUEUE", Time = BaseTime },
            new() { Id = 2, QueueName = QueueName, Event = "CONNECT",    Time = BaseTime.AddSeconds(5), Data1 = "" }
        };
        repo.GetByQueueAndTimeRangeAsync(QueueName, BaseTime, BaseTime.AddHours(1), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(events));

        var reader = new QueueLogReader(repo);
        var stats = await reader.GetQueueSlaAsync(QueueName, BaseTime, BaseTime.AddHours(1), slaThresholdSecs: 20);

        stats.Offered.Should().Be(1);
        stats.Answered.Should().Be(1);
        stats.WithinSla.Should().Be(0);
        stats.AvgWaitSecs.Should().Be(0.0);
    }
}
