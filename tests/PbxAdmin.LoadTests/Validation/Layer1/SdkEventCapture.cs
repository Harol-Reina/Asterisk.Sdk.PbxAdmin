using System.Collections.Concurrent;
using Asterisk.Sdk;
using Microsoft.Extensions.Logging;

namespace PbxAdmin.LoadTests.Validation.Layer1;

/// <summary>
/// Subscribes to AMI events from an IAmiConnection and correlates them into
/// per-call SdkSnapshots keyed by the caller number we originated with.
/// </summary>
public sealed class SdkEventCapture : IAsyncDisposable
{
    // AMI field names (case-sensitive as Asterisk sends them)
    private const string FieldCallerIdNum = "CallerIDNum";
    private const string FieldChannel = "Channel";
    private const string FieldUniqueId = "Uniqueid";
    private const string FieldLinkedId = "Linkedid";
    private const string FieldDestChannel = "DestChannel";
    private const string FieldDialStatus = "DialStatus";
    private const string FieldBridgeUniqueid = "BridgeUniqueid";
    private const string FieldQueue = "Queue";
    private const string FieldMemberName = "MemberName";
    private const string FieldHangupCause = "Cause";
    private const string FieldHangupCauseTxt = "Cause-txt";
    private const string FieldCdrDisposition = "Disposition";
    private const string FieldCdrDuration = "Duration";
    private const string FieldCdrAnswerTime = "Answer";

    private readonly ILogger<SdkEventCapture> _logger;

    // Tracks a pending call registration before we see its Newchannel event.
    private sealed class PendingCall
    {
        public required string CallId { get; init; }
        public required string CallerNumber { get; init; }
        public required string Destination { get; init; }
        public required DateTime StartTime { get; init; }
    }

    // Accumulates raw events for one call once we have matched a Newchannel.
    private sealed class CallState
    {
        public required string CallId { get; init; }
        public required string CallerNumber { get; init; }
        public required string Destination { get; init; }
        public required DateTime StartTime { get; init; }
        public string UniqueId { get; set; } = "";
        public string LinkedId { get; set; } = "";
        public DateTime? AnswerTime { get; set; }
        public DateTime? EndTime { get; set; }
        public string? QueueName { get; set; }
        public string? AgentChannel { get; set; }
        public string? Disposition { get; set; }
        public int? DurationSecs { get; set; }
        public readonly List<CapturedEvent> Events = [];
    }

    // callerNumber → PendingCall (before we see Newchannel)
    private readonly ConcurrentDictionary<string, PendingCall> _pendingByCallerNum = new();

    // uniqueid → CallState (after Newchannel matched)
    private readonly ConcurrentDictionary<string, CallState> _stateByUniqueId = new();

    // callId → uniqueid (for lookup by callId)
    private readonly ConcurrentDictionary<string, string> _uniqueIdByCallId = new();

    private IAmiConnection? _connection;
    private readonly Func<ManagerEvent, ValueTask> _handler;
    private int _totalEventsCapured;

    public SdkEventCapture(ILogger<SdkEventCapture> logger)
    {
        _logger = logger;
        _handler = OnEventAsync;
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>Total AMI events processed since StartCapturing.</summary>
    public int TotalEventsCapured => _totalEventsCapured;

    /// <summary>Number of calls that have been registered (regardless of match status).</summary>
    public int TrackedCalls => _pendingByCallerNum.Count + _uniqueIdByCallId.Count;

    /// <summary>
    /// Subscribes to AMI events on the given connection.
    /// Call this before generating calls.
    /// </summary>
    public void StartCapturing(IAmiConnection connection)
    {
        _connection = connection;
        _connection.OnEvent += _handler;
        _logger.LogInformation("SdkEventCapture started.");
    }

    /// <summary>
    /// Registers a call so that when we see its Newchannel event we can
    /// link the AMI event stream back to the CallGenerationResult.
    /// </summary>
    public void RegisterCall(
        string callId,
        string callerNumber,
        string destination,
        DateTime startTime)
    {
        var pending = new PendingCall
        {
            CallId = callId,
            CallerNumber = callerNumber,
            Destination = destination,
            StartTime = startTime
        };

        _pendingByCallerNum[callerNumber] = pending;

        _logger.LogDebug(
            "Registered call {CallId}: {CallerNumber} -> {Destination}",
            callId, callerNumber, destination);
    }

    /// <summary>Returns a snapshot for the given callId, or null if not found.</summary>
    public SdkSnapshot? GetSnapshot(string callId)
    {
        if (!_uniqueIdByCallId.TryGetValue(callId, out var uniqueId))
            return null;

        if (!_stateByUniqueId.TryGetValue(uniqueId, out var state))
            return null;

        return BuildSnapshot(state);
    }

    /// <summary>
    /// Returns snapshots for all tracked calls. Includes both calls matched to AMI
    /// Newchannel events (full data) and pending calls that were registered but not
    /// yet matched (partial data — enough for CDR/CEL database validation).
    /// </summary>
    public List<SdkSnapshot> GetAllSnapshots()
    {
        var matched = _stateByUniqueId.Values.Select(BuildSnapshot);
        var pending = _pendingByCallerNum.Values.Select(BuildPendingSnapshot);
        return [.. matched, .. pending];
    }

    /// <summary>Unsubscribes from AMI events.</summary>
    public void StopCapturing()
    {
        if (_connection is not null)
        {
            _connection.OnEvent -= _handler;
            _connection = null;
            _logger.LogInformation(
                "SdkEventCapture stopped. Events={TotalEvents} TrackedCalls={TrackedCalls}",
                _totalEventsCapured, TrackedCalls);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        StopCapturing();
        return ValueTask.CompletedTask;
    }

    // -------------------------------------------------------------------------
    // Event processing
    // -------------------------------------------------------------------------

    private ValueTask OnEventAsync(ManagerEvent evt)
    {
        Interlocked.Increment(ref _totalEventsCapured);

        try
        {
            switch (evt.EventType)
            {
                case "Newchannel":
                    HandleNewchannel(evt);
                    break;

                case "Hangup":
                    HandleHangup(evt);
                    break;

                case "QueueCallerJoin":
                    HandleQueueCallerJoin(evt);
                    break;

                case "AgentConnect":
                    HandleAgentConnect(evt);
                    break;

                case "Cdr":
                    HandleCdr(evt);
                    break;

                // All other events: append to any matched call state
                default:
                    AppendEventToMatchedCall(evt);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing AMI event {EventType}", evt.EventType);
        }

        return ValueTask.CompletedTask;
    }

    private void HandleNewchannel(ManagerEvent evt)
    {
        string callerIdNum = GetField(evt, FieldCallerIdNum);
        string uniqueId = GetField(evt, FieldUniqueId);
        string linkedId = GetField(evt, FieldLinkedId);

        // Try to match to a registered pending call
        if (!string.IsNullOrEmpty(callerIdNum)
            && _pendingByCallerNum.TryRemove(callerIdNum, out var pending))
        {
            var state = new CallState
            {
                CallId = pending.CallId,
                CallerNumber = pending.CallerNumber,
                Destination = pending.Destination,
                StartTime = pending.StartTime,
                UniqueId = uniqueId,
                LinkedId = linkedId,
                AnswerTime = DateTime.UtcNow
            };

            _stateByUniqueId[uniqueId] = state;
            _uniqueIdByCallId[pending.CallId] = uniqueId;

            AppendEvent(state, evt);

            _logger.LogDebug(
                "Matched Newchannel: CallId={CallId} UniqueId={UniqueId} CallerIDNum={CallerIdNum}",
                pending.CallId, uniqueId, callerIdNum);
        }
        else
        {
            _logger.LogDebug(
                "Unmatched Newchannel: CallerIDNum={CallerIdNum} UniqueId={UniqueId}",
                callerIdNum, uniqueId);
        }
    }

    private void HandleHangup(ManagerEvent evt)
    {
        if (!TryGetStateForEvent(evt, out var state))
            return;

        state.EndTime = DateTime.UtcNow;

        // Derive duration from answer and end times if not yet set by CDR
        if (state.DurationSecs is null && state.AnswerTime.HasValue)
            state.DurationSecs = (int)(state.EndTime.Value - state.AnswerTime.Value).TotalSeconds;

        // Hangup cause maps loosely to disposition
        if (state.Disposition is null)
        {
            string cause = GetField(evt, FieldHangupCause);
            state.Disposition = cause == "16" ? "ANSWERED" : "NO ANSWER";
        }

        AppendEvent(state, evt);
    }

    private void HandleQueueCallerJoin(ManagerEvent evt)
    {
        if (!TryGetStateForEvent(evt, out var state))
            return;

        string queue = GetField(evt, FieldQueue);
        if (!string.IsNullOrEmpty(queue))
            state.QueueName = queue;

        AppendEvent(state, evt);
    }

    private void HandleAgentConnect(ManagerEvent evt)
    {
        if (!TryGetStateForEvent(evt, out var state))
            return;

        string member = GetField(evt, FieldMemberName);
        if (!string.IsNullOrEmpty(member))
            state.AgentChannel = member;

        AppendEvent(state, evt);
    }

    private void HandleCdr(ManagerEvent evt)
    {
        // CDR events don't carry Uniqueid directly — match via CallerIDNum
        string callerIdNum = GetField(evt, FieldCallerIdNum);

        CallState? state = null;

        if (!string.IsNullOrEmpty(callerIdNum))
        {
            state = _stateByUniqueId.Values
                .FirstOrDefault(s => s.CallerNumber == callerIdNum);
        }

        if (state is null)
            return;

        string dispositionField = GetField(evt, FieldCdrDisposition);
        if (!string.IsNullOrEmpty(dispositionField))
            state.Disposition = dispositionField;

        string durationField = GetField(evt, FieldCdrDuration);
        if (!string.IsNullOrEmpty(durationField) && int.TryParse(durationField, out int dur))
            state.DurationSecs = dur;

        AppendEvent(state, evt);
    }

    private void AppendEventToMatchedCall(ManagerEvent evt)
    {
        if (TryGetStateForEvent(evt, out var state))
            AppendEvent(state, evt);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private bool TryGetStateForEvent(ManagerEvent evt, out CallState state)
    {
        string uniqueId = GetUniqueIdFromEvent(evt);

        if (!string.IsNullOrEmpty(uniqueId) && _stateByUniqueId.TryGetValue(uniqueId, out var found))
        {
            state = found;
            return true;
        }

        state = null!;
        return false;
    }

    private static string GetUniqueIdFromEvent(ManagerEvent evt)
    {
        // Most events carry Uniqueid
        string uid = GetField(evt, FieldUniqueId);
        if (!string.IsNullOrEmpty(uid))
            return uid;

        // Some events (BridgeEnter/Leave) carry Linkedid instead
        return GetField(evt, FieldLinkedId);
    }

    private static void AppendEvent(CallState state, ManagerEvent evt)
    {
        var fields = evt.RawFields is not null
            ? evt.RawFields.ToDictionary(kv => kv.Key, kv => kv.Value ?? "")
            : new Dictionary<string, string>();

        lock (state.Events)
        {
            state.Events.Add(new CapturedEvent
            {
                EventType = evt.EventType ?? "",
                Timestamp = DateTime.UtcNow,
                Channel = GetField(evt, FieldChannel),
                Fields = fields
            });
        }
    }

    private static string GetField(ManagerEvent evt, string fieldName)
    {
        if (evt.RawFields is null)
            return "";
        evt.RawFields.TryGetValue(fieldName, out var value);
        return value ?? "";
    }

    private static SdkSnapshot BuildPendingSnapshot(PendingCall pending) => new()
    {
        CallId = pending.CallId,
        CallerNumber = pending.CallerNumber,
        Destination = pending.Destination,
        StartTime = pending.StartTime,
        EventCount = 0,
        Events = []
    };

    private static SdkSnapshot BuildSnapshot(CallState state)
    {
        List<CapturedEvent> eventsCopy;
        lock (state.Events)
            eventsCopy = [.. state.Events];

        return new SdkSnapshot
        {
            CallId = state.CallId,
            CallerNumber = state.CallerNumber,
            Destination = state.Destination,
            StartTime = state.StartTime,
            AnswerTime = state.AnswerTime,
            EndTime = state.EndTime,
            Disposition = state.Disposition,
            DurationSecs = state.DurationSecs,
            UniqueId = string.IsNullOrEmpty(state.UniqueId) ? null : state.UniqueId,
            LinkedId = string.IsNullOrEmpty(state.LinkedId) ? null : state.LinkedId,
            QueueName = state.QueueName,
            AgentChannel = state.AgentChannel,
            EventCount = eventsCopy.Count,
            Events = eventsCopy
        };
    }
}
