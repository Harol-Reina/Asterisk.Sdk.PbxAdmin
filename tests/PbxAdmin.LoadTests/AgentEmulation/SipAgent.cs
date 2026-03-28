using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Configuration;
using SIPSorcery.Media;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;

namespace PbxAdmin.LoadTests.AgentEmulation;

/// <summary>
/// Represents a single SIP endpoint that registers with Asterisk and handles calls.
/// Designed to run up to 300 instances concurrently sharing a single SIPTransport.
/// </summary>
public sealed class SipAgent : IAsyncDisposable
{
    private readonly string _password;
    private readonly string _serverHost;
    private readonly int _serverPort;
    private readonly SIPTransport _transport;
    private readonly AgentBehaviorOptions _behavior;
    private readonly ILogger _logger;

    private SIPRegistrationUserAgent? _regAgent;
    private SIPUserAgent? _userAgent;
    private VoIPMediaSession? _mediaSession;

    // Stores the pending INVITE request while the agent is in Ringing state,
    // so that AnswerAsync() can call AcceptCall(request) with the correct invite.
    private SIPRequest? _pendingInvite;
    private volatile bool _inviteCancelled;

    private CancellationTokenSource? _autoHangupCts;
    private CancellationTokenSource? _wrapupCts;
    private volatile bool _draining;
    private AgentState _state = AgentState.Offline;
    private readonly Random _random = new();

    public string ExtensionId { get; }
    public AgentState State => _state;
    public int CallsHandled { get; private set; }
    public DateTime? LastCallTime { get; private set; }

    public event Action<SipAgent, AgentState, AgentState>? StateChanged;

    public SipAgent(
        string extensionId,
        string password,
        string serverHost,
        int serverPort,
        SIPTransport sharedTransport,
        AgentBehaviorOptions behavior,
        ILogger logger)
    {
        ExtensionId = extensionId;
        _password = password;
        _serverHost = serverHost;
        _serverPort = serverPort;
        _transport = sharedTransport;
        _behavior = behavior;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>Starts SIP registration with Asterisk.</summary>
    public Task RegisterAsync(CancellationToken ct)
    {
        if (_state != AgentState.Offline && _state != AgentState.Error)
        {
            _logger.LogWarning("Agent {Ext}: RegisterAsync called in state {State}, ignoring", ExtensionId, _state);
            return Task.CompletedTask;
        }

        TransitionTo(AgentState.Registering);

        var aor = SIPURI.ParseSIPURI($"sip:{ExtensionId}@{_serverHost}:{_serverPort}");
        var contactUri = SIPURI.ParseSIPURI($"sip:{ExtensionId}@0.0.0.0");

        int expiry = CalculateStaggeredExpiry(int.Parse(ExtensionId) % 300);

        _regAgent = new SIPRegistrationUserAgent(
            _transport,
            outboundProxy: null,
            sipAccountAOR: aor,
            authUsername: ExtensionId,
            password: _password,
            realm: null,
            registrarHost: $"{_serverHost}:{_serverPort}",
            contactURI: contactUri,
            expiry: expiry,
            customHeaders: null,
            maxRegistrationAttemptTimeout: 10000,
            registerFailureRetryInterval: 10,
            maxRegisterAttempts: 10,
            exitOnUnequivocalFailure: false);

        _regAgent.RegistrationSuccessful += OnRegistrationSuccessful;
        _regAgent.RegistrationFailed += OnRegistrationFailed;
        _regAgent.RegistrationRemoved += OnRegistrationRemoved;
        _regAgent.RegistrationTemporaryFailure += OnRegistrationTemporaryFailure;

        // SIPUserAgent wraps the transport for call handling.
        // isTransportExclusive: false — many agents share the same transport.
        // IMPORTANT: Do NOT subscribe to OnIncomingCall. With a shared transport,
        // every SIPUserAgent fires OnIncomingCall for ALL INVITEs (not just its own),
        // causing all agents to try AcceptCall on the same INVITE → transaction conflicts.
        // Instead, AgentPoolService.OnTransportRequestReceived dispatches INVITEs
        // to the correct agent based on the To header.
        _userAgent = new SIPUserAgent(_transport, outboundProxy: null, isTransportExclusive: false, answerSipAccount: null);
        _userAgent.OnCallHungup += OnCallHungup;

        _regAgent.Start();

        _logger.LogInformation("Agent {Ext}: registration started → {Host}:{Port}", ExtensionId, _serverHost, _serverPort);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Re-attempts registration for an agent currently in Error state.
    /// Stops the old registration agent and starts a fresh one.
    /// </summary>
    public Task RetryRegisterAsync(CancellationToken ct)
    {
        if (_state != AgentState.Error)
        {
            _logger.LogDebug("Agent {Ext}: RetryRegisterAsync called in state {State}, ignoring", ExtensionId, _state);
            return Task.CompletedTask;
        }

        // Stop the old registration agent before creating a new one
        _regAgent?.Stop();
        _regAgent = null;

        // Reset state so RegisterAsync accepts the call
        // (RegisterAsync checks for Offline or Error)

        return RegisterAsync(ct);
    }

    /// <summary>Stops registration and transitions to Offline.</summary>
    public Task UnregisterAsync()
    {
        _regAgent?.Stop();
        TransitionTo(AgentState.Offline);
        _logger.LogInformation("Agent {Ext}: unregistered", ExtensionId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Answers the current ringing call.
    /// Uses VoIPMediaSession which sends comfort noise when no audio source
    /// is configured — appropriate for load testing without real audio.
    /// </summary>
    public async Task AnswerAsync()
    {
        if (_state != AgentState.Ringing || _userAgent is null || _pendingInvite is null)
        {
            _logger.LogWarning("Agent {Ext}: AnswerAsync called in state {State}, ignoring", ExtensionId, _state);
            return;
        }

        await AnswerInviteAsync(_pendingInvite);
    }

    /// <summary>Puts the current call on hold.</summary>
    public async Task HoldAsync()
    {
        if (_state != AgentState.InCall || _userAgent is null || _mediaSession is null)
        {
            _logger.LogWarning("Agent {Ext}: HoldAsync called in state {State}, ignoring", ExtensionId, _state);
            return;
        }

        await _mediaSession.PutOnHold();
        _userAgent.PutOnHold();
        TransitionTo(AgentState.OnHold);
        _logger.LogInformation("Agent {Ext}: call on hold", ExtensionId);
    }

    /// <summary>Takes the current call off hold.</summary>
    public Task UnholdAsync()
    {
        if (_state != AgentState.OnHold || _userAgent is null || _mediaSession is null)
        {
            _logger.LogWarning("Agent {Ext}: UnholdAsync called in state {State}, ignoring", ExtensionId, _state);
            return Task.CompletedTask;
        }

        _mediaSession.TakeOffHold();
        _userAgent.TakeOffHold();
        TransitionTo(AgentState.InCall);
        _logger.LogInformation("Agent {Ext}: call resumed", ExtensionId);
        return Task.CompletedTask;
    }

    /// <summary>Sends a DTMF digit on the current call.</summary>
    public async Task SendDtmfAsync(byte tone)
    {
        if ((_state != AgentState.InCall && _state != AgentState.OnHold) || _userAgent is null)
        {
            _logger.LogWarning("Agent {Ext}: SendDtmfAsync called in state {State}, ignoring", ExtensionId, _state);
            return;
        }

        await _userAgent.SendDtmf(tone);
        _logger.LogDebug("Agent {Ext}: sent DTMF {Tone}", ExtensionId, tone);
    }

    /// <summary>Performs a blind transfer to the specified extension on the same server.</summary>
    public async Task TransferAsync(string targetExtension)
    {
        if (_state != AgentState.InCall || _userAgent is null)
        {
            _logger.LogWarning("Agent {Ext}: TransferAsync called in state {State}, ignoring", ExtensionId, _state);
            return;
        }

        var destination = SIPURI.ParseSIPURI($"sip:{targetExtension}@{_serverHost}:{_serverPort}");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        bool transferred = await _userAgent.BlindTransfer(destination, TimeSpan.FromSeconds(10), cts.Token);

        if (transferred)
        {
            _logger.LogInformation("Agent {Ext}: blind transfer to {Target} succeeded", ExtensionId, targetExtension);
            await CleanupCallAsync();
            await BeginWrapupAsync();
        }
        else
        {
            _logger.LogWarning("Agent {Ext}: blind transfer to {Target} failed", ExtensionId, targetExtension);
        }
    }

    /// <summary>Hangs up the current call and begins the wrapup timer.</summary>
    public async Task HangupAsync()
    {
        if (_state != AgentState.InCall && _state != AgentState.OnHold && _state != AgentState.Ringing)
        {
            _logger.LogWarning("Agent {Ext}: HangupAsync called in state {State}, ignoring", ExtensionId, _state);
            return;
        }

        CancelAutoHangupTimer();
        _userAgent?.Hangup();
        _pendingInvite = null;
        await CleanupCallAsync();
        await BeginWrapupAsync();
        _logger.LogInformation("Agent {Ext}: call hung up", ExtensionId);
    }

    /// <summary>
    /// Handles an incoming INVITE dispatched from the shared transport.
    /// The SIPUserAgent.OnIncomingCall event routes here. Also callable directly
    /// from a transport-level dispatcher when sharing transport across agents.
    /// </summary>
    /// <summary>Prevents the agent from accepting new calls during drain.</summary>
    internal void BeginDrain() => _draining = true;

    internal async Task HandleIncomingInviteAsync(SIPRequest request)
    {
        if (_draining || _state != AgentState.Idle || _userAgent is null)
        {
            _logger.LogDebug("Agent {Ext}: ignoring INVITE in state {State} (draining={Draining})", ExtensionId, _state, _draining);
            return;
        }

        _pendingInvite = request;
        _inviteCancelled = false;
        _logger.LogInformation("Agent {Ext}: INVITE received from {From}", ExtensionId, request.Header.From);
        TransitionTo(AgentState.Ringing);

        if (_behavior.AutoAnswer)
        {
            int ringDelay = _random.Next(_behavior.RingDelaySecs, _behavior.RingDelayMaxSecs + 1);
            await Task.Delay(TimeSpan.FromSeconds(ringDelay));

            if (_state == AgentState.Ringing && !_inviteCancelled)
            {
                await AnswerAsync();
            }
        }
    }

    /// <summary>
    /// Called by AgentPoolService when a SIP CANCEL is received for this agent's
    /// pending INVITE. Transitions back to Idle so the agent is available for new calls.
    /// </summary>
    internal void CancelPendingCall()
    {
        if (_state != AgentState.Ringing) return;

        _inviteCancelled = true;
        _pendingInvite = null;
        TransitionTo(AgentState.Idle);
        _logger.LogInformation("Agent {Ext}: call cancelled by remote", ExtensionId);
    }

    public async ValueTask DisposeAsync()
    {
        CancelAutoHangupTimer();

        if (_wrapupCts is not null)
        {
            await _wrapupCts.CancelAsync();
            _wrapupCts.Dispose();
        }
        _wrapupCts = null;

        _regAgent?.Stop();

        if (_userAgent is not null)
        {
            _userAgent.OnCallHungup -= OnCallHungup;

            if (_userAgent.IsCallActive)
            {
                _userAgent.Hangup();
            }

            _userAgent.Close();
            _userAgent = null;
        }

        if (_mediaSession is not null)
        {
            _mediaSession.Close(null);
            _mediaSession.Dispose();
            _mediaSession = null;
        }

        await Task.CompletedTask;
    }

    // -------------------------------------------------------------------------
    // Private: registration event handlers
    // -------------------------------------------------------------------------

    private void OnRegistrationSuccessful(SIPURI uri, SIPResponse response)
    {
        TransitionTo(AgentState.Idle);
        _logger.LogInformation("Agent {Ext}: registered successfully at {Uri}", ExtensionId, uri);
    }

    private void OnRegistrationFailed(SIPURI uri, SIPResponse? response, string reason)
    {
        TransitionTo(AgentState.Error);
        _logger.LogError("Agent {Ext}: registration failed — {Reason}", ExtensionId, reason);
    }

    private void OnRegistrationRemoved(SIPURI uri, SIPResponse response)
    {
        if (_state == AgentState.Idle)
        {
            TransitionTo(AgentState.Offline);
        }

        _logger.LogWarning("Agent {Ext}: registration removed", ExtensionId);
    }

    private void OnRegistrationTemporaryFailure(SIPURI uri, SIPResponse? response, string reason)
    {
        _logger.LogWarning("Agent {Ext}: registration temporary failure — {Reason}", ExtensionId, reason);
    }

    // -------------------------------------------------------------------------
    // Private: call event handlers
    // -------------------------------------------------------------------------

    private void OnCallHungup(SIPDialogue dialogue)
    {
        _logger.LogInformation("Agent {Ext}: remote hangup", ExtensionId);
        CancelAutoHangupTimer();
        _ = CleanupCallAndWrapupAsync();
    }

    // -------------------------------------------------------------------------
    // Private: call lifecycle helpers
    // -------------------------------------------------------------------------

    private async Task AnswerInviteAsync(SIPRequest request)
    {
        if (_userAgent is null) return;

        try
        {
            var uas = _userAgent.AcceptCall(request);
            _mediaSession = new VoIPMediaSession();
            bool answered = await _userAgent.Answer(uas, _mediaSession, publicIpAddress: null);

            if (answered)
            {
                await _mediaSession.Start();
                _pendingInvite = null;
                TransitionTo(AgentState.InCall);
                CallsHandled++;
                LastCallTime = DateTime.UtcNow;

                _logger.LogInformation("Agent {Ext}: call answered (total: {Count})", ExtensionId, CallsHandled);

                // Schedule auto-hangup after configured talk time (cancellable by remote hangup)
                CancelAutoHangupTimer();
                _autoHangupCts = new CancellationTokenSource();
                _ = AutoHangupAfterTalkTimeAsync(_autoHangupCts.Token);
            }
            else
            {
                _logger.LogWarning("Agent {Ext}: Answer() returned false — cleaning up", ExtensionId);
                _pendingInvite = null;
                await CleanupCallAsync();
                TransitionTo(AgentState.Idle);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent {Ext}: error in AnswerInviteAsync", ExtensionId);
            _pendingInvite = null;
            await CleanupCallAsync();
            TransitionTo(AgentState.Idle);
        }
    }

    private async Task AutoHangupAfterTalkTimeAsync(CancellationToken ct)
    {
        // Apply ±variance% to talk time for human-like behavior
        int baseTalkTime = _behavior.TalkTimeSecs;
        double variance = baseTalkTime * _behavior.TalkTimeVariancePercent / 100.0;
        int actualTalkTime = baseTalkTime + _random.Next((int)-variance, (int)variance + 1);
        actualTalkTime = Math.Max(1, actualTalkTime);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(actualTalkTime), ct);
        }
        catch (OperationCanceledException)
        {
            return; // Remote hangup already handled cleanup
        }

        if (_state == AgentState.InCall || _state == AgentState.OnHold)
        {
            _logger.LogInformation("Agent {Ext}: talk time elapsed, hanging up", ExtensionId);
            await HangupAsync();
        }
    }

    private void CancelAutoHangupTimer()
    {
        if (_autoHangupCts is null) return;
        _autoHangupCts.Cancel();
        _autoHangupCts.Dispose();
        _autoHangupCts = null;
    }

    private async Task BeginWrapupAsync()
    {
        TransitionTo(AgentState.Wrapup);

        if (_wrapupCts is not null)
        {
            await _wrapupCts.CancelAsync();
            _wrapupCts.Dispose();
        }
        _wrapupCts = new CancellationTokenSource();
        var token = _wrapupCts.Token;

        try
        {
            int wrapupTime = _random.Next(_behavior.WrapupTimeSecs, _behavior.WrapupMaxSecs + 1);
            await Task.Delay(TimeSpan.FromSeconds(wrapupTime), token);

            if (!token.IsCancellationRequested)
            {
                TransitionTo(AgentState.Idle);
                _logger.LogDebug("Agent {Ext}: wrapup complete, now idle", ExtensionId);
            }
        }
        catch (OperationCanceledException)
        {
            // Wrapup cancelled — state managed externally (e.g. DisposeAsync)
        }
    }

    private async Task CleanupCallAndWrapupAsync()
    {
        await CleanupCallAsync();
        await BeginWrapupAsync();
    }

    private Task CleanupCallAsync()
    {
        if (_mediaSession is not null)
        {
            _mediaSession.Close(null);
            _mediaSession.Dispose();
            _mediaSession = null;
        }

        return Task.CompletedTask;
    }

    // -------------------------------------------------------------------------
    // Private: state machine
    // -------------------------------------------------------------------------

    private void TransitionTo(AgentState newState)
    {
        if (_state == newState) return;

        var previous = _state;
        _state = newState;
        _logger.LogDebug("Agent {Ext}: {From} → {To}", ExtensionId, previous, newState);

        StateChanged?.Invoke(this, previous, newState);
    }

    /// <summary>
    /// Returns a registration expiry value between 90 and 150 seconds,
    /// deterministically staggered by agent index to avoid thundering-herd
    /// re-registration when running hundreds of agents.
    /// </summary>
    internal static int CalculateStaggeredExpiry(int agentIndex)
    {
        // Spread across 90-150s range using modulo for deterministic distribution
        return 90 + (agentIndex * 7 % 61);
    }
}
