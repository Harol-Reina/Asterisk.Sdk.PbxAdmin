using FluentAssertions;
using PbxAdmin.LoadTests.Validation.Layer1;
using PbxAdmin.LoadTests.Validation.Layer2;
using PbxAdmin.LoadTests.Validation.Layer3;

namespace PbxAdmin.Tests.LoadTests;

public sealed class EventSequenceValidatorTests
{
    private static readonly DateTime BaseTime = new(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);

    private static SdkSnapshot BuildSnapshot(
        int eventCount = 5,
        DateTime? answerTime = null) => new()
    {
        CallId = "loadtest-000001",
        CallerNumber = "573101234567",
        Destination = "200",
        StartTime = BaseTime,
        AnswerTime = answerTime ?? BaseTime.AddSeconds(5),
        EndTime = BaseTime.AddSeconds(35),
        EventCount = eventCount
    };

    private static List<CelRecord> BuildCelSequence(bool includeAnswer = true, bool balanced = true) =>
    [
        new CelRecord { EventType = "CHAN_START",    EventTime = BaseTime,                UniqueId = "1711447200.001" },
        new CelRecord { EventType = "ANSWER",        EventTime = BaseTime.AddSeconds(5),  UniqueId = "1711447200.001" },
        new CelRecord { EventType = "BRIDGE_ENTER",  EventTime = BaseTime.AddSeconds(6),  UniqueId = "1711447200.001" },
        new CelRecord { EventType = "BRIDGE_EXIT",   EventTime = BaseTime.AddSeconds(30), UniqueId = "1711447200.001" },
        new CelRecord { EventType = "HANGUP",        EventTime = BaseTime.AddSeconds(31), UniqueId = "1711447200.001" },
    ];

    // -------------------------------------------------------------------------
    // Full-pass scenario
    // -------------------------------------------------------------------------

    [Fact]
    public void Validate_ShouldPass_WhenSequenceMatches()
    {
        var sdk = BuildSnapshot(eventCount: 5);
        var cel = BuildCelSequence();

        var result = EventSequenceValidator.ValidateEventSequence(sdk, cel);

        result.Passed.Should().BeTrue();
        result.Checks.Should().AllSatisfy(c => c.Passed.Should().BeTrue());
    }

    // -------------------------------------------------------------------------
    // CelRecordsExist check
    // -------------------------------------------------------------------------

    [Fact]
    public void Validate_ShouldFailCelRecords_WhenEmpty()
    {
        var sdk = BuildSnapshot();

        var result = EventSequenceValidator.ValidateEventSequence(sdk, []);

        result.Passed.Should().BeFalse();
        var check = result.Checks.Single(c => c.CheckName == "CelRecordsExist");
        check.Passed.Should().BeFalse();
        check.Message.Should().Contain(sdk.CallId);
    }

    // -------------------------------------------------------------------------
    // BridgeConsistency check
    // -------------------------------------------------------------------------

    [Fact]
    public void Validate_ShouldFailBridgeConsistency_WhenUnbalanced()
    {
        // Two BRIDGE_ENTER events but only one BRIDGE_EXIT
        var cel = new List<CelRecord>
        {
            new() { EventType = "CHAN_START",   EventTime = BaseTime,                UniqueId = "1711447200.001" },
            new() { EventType = "BRIDGE_ENTER", EventTime = BaseTime.AddSeconds(5),  UniqueId = "1711447200.001" },
            new() { EventType = "BRIDGE_ENTER", EventTime = BaseTime.AddSeconds(10), UniqueId = "1711447200.001" },
            new() { EventType = "BRIDGE_EXIT",  EventTime = BaseTime.AddSeconds(30), UniqueId = "1711447200.001" },
            new() { EventType = "HANGUP",       EventTime = BaseTime.AddSeconds(31), UniqueId = "1711447200.001" },
        };
        var sdk = BuildSnapshot(eventCount: 5);

        var result = EventSequenceValidator.ValidateEventSequence(sdk, cel);

        result.Passed.Should().BeFalse();
        var check = result.Checks.Single(c => c.CheckName == "BridgeConsistency");
        check.Passed.Should().BeFalse();
        check.Message.Should().Contain("BRIDGE_ENTER");
    }

    // -------------------------------------------------------------------------
    // EventOrder check
    // -------------------------------------------------------------------------

    [Fact]
    public void Validate_ShouldFailOrder_WhenEventsOutOfOrder()
    {
        // HANGUP appears before BRIDGE_EXIT — out of order
        var cel = new List<CelRecord>
        {
            new() { EventType = "CHAN_START",   EventTime = BaseTime,                UniqueId = "1711447200.001" },
            new() { EventType = "BRIDGE_ENTER", EventTime = BaseTime.AddSeconds(5),  UniqueId = "1711447200.001" },
            new() { EventType = "HANGUP",       EventTime = BaseTime.AddSeconds(10), UniqueId = "1711447200.001" },
            new() { EventType = "BRIDGE_EXIT",  EventTime = BaseTime.AddSeconds(8),  UniqueId = "1711447200.001" }, // earlier than HANGUP
        };
        var sdk = BuildSnapshot(eventCount: 4);

        var result = EventSequenceValidator.ValidateEventSequence(sdk, cel);

        result.Passed.Should().BeFalse();
        var check = result.Checks.Single(c => c.CheckName == "EventOrder");
        check.Passed.Should().BeFalse();
        check.Message.Should().Contain("BRIDGE_EXIT");
    }

    // -------------------------------------------------------------------------
    // SdkSawAnswer check
    // -------------------------------------------------------------------------

    [Fact]
    public void Validate_ShouldFailSdkSawAnswer_WhenCelHasAnswerButSdkDoesNot()
    {
        var sdkNoAnswer = new SdkSnapshot
        {
            CallId = "loadtest-000001",
            CallerNumber = "573101234567",
            Destination = "200",
            StartTime = BaseTime,
            AnswerTime = null,   // SDK missed the answer
            EndTime = BaseTime.AddSeconds(35),
            EventCount = 5
        };
        var cel = BuildCelSequence(includeAnswer: true);

        var result = EventSequenceValidator.ValidateEventSequence(sdkNoAnswer, cel);

        result.Passed.Should().BeFalse();
        var check = result.Checks.Single(c => c.CheckName == "SdkSawAnswer");
        check.Passed.Should().BeFalse();
        check.Message.Should().Contain("AnswerTime");
    }
}
