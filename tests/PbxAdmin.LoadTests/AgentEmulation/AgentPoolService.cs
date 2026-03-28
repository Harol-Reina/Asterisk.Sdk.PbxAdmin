using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PbxAdmin.LoadTests.Configuration;
using PbxAdmin.LoadTests.Metrics;
using SIPSorcery.SIP;

namespace PbxAdmin.LoadTests.AgentEmulation;

/// <summary>
/// Manages a pool of SipAgent instances sharing a single SIPTransport.
/// Handles lifecycle (start/stop), INVITE dispatch, and pool statistics.
/// Supports 20-300 agents concurrently.
/// </summary>
public sealed class AgentPoolService : IAsyncDisposable
{
    private readonly LoadTestOptions _loadOptions;
    private readonly AgentBehaviorOptions _behaviorOptions;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AgentPoolService> _logger;
    private MetricsCollector? _metrics;

    private SIPTransport? _transport;
    private List<SipAgent> _agents = [];
    private bool _started;




    public AgentPoolService(
        IOptions<LoadTestOptions> loadOptions,
        IOptions<AgentBehaviorOptions> behaviorOptions,
        ILoggerFactory loggerFactory)
    {
        _loadOptions = loadOptions.Value;
        _behaviorOptions = behaviorOptions.Value;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<AgentPoolService>();
    }

    // -------------------------------------------------------------------------
    // Properties
    // -------------------------------------------------------------------------

    public int TotalAgents => _agents.Count;
    public int IdleAgents => _agents.Count(a => a.State == AgentState.Idle);
    public int InCallAgents => _agents.Count(a => a.State == AgentState.InCall || a.State == AgentState.OnHold);
    public int RingingAgents => _agents.Count(a => a.State == AgentState.Ringing);
    public IReadOnlyList<SipAgent> Agents => _agents.AsReadOnly();

    /// <summary>
    /// Prevents all agents from accepting new calls. Existing calls continue
    /// until they complete naturally. Used during test drain phase.
    /// </summary>
    public void BeginDrain()
    {
        foreach (var agent in _agents)
            agent.BeginDrain();
    }

    // -------------------------------------------------------------------------
    // Metrics binding
    // -------------------------------------------------------------------------

    /// <summary>
    /// Attaches a MetricsCollector so that agent state transitions automatically
    /// update peak-in-call, calls-answered, and error counters. Call this once
    /// before any StartAsync — the binding persists across restarts.
    /// </summary>
    public void AttachMetrics(MetricsCollector metrics) => _metrics = metrics;

    private void WireAgentMetrics(SipAgent agent)
    {
        if (_metrics is null) return;

        agent.StateChanged += (a, previousState, newState) =>
        {
            switch (newState)
            {
                case AgentState.InCall:
                    _metrics.RecordAgentBusy();
                    _metrics.RecordCallAnswered();
                    break;
                case AgentState.Wrapup or AgentState.Idle
                    when previousState is AgentState.InCall or AgentState.OnHold:
                    _metrics.RecordAgentFree();
                    break;
                case AgentState.Error:
                    _metrics.RecordAgentError();
                    break;
            }
        };
    }

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates N agents sharing one SIPTransport, registers them in progressive
    /// waves with per-wave readiness polling, and wires metrics.
    /// </summary>
    public async Task StartAsync(int agentCount, CancellationToken ct)
    {
        if (agentCount < _behaviorOptions.MinAgents)
            throw new ArgumentOutOfRangeException(nameof(agentCount),
                $"agentCount {agentCount} is below MinAgents {_behaviorOptions.MinAgents}.");

        if (agentCount > _behaviorOptions.MaxAgents)
            throw new ArgumentOutOfRangeException(nameof(agentCount),
                $"agentCount {agentCount} exceeds MaxAgents {_behaviorOptions.MaxAgents}.");

        _transport = new SIPTransport();
        _transport.AddSIPChannel(new SIPUDPChannel(IPAddress.Any, 0));
        _transport.SIPTransportRequestReceived += OnTransportRequestReceived;

        _agents = new List<SipAgent>(agentCount);
        var serverHost = _loadOptions.TargetPbxAmi.Host;
        int sipPort = GetSipPort(_loadOptions.TargetServer);

        // Create all agents upfront (lightweight, no registration yet)
        for (int i = 0; i < agentCount; i++)
        {
            var (extensionId, password) = GetAgentCredentials(i, _loadOptions.TargetServer);
            var agentLogger = _loggerFactory.CreateLogger($"SipAgent.{extensionId}");

            var agent = new SipAgent(
                extensionId, password, serverHost, sipPort,
                _transport, _behaviorOptions, agentLogger);

            _agents.Add(agent);
        }

        // Wire metrics before waves so all state transitions are tracked
        _metrics?.SetTotalAgents(agentCount);
        foreach (var agent in _agents)
            WireAgentMetrics(agent);
        _started = true;

        // Register in progressive waves
        int waveSize = _behaviorOptions.WaveSize;
        int waveCount = CalculateWaveCount(agentCount, waveSize);

        _logger.LogInformation(
            "Registering {N} agents in {Waves} waves of {Size} (adaptive interval)",
            agentCount, waveCount, waveSize);

        for (int w = 0; w < waveCount; w++)
        {
            ct.ThrowIfCancellationRequested();
            int start = w * waveSize;
            int end = Math.Min(start + waveSize, agentCount);
            var wave = _agents.GetRange(start, end - start);

            _logger.LogInformation("Wave {Wave}/{Total}: registering agents {First}-{Last}",
                w + 1, waveCount, _agents[start].ExtensionId, _agents[end - 1].ExtensionId);

            // Fire registration for all agents in wave
            foreach (var agent in wave)
                await agent.RegisterAsync(ct);

            // Poll until wave settles
            await WaitForWaveReadyAsync(wave, w + 1, ct);

            // Stabilization delay between waves (skip after last wave)
            if (w < waveCount - 1)
                await Task.Delay(TimeSpan.FromSeconds(_behaviorOptions.WaveStabilizationSecs), ct);
        }

        int idle = IdleAgents;
        int errors = _agents.Count(a => a.State == AgentState.Error);
        _logger.LogInformation("All waves complete: {N} agents ({Idle} idle, {Errors} errors)", agentCount, idle, errors);
    }

    /// <summary>
    /// Unregisters all agents, shuts down the transport, and disposes agents.
    /// </summary>
    public async Task StopAsync()
    {
        if (!_started) return;

        await Task.WhenAll(_agents.Select(a => a.UnregisterAsync()));

        if (_transport is not null)
        {
            _transport.SIPTransportRequestReceived -= OnTransportRequestReceived;
            _transport.Shutdown();
        }

        await Task.WhenAll(_agents.Select(a => a.DisposeAsync().AsTask()));

        _agents.Clear();
        _started = false;
        _logger.LogInformation("AgentPoolService stopped: all agents unregistered and disposed");
    }

    private async Task WaitForWaveReadyAsync(List<SipAgent> wave, int waveNumber, CancellationToken ct)
    {
        int total = wave.Count;
        int required = (int)Math.Ceiling(total * WaveReadyPercent / 100.0);
        var deadline = DateTime.UtcNow.AddSeconds(WaveReadinessTimeoutSecs);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(WaveReadinessPollSecs), ct);

            int idle = wave.Count(a => a.State == AgentState.Idle);
            int errors = wave.Count(a => a.State == AgentState.Error);
            int registering = wave.Count(a => a.State == AgentState.Registering);

            if (idle >= required || registering == 0)
            {
                _logger.LogInformation(
                    "Wave {Wave} ready: {Idle}/{Total} Idle, {Errors} Error",
                    waveNumber, idle, total, errors);
                return;
            }
        }

        // Timeout — log warning but continue (agents will register via SIPSorcery retry)
        int finalIdle = wave.Count(a => a.State == AgentState.Idle);
        _logger.LogWarning(
            "Wave {Wave} timeout: {Idle}/{Total} Idle after {Timeout}s — continuing",
            waveNumber, finalIdle, total, WaveReadinessTimeoutSecs);
    }

    // -------------------------------------------------------------------------
    // Query
    // -------------------------------------------------------------------------

    public SipAgent? GetAgent(string extensionId)
        => _agents.FirstOrDefault(a => a.ExtensionId == extensionId);

    public IEnumerable<SipAgent> GetIdleAgents()
        => _agents.Where(a => a.State == AgentState.Idle);

    public IEnumerable<SipAgent> GetBusyAgents()
        => _agents.Where(a => a.State == AgentState.InCall || a.State == AgentState.OnHold);

    // -------------------------------------------------------------------------
    // Stats
    // -------------------------------------------------------------------------

    public AgentPoolStats GetStats() => new()
    {
        Total = _agents.Count,
        Idle = _agents.Count(a => a.State == AgentState.Idle),
        Ringing = _agents.Count(a => a.State == AgentState.Ringing),
        InCall = _agents.Count(a => a.State == AgentState.InCall),
        OnHold = _agents.Count(a => a.State == AgentState.OnHold),
        Wrapup = _agents.Count(a => a.State == AgentState.Wrapup),
        Error = _agents.Count(a => a.State == AgentState.Error),
        TotalCallsHandled = _agents.Sum(a => a.CallsHandled),
        Timestamp = DateTime.UtcNow
    };

    // -------------------------------------------------------------------------
    // IAsyncDisposable
    // -------------------------------------------------------------------------

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _transport?.Dispose();
        _transport = null;
    }

    // -------------------------------------------------------------------------
    // Internal helpers (testable without SIP)
    // -------------------------------------------------------------------------

    internal const int WaveReadyPercent = 80;
    internal const int WaveReadinessTimeoutSecs = 60;
    internal const int WaveReadinessPollSecs = 2;

    /// <summary>
    /// Returns the extension ID and password for the Nth agent (zero-based index)
    /// based on the target server type.
    /// Realtime: 2100-2399, password loadtest{ext}
    /// File:     4100-4399, password loadtest{ext}
    /// </summary>
    /// <summary>
    /// Returns the registration batch size scaled to the total agent count.
    /// Smaller pools use smaller batches to avoid overwhelming Asterisk;
    /// larger pools use bigger batches to reduce total registration time.
    /// </summary>
    internal static int CalculateBatchSize(int agentCount) => agentCount switch
    {
        <= 50 => 10,
        <= 150 => 20,
        _ => 30
    };

    internal static int CalculateWaveCount(int agentCount, int waveSize)
        => (agentCount + waveSize - 1) / waveSize;

    internal static int CalculateMinDurationMinutes(int agentCount, int waveSize)
        => CalculateWaveCount(agentCount, waveSize) + 5;

    internal static (string extensionId, string password) GetAgentCredentials(int index, string targetServer)
    {
        int baseExtension = targetServer.Equals("file", StringComparison.OrdinalIgnoreCase)
            ? 4100
            : 2100;

        string extensionId = (baseExtension + index).ToString();
        string password = $"loadtest{extensionId}";
        return (extensionId, password);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static int GetSipPort(string targetServer)
        => targetServer.Equals("file", StringComparison.OrdinalIgnoreCase) ? 5061 : 5060;

    private async Task OnTransportRequestReceived(SIPEndPoint localSipEndPoint, SIPEndPoint remoteEndPoint, SIPRequest sipRequest)
    {
        // When Asterisk's Queue app picks a winner, it CANCELs the other ringing agents.
        // Transition those agents back to Idle immediately so they don't waste time
        // trying to answer a call that's already gone.
        if (sipRequest.Method == SIPMethodsEnum.CANCEL)
        {
            string? cancelToUser = sipRequest.Header.To?.ToURI?.User;
            if (!string.IsNullOrEmpty(cancelToUser))
            {
                GetAgent(cancelToUser)?.CancelPendingCall();
            }
            return;
        }

        if (sipRequest.Method != SIPMethodsEnum.INVITE)
            return;

        // Extract the user part of the To header (the extension being called)
        string? toUser = sipRequest.Header.To?.ToURI?.User;

        if (string.IsNullOrEmpty(toUser))
        {
            _logger.LogWarning("INVITE received with no To user: dropping");
            return;
        }

        var agent = GetAgent(toUser);

        if (agent is null)
        {
            _logger.LogWarning("INVITE for unknown extension {Ext}: responding 404", toUser);
            var notFound = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.NotFound, null);
            await _transport!.SendResponseAsync(notFound);
            return;
        }

        await agent.HandleIncomingInviteAsync(sipRequest);
    }
}
