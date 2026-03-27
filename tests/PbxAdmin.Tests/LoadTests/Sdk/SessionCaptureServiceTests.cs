using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PbxAdmin.LoadTests.Sdk;

namespace PbxAdmin.Tests.LoadTests.Sdk;

public sealed class SessionCaptureServiceTests
{
    private static CallSessionSnapshot BuildSnapshot(
        string sessionId = "session-001",
        string callerNumber = "573101234567",
        string? finalState = "Completed",
        TimeSpan? duration = null) => new()
    {
        SessionId = sessionId,
        CallerNumber = callerNumber,
        FinalState = finalState,
        StartTime = DateTimeOffset.UtcNow.AddSeconds(-30),
        EndTime = DateTimeOffset.UtcNow,
        Duration = duration ?? TimeSpan.FromSeconds(30),
        ParticipantCount = 2
    };

    [Fact]
    public void CompletedSessionCount_ShouldBeZero_WhenNoSessionsCaptured()
    {
        var sut = new SessionCaptureService(NullLoggerFactory.Instance);

        sut.CompletedSessionCount.Should().Be(0);
    }

    [Fact]
    public void GetSessionByCallerNumber_ShouldReturnNull_WhenNotFound()
    {
        var sut = new SessionCaptureService(NullLoggerFactory.Instance);

        sut.GetSessionByCallerNumber("999").Should().BeNull();
    }

    [Fact]
    public void GetSessionBySessionId_ShouldReturnNull_WhenNotFound()
    {
        var sut = new SessionCaptureService(NullLoggerFactory.Instance);

        sut.GetSessionBySessionId("nonexistent").Should().BeNull();
    }

    [Fact]
    public void AddSnapshot_ShouldStore_AndRetrieveBySessionId()
    {
        var sut = new SessionCaptureService(NullLoggerFactory.Instance);
        var snapshot = BuildSnapshot(sessionId: "abc-123");

        sut.AddSnapshot(snapshot);

        sut.GetSessionBySessionId("abc-123").Should().NotBeNull();
        sut.GetSessionBySessionId("abc-123")!.SessionId.Should().Be("abc-123");
        sut.CompletedSessionCount.Should().Be(1);
    }

    [Fact]
    public void AddSnapshot_ShouldRetrieve_ByCallerNumber()
    {
        var sut = new SessionCaptureService(NullLoggerFactory.Instance);
        var snapshot = BuildSnapshot(callerNumber: "573209876543");

        sut.AddSnapshot(snapshot);

        var result = sut.GetSessionByCallerNumber("573209876543");
        result.Should().NotBeNull();
        result!.CallerNumber.Should().Be("573209876543");
    }

    [Fact]
    public void AddSnapshot_ShouldNotDuplicate_WhenSameSessionIdAdded()
    {
        var sut = new SessionCaptureService(NullLoggerFactory.Instance);
        var first = BuildSnapshot(sessionId: "dup-001", callerNumber: "111");
        var second = BuildSnapshot(sessionId: "dup-001", callerNumber: "222");

        sut.AddSnapshot(first);
        sut.AddSnapshot(second);

        sut.CompletedSessionCount.Should().Be(1);
        // First one wins
        sut.GetSessionBySessionId("dup-001")!.CallerNumber.Should().Be("111");
    }

    [Fact]
    public void GetCompletedSessions_ShouldReturnAll()
    {
        var sut = new SessionCaptureService(NullLoggerFactory.Instance);
        sut.AddSnapshot(BuildSnapshot(sessionId: "s1", callerNumber: "111"));
        sut.AddSnapshot(BuildSnapshot(sessionId: "s2", callerNumber: "222"));
        sut.AddSnapshot(BuildSnapshot(sessionId: "s3", callerNumber: "333"));

        var all = sut.GetCompletedSessions();

        all.Should().HaveCount(3);
    }
}
