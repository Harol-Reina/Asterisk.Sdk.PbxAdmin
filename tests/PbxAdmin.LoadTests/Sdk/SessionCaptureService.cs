using System.Collections.Concurrent;
using Asterisk.Sdk.Sessions;
using Asterisk.Sdk.Sessions.Manager;
using Microsoft.Extensions.Logging;

namespace PbxAdmin.LoadTests.Sdk;

/// <summary>
/// Polls ICallSessionManager for completed CallSessions and stores immutable
/// CallSessionSnapshot records for later validation against CDR/CEL data.
/// </summary>
public sealed class SessionCaptureService : IDisposable
{
    private readonly ILogger<SessionCaptureService> _logger;
    private readonly ConcurrentDictionary<string, CallSessionSnapshot> _sessions = new();

    private ICallSessionManager? _sessionManager;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;

    public SessionCaptureService(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<SessionCaptureService>();
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>Number of completed sessions captured so far.</summary>
    public int CompletedSessionCount => _sessions.Count;

    /// <summary>Adds a snapshot to the store. Ignores duplicates (same SessionId).</summary>
    public void AddSnapshot(CallSessionSnapshot snapshot)
    {
        if (_sessions.TryAdd(snapshot.SessionId, snapshot))
            _logger.LogDebug("Captured session {SessionId} ({CallerNumber})", snapshot.SessionId, snapshot.CallerNumber);
    }

    /// <summary>Returns all captured session snapshots.</summary>
    public IReadOnlyList<CallSessionSnapshot> GetCompletedSessions()
        => _sessions.Values.ToList().AsReadOnly();

    /// <summary>Finds a session by its SessionId, or null if not found.</summary>
    public CallSessionSnapshot? GetSessionBySessionId(string sessionId)
        => _sessions.GetValueOrDefault(sessionId);

    /// <summary>Finds the first session matching the caller number, or null if not found.</summary>
    public CallSessionSnapshot? GetSessionByCallerNumber(string callerNumber)
        => _sessions.Values.FirstOrDefault(s =>
            string.Equals(s.CallerNumber, callerNumber, StringComparison.Ordinal));

    // -------------------------------------------------------------------------
    // Attach / Stop
    // -------------------------------------------------------------------------

    /// <summary>
    /// Starts a background polling loop that captures completed sessions from
    /// the ICallSessionManager every 2 seconds.
    /// </summary>
    public void Attach(ICallSessionManager sessionManager)
    {
        _sessionManager = sessionManager;
        _pollCts = new CancellationTokenSource();
        _pollTask = PollLoopAsync(_pollCts.Token);
        _logger.LogInformation("SessionCaptureService attached — polling every 2s");
    }

    /// <summary>
    /// Stops the background polling loop and does a final capture pass.
    /// </summary>
    public async Task StopAsync()
    {
        if (_pollCts is not null)
        {
            await _pollCts.CancelAsync();

            if (_pollTask is not null)
            {
                try
                {
                    await _pollTask;
                }
                catch (OperationCanceledException)
                {
                    // Expected
                }
            }

            _pollCts.Dispose();
            _pollCts = null;
            _pollTask = null;
        }

        // Final capture to pick up any sessions that completed after the last poll
        CaptureCompletedSessions();

        _logger.LogInformation(
            "SessionCaptureService stopped. TotalCaptured={Count}",
            _sessions.Count);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
    }

    // -------------------------------------------------------------------------
    // Internal
    // -------------------------------------------------------------------------

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                CaptureCompletedSessions();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during session capture poll");
            }
        }
    }

    internal void CaptureCompletedSessions()
    {
        if (_sessionManager is null) return;

        try
        {
            var completed = _sessionManager.GetRecentCompleted(1000);

            foreach (var session in completed)
            {
                var snapshot = CreateSnapshot(session);
                AddSnapshot(snapshot);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to capture completed sessions");
        }
    }

    internal static CallSessionSnapshot CreateSnapshot(CallSession session) => new()
    {
        SessionId = session.SessionId,
        CallerNumber = session.CallerIdNum,
        LinkedId = session.LinkedId,
        QueueName = session.QueueName,
        AgentInterface = session.AgentInterface,
        FinalState = session.State.ToString(),
        StartTime = session.CreatedAt,
        AnswerTime = session.ConnectedAt,
        EndTime = session.CompletedAt,
        Duration = session.Duration,
        TalkTime = session.TalkTime,
        ParticipantCount = session.Participants.Count
    };
}
