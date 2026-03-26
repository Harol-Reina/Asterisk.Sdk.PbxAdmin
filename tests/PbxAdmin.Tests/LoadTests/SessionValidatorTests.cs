using FluentAssertions;
using PbxAdmin.LoadTests.Validation.Layer1;
using PbxAdmin.LoadTests.Validation.Layer2;
using PbxAdmin.LoadTests.Validation.Layer3;

namespace PbxAdmin.Tests.LoadTests;

public sealed class SessionValidatorTests
{
    private static readonly DateTime BaseTime = new(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);

    private static SdkSnapshot BuildSnapshot(
        string callId = "loadtest-000001",
        string callerNumber = "573101234567",
        string destination = "200",
        string? disposition = "ANSWERED",
        int? durationSecs = 30,
        string? uniqueId = "1711447200.001",
        DateTime? endTime = null) => new()
    {
        CallId = callId,
        CallerNumber = callerNumber,
        Destination = destination,
        StartTime = BaseTime,
        AnswerTime = BaseTime.AddSeconds(5),
        EndTime = endTime ?? BaseTime.AddSeconds(35),
        Disposition = disposition,
        DurationSecs = durationSecs,
        UniqueId = uniqueId
    };

    private static CdrRecord BuildCdr(
        string src = "573101234567",
        string dst = "200",
        string disposition = "ANSWERED",
        int billSec = 30,
        string uniqueId = "1711447200.001") => new()
    {
        Src = src,
        Dst = dst,
        Disposition = disposition,
        BillSec = billSec,
        UniqueId = uniqueId,
        Duration = billSec + 5
    };

    // -------------------------------------------------------------------------
    // Full-pass scenario
    // -------------------------------------------------------------------------

    [Fact]
    public void ValidateCall_ShouldPass_WhenSdkMatchesCdr()
    {
        var sdk = BuildSnapshot();
        var cdr = BuildCdr();

        var result = SessionValidator.ValidateCall(sdk, cdr);

        result.Passed.Should().BeTrue();
        result.Checks.Should().AllSatisfy(c => c.Passed.Should().BeTrue());
    }

    // -------------------------------------------------------------------------
    // CdrExists check
    // -------------------------------------------------------------------------

    [Fact]
    public void ValidateCall_ShouldFailCdrExists_WhenCdrIsNull()
    {
        var sdk = BuildSnapshot();

        var result = SessionValidator.ValidateCall(sdk, cdr: null);

        result.Passed.Should().BeFalse();
        var check = result.Checks.Single(c => c.CheckName == "CdrExists");
        check.Passed.Should().BeFalse();
        check.Message.Should().Contain(sdk.CallId);
    }

    // -------------------------------------------------------------------------
    // DispositionMatch check
    // -------------------------------------------------------------------------

    [Fact]
    public void ValidateCall_ShouldFailDisposition_WhenMismatch()
    {
        var sdk = BuildSnapshot(disposition: "ANSWERED");
        var cdr = BuildCdr(disposition: "NO ANSWER");

        var result = SessionValidator.ValidateCall(sdk, cdr);

        result.Passed.Should().BeFalse();
        var check = result.Checks.Single(c => c.CheckName == "DispositionMatch");
        check.Passed.Should().BeFalse();
        check.Expected.Should().Be("ANSWERED");
        check.Actual.Should().Be("NO ANSWER");
    }

    // -------------------------------------------------------------------------
    // DurationMatch check
    // -------------------------------------------------------------------------

    [Fact]
    public void ValidateCall_ShouldPassDuration_WithinTolerance()
    {
        // 1s difference — within the 2s tolerance
        var sdk = BuildSnapshot(durationSecs: 31);
        var cdr = BuildCdr(billSec: 30);

        var result = SessionValidator.ValidateCall(sdk, cdr);

        var check = result.Checks.Single(c => c.CheckName == "DurationMatch");
        check.Passed.Should().BeTrue();
    }

    [Fact]
    public void ValidateCall_ShouldFailDuration_WhenBeyondTolerance()
    {
        // 5s difference — exceeds the 2s tolerance
        var sdk = BuildSnapshot(durationSecs: 35);
        var cdr = BuildCdr(billSec: 30);

        var result = SessionValidator.ValidateCall(sdk, cdr);

        result.Passed.Should().BeFalse();
        var check = result.Checks.Single(c => c.CheckName == "DurationMatch");
        check.Passed.Should().BeFalse();
        check.Message.Should().Contain("5s");
    }

    // -------------------------------------------------------------------------
    // SdkDetectedHangup check
    // -------------------------------------------------------------------------

    [Fact]
    public void ValidateCall_ShouldFailHangup_WhenSdkMissedIt()
    {
        var sdk = BuildSnapshot(endTime: null) with { EndTime = null };
        var cdr = BuildCdr();

        // EndTime null means the snapshot builder must produce a null EndTime;
        // override using the record with expression
        var noHangupSdk = new SdkSnapshot
        {
            CallId = sdk.CallId,
            CallerNumber = sdk.CallerNumber,
            Destination = sdk.Destination,
            StartTime = sdk.StartTime,
            AnswerTime = sdk.AnswerTime,
            EndTime = null,
            Disposition = sdk.Disposition,
            DurationSecs = sdk.DurationSecs,
            UniqueId = sdk.UniqueId
        };

        var result = SessionValidator.ValidateCall(noHangupSdk, cdr);

        result.Passed.Should().BeFalse();
        var check = result.Checks.Single(c => c.CheckName == "SdkDetectedHangup");
        check.Passed.Should().BeFalse();
        check.Message.Should().Contain("Hangup");
    }
}
