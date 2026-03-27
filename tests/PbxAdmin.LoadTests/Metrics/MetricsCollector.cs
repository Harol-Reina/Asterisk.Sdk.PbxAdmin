using Microsoft.Extensions.Logging;

namespace PbxAdmin.LoadTests.Metrics;

/// <summary>
/// Thread-safe aggregator of real-time metrics during a test run.
/// </summary>
public sealed class MetricsCollector
{
    private readonly ILogger<MetricsCollector> _logger;

    private int _callsOriginated;
    private int _callsAnswered;
    private int _callsFailed;
    private int _currentActiveCalls;
    private int _peakConcurrentCalls;
    private int _totalAgents;
    private int _currentAgentsInCall;
    private int _peakAgentsInCall;
    private int _agentErrors;

    public MetricsCollector(ILogger<MetricsCollector> logger)
    {
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // Real-time counters (thread-safe reads via Volatile)
    // -------------------------------------------------------------------------

    public int CallsOriginated => Volatile.Read(ref _callsOriginated);
    public int CallsAnswered => Volatile.Read(ref _callsAnswered);
    public int CallsFailed => Volatile.Read(ref _callsFailed);
    public int CurrentActiveCalls => Volatile.Read(ref _currentActiveCalls);
    public int PeakConcurrentCalls => Volatile.Read(ref _peakConcurrentCalls);
    public int TotalAgents => Volatile.Read(ref _totalAgents);
    public int PeakAgentsInCall => Volatile.Read(ref _peakAgentsInCall);
    public int AgentErrors => Volatile.Read(ref _agentErrors);

    // -------------------------------------------------------------------------
    // Recording methods
    // -------------------------------------------------------------------------

    public void RecordCallOriginated()
    {
        int n = Interlocked.Increment(ref _callsOriginated);
        _logger.LogDebug("CallsOriginated={N}", n);
    }

    public void RecordCallAnswered()
    {
        int n = Interlocked.Increment(ref _callsAnswered);
        _logger.LogDebug("CallsAnswered={N}", n);
    }

    public void RecordCallFailed()
    {
        int n = Interlocked.Increment(ref _callsFailed);
        _logger.LogDebug("CallsFailed={N}", n);
    }

    /// <summary>Increments the active call counter and updates the peak if needed.</summary>
    public void RecordCallStarted()
    {
        int active = Interlocked.Increment(ref _currentActiveCalls);
        UpdatePeak(active);
        _logger.LogDebug("CallStarted: active={Active}", active);
    }

    /// <summary>Decrements the active call counter.</summary>
    public void RecordCallEnded()
    {
        int active = Interlocked.Decrement(ref _currentActiveCalls);
        _logger.LogDebug("CallEnded: active={Active}", active);
    }

    // -------------------------------------------------------------------------
    // Agent tracking
    // -------------------------------------------------------------------------

    public void SetTotalAgents(int count) => Volatile.Write(ref _totalAgents, count);

    public void RecordAgentBusy()
    {
        int current = Interlocked.Increment(ref _currentAgentsInCall);
        UpdatePeakAgents(current);
    }

    public void RecordAgentFree()
    {
        Interlocked.Decrement(ref _currentAgentsInCall);
    }

    public void RecordAgentError()
    {
        Interlocked.Increment(ref _agentErrors);
    }

    // -------------------------------------------------------------------------
    // Summary
    // -------------------------------------------------------------------------

    public MetricsSummary GetSummary(TimeSpan elapsed)
    {
        int originated = CallsOriginated;
        double cpm = elapsed.TotalMinutes > 0
            ? originated / elapsed.TotalMinutes
            : 0;

        return new MetricsSummary
        {
            CallsOriginated = originated,
            CallsAnswered = CallsAnswered,
            CallsFailed = CallsFailed,
            PeakConcurrentCalls = PeakConcurrentCalls,
            CallsPerMinute = Math.Round(cpm, 2),
            Elapsed = elapsed,
            TotalAgents = TotalAgents,
            PeakAgentsInCall = PeakAgentsInCall,
            AgentErrors = AgentErrors
        };
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private void UpdatePeak(int current)
    {
        int observed = Volatile.Read(ref _peakConcurrentCalls);
        while (current > observed)
        {
            int previous = Interlocked.CompareExchange(ref _peakConcurrentCalls, current, observed);
            if (previous == observed)
                break;
            observed = previous;
        }
    }

    private void UpdatePeakAgents(int current)
    {
        int observed = Volatile.Read(ref _peakAgentsInCall);
        while (current > observed)
        {
            int previous = Interlocked.CompareExchange(ref _peakAgentsInCall, current, observed);
            if (previous == observed)
                break;
            observed = previous;
        }
    }
}
