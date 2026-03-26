using FluentAssertions;
using NSubstitute;
using PbxAdmin.LoadTests.Validation.Layer2;
using PbxAdmin.LoadTests.Validation.Layer2.Repositories;

namespace PbxAdmin.Tests.LoadTests;

public sealed class CelReaderTests
{
    private static readonly DateTime BaseTime = new(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
    private const string LinkedId = "1711447200.042";

    private static List<CelRecord> MakeCallEvents() =>
    [
        new() { Id = 1, EventType = "CHAN_START",    EventTime = BaseTime,              LinkedId = LinkedId },
        new() { Id = 2, EventType = "ANSWER",        EventTime = BaseTime.AddSeconds(3), LinkedId = LinkedId },
        new() { Id = 3, EventType = "BRIDGE_ENTER",  EventTime = BaseTime.AddSeconds(5), LinkedId = LinkedId },
        new() { Id = 4, EventType = "APP_START",     EventTime = BaseTime.AddSeconds(6), LinkedId = LinkedId },
        new() { Id = 5, EventType = "BRIDGE_EXIT",   EventTime = BaseTime.AddSeconds(35), LinkedId = LinkedId },
        new() { Id = 6, EventType = "HANGUP",        EventTime = BaseTime.AddSeconds(36), LinkedId = LinkedId },
        new() { Id = 7, EventType = "LINKEDID_END",  EventTime = BaseTime.AddSeconds(37), LinkedId = LinkedId }
    ];

    // -------------------------------------------------------------------------
    // GetEventSequence
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetEventSequence_ShouldReturnOrderedByTime()
    {
        var repo = Substitute.For<ICelReadRepository>();
        // Return events in reverse order to verify sorting
        var events = MakeCallEvents();
        events.Reverse();
        repo.GetByLinkedIdAsync(LinkedId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(events));

        var reader = new CelReader(repo);
        var result = await reader.GetEventSequenceAsync(LinkedId);

        result.Should().HaveCount(7);
        result.Should().BeInAscendingOrder(e => e.EventTime);
        result[0].EventType.Should().Be("CHAN_START");
        result[^1].EventType.Should().Be("LINKEDID_END");
    }

    [Fact]
    public async Task GetEventSequence_ShouldReturnEmpty_WhenNoEvents()
    {
        var repo = Substitute.For<ICelReadRepository>();
        repo.GetByLinkedIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<CelRecord>()));

        var reader = new CelReader(repo);
        var result = await reader.GetEventSequenceAsync("nonexistent");

        result.Should().BeEmpty();
    }

    // -------------------------------------------------------------------------
    // GetBridgeEvents
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetBridgeEvents_ShouldOnlyReturnBridgeTypes()
    {
        var repo = Substitute.For<ICelReadRepository>();
        repo.GetByLinkedIdAsync(LinkedId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MakeCallEvents()));

        var reader = new CelReader(repo);
        var result = await reader.GetBridgeEventsAsync(LinkedId);

        result.Should().HaveCount(2);
        result.Should().OnlyContain(e =>
            e.EventType == "BRIDGE_ENTER" || e.EventType == "BRIDGE_EXIT");
    }

    [Fact]
    public async Task GetBridgeEvents_ShouldReturnEmpty_WhenNoBridgeEvents()
    {
        var repo = Substitute.For<ICelReadRepository>();
        var events = new List<CelRecord>
        {
            new() { Id = 1, EventType = "CHAN_START", EventTime = BaseTime, LinkedId = LinkedId },
            new() { Id = 2, EventType = "HANGUP",     EventTime = BaseTime.AddSeconds(5), LinkedId = LinkedId }
        };
        repo.GetByLinkedIdAsync(LinkedId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(events));

        var reader = new CelReader(repo);
        var result = await reader.GetBridgeEventsAsync(LinkedId);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetBridgeEvents_ShouldBeOrderedByTime()
    {
        var repo = Substitute.For<ICelReadRepository>();
        var events = MakeCallEvents();
        events.Reverse();
        repo.GetByLinkedIdAsync(LinkedId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(events));

        var reader = new CelReader(repo);
        var result = await reader.GetBridgeEventsAsync(LinkedId);

        result.Should().HaveCount(2);
        result.Should().BeInAscendingOrder(e => e.EventTime);
        result[0].EventType.Should().Be("BRIDGE_ENTER");
        result[1].EventType.Should().Be("BRIDGE_EXIT");
    }
}
