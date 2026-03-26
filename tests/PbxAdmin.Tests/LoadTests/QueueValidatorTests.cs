using FluentAssertions;
using PbxAdmin.LoadTests.Validation.Layer1;
using PbxAdmin.LoadTests.Validation.Layer2;
using PbxAdmin.LoadTests.Validation.Layer3;

namespace PbxAdmin.Tests.LoadTests;

public sealed class QueueValidatorTests
{
    private static readonly DateTime BaseTime = new(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);

    private static SdkSnapshot BuildSnapshot(
        string? queueName = "ventas",
        string? agentChannel = "SIP/2100",
        string? disposition = "ANSWERED") => new()
    {
        CallId = "loadtest-000001",
        CallerNumber = "573101234567",
        Destination = "200",
        StartTime = BaseTime,
        EndTime = BaseTime.AddSeconds(60),
        QueueName = queueName,
        AgentChannel = agentChannel,
        Disposition = disposition
    };

    private static List<QueueLogRecord> BuildQueueEvents(
        string queueName = "ventas",
        string agent = "SIP/2100",
        bool includeConnect = true,
        bool includeAbandon = false) =>
    [
        new QueueLogRecord
        {
            Time = BaseTime,
            CallId = "loadtest-000001",
            QueueName = queueName,
            Agent = "NONE",
            Event = "ENTERQUEUE",
            Data1 = "",
            Data2 = "573101234567"
        },
        .. includeConnect
            ?
            [
                new QueueLogRecord
                {
                    Time = BaseTime.AddSeconds(5),
                    CallId = "loadtest-000001",
                    QueueName = queueName,
                    Agent = agent,
                    Event = "CONNECT",
                    Data1 = "5"
                }
            ]
            : (IEnumerable<QueueLogRecord>)[],
        .. includeAbandon
            ?
            [
                new QueueLogRecord
                {
                    Time = BaseTime.AddSeconds(30),
                    CallId = "loadtest-000001",
                    QueueName = queueName,
                    Agent = "NONE",
                    Event = "ABANDON",
                    Data1 = "1",
                    Data2 = "1",
                    Data3 = "30"
                }
            ]
            : (IEnumerable<QueueLogRecord>)[],
    ];

    // -------------------------------------------------------------------------
    // Full-pass scenario
    // -------------------------------------------------------------------------

    [Fact]
    public void Validate_ShouldPass_WhenQueueEventsMatch()
    {
        var sdk = BuildSnapshot();
        var events = BuildQueueEvents();

        var result = QueueValidator.ValidateQueueCall(sdk, events);

        result.Passed.Should().BeTrue();
        result.Checks.Should().AllSatisfy(c => c.Passed.Should().BeTrue());
    }

    // -------------------------------------------------------------------------
    // QueueEntryExists check
    // -------------------------------------------------------------------------

    [Fact]
    public void Validate_ShouldFailEntry_WhenNoEnterQueue()
    {
        var sdk = BuildSnapshot();
        // Only a CONNECT event, no ENTERQUEUE
        var events = new List<QueueLogRecord>
        {
            new()
            {
                Time = BaseTime.AddSeconds(5),
                CallId = "loadtest-000001",
                QueueName = "ventas",
                Agent = "SIP/2100",
                Event = "CONNECT"
            }
        };

        var result = QueueValidator.ValidateQueueCall(sdk, events);

        result.Passed.Should().BeFalse();
        var check = result.Checks.Single(c => c.CheckName == "QueueEntryExists");
        check.Passed.Should().BeFalse();
        check.Message.Should().Contain("ENTERQUEUE");
    }

    // -------------------------------------------------------------------------
    // AgentMatch check
    // -------------------------------------------------------------------------

    [Fact]
    public void Validate_ShouldFailAgent_WhenMismatch()
    {
        var sdk = BuildSnapshot(agentChannel: "SIP/2100");
        // queue_log says a different agent answered
        var events = BuildQueueEvents(agent: "SIP/2199");

        var result = QueueValidator.ValidateQueueCall(sdk, events);

        result.Passed.Should().BeFalse();
        var check = result.Checks.Single(c => c.CheckName == "AgentMatch");
        check.Passed.Should().BeFalse();
        check.Expected.Should().Be("SIP/2100");
        check.Actual.Should().Be("SIP/2199");
    }

    // -------------------------------------------------------------------------
    // AbandonDetected check
    // -------------------------------------------------------------------------

    [Fact]
    public void Validate_ShouldDetectAbandon_WhenSdkSaysNoAnswer()
    {
        // SDK says NO ANSWER but queue_log has no ABANDON event
        var sdk = BuildSnapshot(agentChannel: null, disposition: "NO ANSWER");
        var events = BuildQueueEvents(includeConnect: false, includeAbandon: false);

        var result = QueueValidator.ValidateQueueCall(sdk, events);

        result.Passed.Should().BeFalse();
        var check = result.Checks.Single(c => c.CheckName == "AbandonDetected");
        check.Passed.Should().BeFalse();
        check.Message.Should().Contain("ABANDON");
    }
}
