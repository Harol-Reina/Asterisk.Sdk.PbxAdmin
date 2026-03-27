using FluentAssertions;
using PbxAdmin.LoadTests.Sdk;
using PbxAdmin.LoadTests.Validation.Layer2;
using PbxAdmin.LoadTests.Validation.Layer3;

namespace PbxAdmin.Tests.LoadTests.Sdk;

public sealed class SessionValidatorSessionTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 3, 26, 10, 0, 0, TimeSpan.Zero);

    private static CallSessionSnapshot BuildSnapshot(
        string sessionId = "session-001",
        string callerNumber = "573101234567",
        string? finalState = "Completed",
        DateTimeOffset? answerTime = null,
        TimeSpan? duration = null) => new()
    {
        SessionId = sessionId,
        CallerNumber = callerNumber,
        FinalState = finalState,
        StartTime = BaseTime,
        AnswerTime = answerTime ?? BaseTime.AddSeconds(5),
        EndTime = BaseTime.AddSeconds(35),
        Duration = duration ?? TimeSpan.FromSeconds(30),
        TalkTime = TimeSpan.FromSeconds(25),
        ParticipantCount = 2
    };

    private static CdrRecord BuildCdr(
        string src = "573101234567",
        string disposition = "ANSWERED",
        int billSec = 30) => new()
    {
        Src = src,
        Dst = "105",
        Disposition = disposition,
        BillSec = billSec,
        Duration = billSec + 5,
        UniqueId = "1711447200.001"
    };

    // -------------------------------------------------------------------------
    // Full-pass scenario
    // -------------------------------------------------------------------------

    [Fact]
    public void ValidateSession_ShouldPass_WhenAllChecksMatch()
    {
        var session = BuildSnapshot(finalState: "Completed");
        var cdr = BuildCdr(disposition: "ANSWERED", billSec: 30);

        var result = SessionValidator.ValidateSession(session, cdr);

        result.Passed.Should().BeTrue();
        result.Checks.Should().AllSatisfy(c => c.Passed.Should().BeTrue());
    }

    // -------------------------------------------------------------------------
    // CdrExists check
    // -------------------------------------------------------------------------

    [Fact]
    public void ValidateSession_ShouldFailCdrExists_WhenCdrIsNull()
    {
        var session = BuildSnapshot();

        var result = SessionValidator.ValidateSession(session, cdr: null);

        result.Passed.Should().BeFalse();
        var check = result.Checks.Single(c => c.CheckName == "CdrExists");
        check.Passed.Should().BeFalse();
        check.Message.Should().Contain(session.SessionId);
    }

    // -------------------------------------------------------------------------
    // Check 8: StateMatchesDisposition
    // -------------------------------------------------------------------------

    [Fact]
    public void ValidateSession_ShouldPassState_WhenCompletedMatchesAnswered()
    {
        var session = BuildSnapshot(finalState: "Completed");
        var cdr = BuildCdr(disposition: "ANSWERED");

        var result = SessionValidator.ValidateSession(session, cdr);

        var check = result.Checks.Single(c => c.CheckName == "StateMatchesDisposition");
        check.Passed.Should().BeTrue();
    }

    [Fact]
    public void ValidateSession_ShouldFailState_WhenCompletedWithNoAnswer()
    {
        var session = BuildSnapshot(finalState: "Completed");
        var cdr = BuildCdr(disposition: "NO ANSWER");

        var result = SessionValidator.ValidateSession(session, cdr);

        result.Passed.Should().BeFalse();
        var check = result.Checks.Single(c => c.CheckName == "StateMatchesDisposition");
        check.Passed.Should().BeFalse();
    }

    [Fact]
    public void ValidateSession_ShouldPassState_WhenFailedMatchesAnswered()
    {
        // Queue calls: Answer() before Queue() means CDR=ANSWERED even if no agent connects.
        // SDK correctly marks as Failed because no agent connected.
        var session = BuildSnapshot(finalState: "Failed", answerTime: null, duration: null);
        var cdr = BuildCdr(disposition: "ANSWERED");

        var result = SessionValidator.ValidateSession(session, cdr);

        var check = result.Checks.Single(c => c.CheckName == "StateMatchesDisposition");
        check.Passed.Should().BeTrue();
    }

    [Fact]
    public void ValidateSession_ShouldPassState_WhenTimedOutMatchesAnswered()
    {
        // Same as above but SDK marks as TimedOut (queue ring timeout).
        var session = BuildSnapshot(finalState: "TimedOut", answerTime: null, duration: null);
        var cdr = BuildCdr(disposition: "ANSWERED");

        var result = SessionValidator.ValidateSession(session, cdr);

        var check = result.Checks.Single(c => c.CheckName == "StateMatchesDisposition");
        check.Passed.Should().BeTrue();
    }

    [Fact]
    public void ValidateSession_ShouldPassState_WhenTimedOutMatchesNoAnswer()
    {
        var session = BuildSnapshot(finalState: "TimedOut");
        var cdr = BuildCdr(disposition: "NO ANSWER");

        var result = SessionValidator.ValidateSession(session, cdr);

        var check = result.Checks.Single(c => c.CheckName == "StateMatchesDisposition");
        check.Passed.Should().BeTrue();
    }

    [Fact]
    public void ValidateSession_ShouldPassState_WhenFailedMatchesBusy()
    {
        var session = BuildSnapshot(finalState: "Failed");
        var cdr = BuildCdr(disposition: "BUSY");

        var result = SessionValidator.ValidateSession(session, cdr);

        var check = result.Checks.Single(c => c.CheckName == "StateMatchesDisposition");
        check.Passed.Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // Check 9: DurationMatch
    // -------------------------------------------------------------------------

    [Fact]
    public void ValidateSession_ShouldPassDuration_WithinTolerance()
    {
        var session = BuildSnapshot(duration: TimeSpan.FromSeconds(31));
        var cdr = BuildCdr(billSec: 30);

        var result = SessionValidator.ValidateSession(session, cdr);

        var check = result.Checks.Single(c => c.CheckName == "DurationMatch");
        check.Passed.Should().BeTrue();
    }

    [Fact]
    public void ValidateSession_ShouldFailDuration_WhenBeyondTolerance()
    {
        var session = BuildSnapshot(duration: TimeSpan.FromSeconds(35));
        var cdr = BuildCdr(billSec: 30);

        var result = SessionValidator.ValidateSession(session, cdr);

        result.Passed.Should().BeFalse();
        var check = result.Checks.Single(c => c.CheckName == "DurationMatch");
        check.Passed.Should().BeFalse();
        check.Message.Should().Contain("5s");
    }

    [Fact]
    public void ValidateSession_ShouldPassDuration_WhenNullDuration()
    {
        var session = BuildSnapshot(duration: null) with { Duration = null };
        var cdr = BuildCdr(billSec: 30);

        var result = SessionValidator.ValidateSession(session, cdr);

        var check = result.Checks.Single(c => c.CheckName == "DurationMatch");
        check.Passed.Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // Check 10: CallerMatch
    // -------------------------------------------------------------------------

    [Fact]
    public void ValidateSession_ShouldPassCaller_WhenMatch()
    {
        var session = BuildSnapshot(callerNumber: "573101234567");
        var cdr = BuildCdr(src: "573101234567");

        var result = SessionValidator.ValidateSession(session, cdr);

        var check = result.Checks.Single(c => c.CheckName == "CallerMatch");
        check.Passed.Should().BeTrue();
    }

    [Fact]
    public void ValidateSession_ShouldFailCaller_WhenMismatch()
    {
        var session = BuildSnapshot(callerNumber: "573101234567");
        var cdr = BuildCdr(src: "573209999999");

        var result = SessionValidator.ValidateSession(session, cdr);

        result.Passed.Should().BeFalse();
        var check = result.Checks.Single(c => c.CheckName == "CallerMatch");
        check.Passed.Should().BeFalse();
    }
}
