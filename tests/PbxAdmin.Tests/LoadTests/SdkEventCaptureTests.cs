using FluentAssertions;
using PbxAdmin.LoadTests.Validation.Layer1;
using Microsoft.Extensions.Logging.Abstractions;

namespace PbxAdmin.Tests.LoadTests;

public sealed class SdkEventCaptureTests
{
    private static readonly DateTime BaseTime = new(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);

    private static SdkEventCapture CreateCapture()
        => new(NullLogger<SdkEventCapture>.Instance);

    // -------------------------------------------------------------------------
    // RegisterCall
    // -------------------------------------------------------------------------

    [Fact]
    public void RegisterCall_ShouldTrackNewCall()
    {
        var capture = CreateCapture();

        capture.RegisterCall("loadtest-000001", "573101234567", "200", BaseTime);

        capture.TrackedCalls.Should().Be(1);
    }

    [Fact]
    public void TrackedCalls_ShouldIncrementOnRegister()
    {
        var capture = CreateCapture();

        capture.RegisterCall("loadtest-000001", "573101234567", "200", BaseTime);
        capture.RegisterCall("loadtest-000002", "573109876543", "201", BaseTime.AddSeconds(5));
        capture.RegisterCall("loadtest-000003", "573105551234", "200", BaseTime.AddSeconds(10));

        capture.TrackedCalls.Should().Be(3);
    }

    [Fact]
    public void RegisterCall_ShouldOverwritePreviousEntry_WhenSameCallerNumber()
    {
        var capture = CreateCapture();

        capture.RegisterCall("loadtest-000001", "573101234567", "200", BaseTime);
        capture.RegisterCall("loadtest-000002", "573101234567", "201", BaseTime.AddSeconds(30));

        // The second registration replaces the first for the same caller number
        capture.TrackedCalls.Should().Be(1);
    }

    // -------------------------------------------------------------------------
    // GetSnapshot
    // -------------------------------------------------------------------------

    [Fact]
    public void GetSnapshot_ShouldReturnNull_WhenCallNotRegistered()
    {
        var capture = CreateCapture();

        var result = capture.GetSnapshot("nonexistent-call-id");

        result.Should().BeNull();
    }

    [Fact]
    public void GetSnapshot_ShouldReturnNull_WhenCallRegisteredButNoNewchannelSeen()
    {
        var capture = CreateCapture();

        capture.RegisterCall("loadtest-000001", "573101234567", "200", BaseTime);

        // No AMI events processed → call is still pending, no snapshot yet
        var result = capture.GetSnapshot("loadtest-000001");

        result.Should().BeNull();
    }

    // -------------------------------------------------------------------------
    // GetAllSnapshots
    // -------------------------------------------------------------------------

    [Fact]
    public void GetAllSnapshots_ShouldReturnEmpty_WhenNoCalls()
    {
        var capture = CreateCapture();

        var result = capture.GetAllSnapshots();

        result.Should().BeEmpty();
    }

    [Fact]
    public void GetAllSnapshots_ShouldReturnPendingCalls_WhenRegisteredButNotMatched()
    {
        var capture = CreateCapture();

        capture.RegisterCall("loadtest-000001", "573101234567", "200", BaseTime);
        capture.RegisterCall("loadtest-000002", "573109876543", "201", BaseTime.AddSeconds(5));

        // No Newchannel events processed — pending calls are still included
        // so that CDR/CEL database validation can use their CallerNumber
        var result = capture.GetAllSnapshots();

        result.Should().HaveCount(2);
        result.Should().Contain(s => s.CallId == "loadtest-000001" && s.CallerNumber == "573101234567");
        result.Should().Contain(s => s.CallId == "loadtest-000002" && s.CallerNumber == "573109876543");
        result.Should().OnlyContain(s => s.UniqueId == null && s.LinkedId == null,
            because: "pending calls have no AMI-matched unique/linked IDs");
    }

    // -------------------------------------------------------------------------
    // TotalEventsCapured
    // -------------------------------------------------------------------------

    [Fact]
    public void TotalEventsCapured_ShouldBeZero_Initially()
    {
        var capture = CreateCapture();

        capture.TotalEventsCapured.Should().Be(0);
    }

    // -------------------------------------------------------------------------
    // SdkSnapshot defaults
    // -------------------------------------------------------------------------

    [Fact]
    public void SdkSnapshot_ShouldHaveCorrectDefaults()
    {
        var snapshot = new SdkSnapshot
        {
            CallId = "test-001",
            CallerNumber = "573101234567",
            Destination = "200",
            StartTime = BaseTime
        };

        snapshot.AnswerTime.Should().BeNull();
        snapshot.EndTime.Should().BeNull();
        snapshot.Disposition.Should().BeNull();
        snapshot.DurationSecs.Should().BeNull();
        snapshot.UniqueId.Should().BeNull();
        snapshot.LinkedId.Should().BeNull();
        snapshot.QueueName.Should().BeNull();
        snapshot.AgentChannel.Should().BeNull();
        snapshot.EventCount.Should().Be(0);
        snapshot.Events.Should().BeEmpty();
    }

    [Fact]
    public void SdkSnapshot_ShouldPopulateRequiredFields()
    {
        var snapshot = new SdkSnapshot
        {
            CallId = "loadtest-000042-20260326120000",
            CallerNumber = "573101234567",
            Destination = "200",
            StartTime = BaseTime
        };

        snapshot.CallId.Should().Be("loadtest-000042-20260326120000");
        snapshot.CallerNumber.Should().Be("573101234567");
        snapshot.Destination.Should().Be("200");
        snapshot.StartTime.Should().Be(BaseTime);
    }

    // -------------------------------------------------------------------------
    // CapturedEvent
    // -------------------------------------------------------------------------

    [Fact]
    public void CapturedEvent_ShouldStoreFields()
    {
        var captured = new CapturedEvent
        {
            EventType = "Newchannel",
            Timestamp = BaseTime,
            Channel = "Local/200@pstn-to-realtime-dynamic-00000001;1",
            Fields = new Dictionary<string, string>
            {
                ["CallerIDNum"] = "573101234567",
                ["Uniqueid"] = "1711447200.001",
                ["Linkedid"] = "1711447200.001"
            }
        };

        captured.EventType.Should().Be("Newchannel");
        captured.Timestamp.Should().Be(BaseTime);
        captured.Channel.Should().Be("Local/200@pstn-to-realtime-dynamic-00000001;1");
        captured.Fields["CallerIDNum"].Should().Be("573101234567");
        captured.Fields["Uniqueid"].Should().Be("1711447200.001");
    }

    [Fact]
    public void CapturedEvent_ShouldHaveEmptyFieldsByDefault()
    {
        var captured = new CapturedEvent
        {
            EventType = "Hangup",
            Timestamp = BaseTime,
            Channel = "SIP/1000-00000001"
        };

        captured.Fields.Should().BeEmpty();
    }

    // -------------------------------------------------------------------------
    // StopCapturing (no connection — should not throw)
    // -------------------------------------------------------------------------

    [Fact]
    public void StopCapturing_ShouldNotThrow_WhenNeverStarted()
    {
        var capture = CreateCapture();

        var act = () => capture.StopCapturing();

        act.Should().NotThrow();
    }
}
