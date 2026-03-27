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

    // Readiness gate configuration
    internal const int MinReadyPercent = 80;
    internal const int MaxRetryWaves = 2;
    internal const int ReadinessTimeoutSecs = 60;
    internal const int ReadinessPollIntervalSecs = 2;

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
    /// Creates N agents sharing one SIPTransport, registers them with Asterisk
    /// in batches of 10, and logs a summary on completion.
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
        // Bind to an ephemeral port by passing port 0.
        _transport.AddSIPChannel(new SIPUDPChannel(IPAddress.Any, 0));
        _transport.SIPTransportRequestReceived += OnTransportRequestReceived;

        _agents = new List<SipAgent>(agentCount);

        var serverHost = _loadOptions.TargetPbxAmi.Host;

        for (int i = 0; i < agentCount; i++)
        {
            var (extensionId, password) = GetAgentCredentials(i, _loadOptions.TargetServer);
            var agentLogger = _loggerFactory.CreateLogger($"SipAgent.{extensionId}");
            int sipPort = GetSipPort(_loadOptions.TargetServer);

            var agent = new SipAgent(
                extensionId,
                password,
                serverHost,
                sipPort,
                _transport,
                _behaviorOptions,
                agentLogger);

            _agents.Add(agent);
        }

        // Register in adaptive batches to balance speed vs Asterisk load
        int batchSize = CalculateBatchSize(agentCount);
        _logger.LogInformation("Registering {N} agents in batches of {Batch}", agentCount, batchSize);

        for (int i = 0; i < _agents.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();

            var batch = _agents.Skip(i).Take(batchSize).ToList();
            await Task.WhenAll(batch.Select(a => a.RegisterAsync(ct)));
        }

        // Wire metrics and mark started before readiness gate so state
        // transitions are tracked even if the gate fails
        _metrics?.SetTotalAgents(agentCount);
        foreach (var agent in _agents)
            WireAgentMetrics(agent);
        _started = true;

        // Wait for agents to register and retry failures
        await WaitForReadyAsync(ct);

        int idle = IdleAgents;
        int errors = _agents.Count(a => a.State == AgentState.Error);
        _logger.LogInformation("Registered {N} agents ({Idle} idle, {Errors} errors)", agentCount, idle, errors);
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

    /// <summary>
    /// Polls agent state until enough are Idle, retries agents in Error state,
    /// and ensures a minimum percentage of agents are Idle before returning.
    /// </summary>
    private async Task WaitForReadyAsync(CancellationToken ct)
    {
        int total = TotalAgents;

        // Phase 1: Poll until agents settle (Idle or Error, not Registering)
        _logger.LogInformation("Waiting up to {Secs}s for {N} agents to register (polling every {Poll}s)...",
            ReadinessTimeoutSecs, total, ReadinessPollIntervalSecs);

        await PollUntilSettledAsync(total, "Initial registration", ct);
        LogReadinessStatus("After initial wait");

        // Phase 2: Retry waves for agents stuck in Error
        await RetryErrorAgentsAsync(ct);

        // Phase 3: Final readiness check
        int finalIdle = IdleAgents;
        int finalReadyPercent = total > 0 ? finalIdle * 100 / total : 0;

        if (finalReadyPercent < MinReadyPercent)
        {
            int errors = _agents.Count(a => a.State == AgentState.Error);
            int registering = _agents.Count(a => a.State == AgentState.Registering);

            throw new InvalidOperationException(
                $"Agent readiness check failed: {finalIdle}/{total} agents Idle ({finalReadyPercent}%), " +
                $"minimum required {MinReadyPercent}%. " +
                $"({errors} Error, {registering} still Registering)");
        }

        _logger.LogInformation(
            "Readiness gate passed: {Idle}/{Total} agents Idle ({Percent}%)",
            finalIdle, total, finalReadyPercent);
    }

    private async Task PollUntilSettledAsync(int total, string phase, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(ReadinessTimeoutSecs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(ReadinessPollIntervalSecs), ct);

            int idle = IdleAgents;
            int registering = _agents.Count(a => a.State == AgentState.Registering);
            int readyPercent = total > 0 ? idle * 100 / total : 0;

            if (registering == 0 || readyPercent >= MinReadyPercent)
            {
                LogReadinessStatus(phase);
                return;
            }
        }
    }

    private async Task RetryErrorAgentsAsync(CancellationToken ct)
    {
        for (int wave = 1; wave <= MaxRetryWaves; wave++)
        {
            var errorAgents = _agents.Where(a => a.State == AgentState.Error).ToList();
            if (errorAgents.Count == 0) break;

            _logger.LogInformation(
                "Retry wave {Wave}/{Max}: re-registering {Count} agents in Error state",
                wave, MaxRetryWaves, errorAgents.Count);

            const int retryBatchSize = 10;
            for (int i = 0; i < errorAgents.Count; i += retryBatchSize)
            {
                ct.ThrowIfCancellationRequested();
                var batch = errorAgents.Skip(i).Take(retryBatchSize).ToList();
                await Task.WhenAll(batch.Select(a => a.RetryRegisterAsync(ct)));
            }

            await PollUntilSettledAsync(TotalAgents, $"Retry wave {wave}", ct);
        }
    }

    private void LogReadinessStatus(string phase)
    {
        int idle = IdleAgents;
        int errors = _agents.Count(a => a.State == AgentState.Error);
        int registering = _agents.Count(a => a.State == AgentState.Registering);
        int total = TotalAgents;

        _logger.LogInformation(
            "[{Phase}] Agents: {Idle}/{Total} Idle, {Errors} Error, {Registering} Registering",
            phase, idle, total, errors, registering);
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
