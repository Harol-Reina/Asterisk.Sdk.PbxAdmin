using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PbxAdmin.LoadTests.Configuration;
using PbxAdmin.LoadTests.Metrics;

namespace PbxAdmin.LoadTests.CallGeneration;

/// <summary>
/// Maintains a target number of concurrent calls by generating new calls
/// as existing ones complete. Supports ramp-up and scenario-based destination selection.
/// </summary>
public sealed class CallPatternScheduler : IAsyncDisposable
{
    private const string ConferenceDestination = "801";
    private const string DefaultDestination = "105";

    private readonly CallPatternOptions _options;
    private readonly CallGeneratorService _generator;
    private readonly ILogger<CallPatternScheduler> _logger;
    private readonly Random _random = new();
    private MetricsCollector? _metrics;

    private int _activeCalls;
    private int _totalCallsGenerated;
    private int _totalCallsCompleted;
    private int _targetConcurrent;
    private bool _isRunning;

    private CancellationTokenSource? _cts;
    private Task? _backgroundLoop;
    private Task? _statsLoop;
    private DateTime _startedAt;

    private readonly Dictionary<string, int> _scenarioCounts = new();
    private readonly object _statsLock = new();

    public int ActiveCalls => _activeCalls;
    public int TotalCallsGenerated => _totalCallsGenerated;
    public int TargetConcurrent => _targetConcurrent;
    public bool IsRunning => _isRunning;

    public event Action<SchedulerStats>? StatsUpdated;

    /// <summary>
    /// Attaches a MetricsCollector so call generation events (originated, started,
    /// ended, failed) are tracked for the report. Call before StartAsync.
    /// </summary>
    public void AttachMetrics(MetricsCollector metrics) => _metrics = metrics;

    public CallPatternScheduler(
        IOptions<CallPatternOptions> options,
        CallGeneratorService generator,
        ILogger<CallPatternScheduler> logger)
    {
        _options = options.Value;
        _generator = generator;
        _logger = logger;

        foreach (var scenario in _options.ScenarioMix.Keys)
            _scenarioCounts[scenario] = 0;
    }

    /// <summary>
    /// Starts the scheduler, ramping up to <paramref name="targetConcurrent"/> calls
    /// over <see cref="CallPatternOptions.RampUpMinutes"/> minutes.
    /// Runs until the cancellation token is cancelled or
    /// <see cref="CallPatternOptions.TestDurationMinutes"/> expires.
    /// </summary>
    public async Task StartAsync(int targetConcurrent, CancellationToken ct)
    {
        if (targetConcurrent > _options.MaxConcurrentCalls)
            throw new ArgumentOutOfRangeException(
                nameof(targetConcurrent),
                $"targetConcurrent ({targetConcurrent}) exceeds MaxConcurrentCalls ({_options.MaxConcurrentCalls}).");

        _targetConcurrent = targetConcurrent;
        _startedAt = DateTime.UtcNow;
        _isRunning = true;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cts.CancelAfter(TimeSpan.FromMinutes(_options.TestDurationMinutes));

        _logger.LogInformation(
            "Scheduler starting: target={Target}, rampUp={RampUp}m, duration={Duration}m",
            targetConcurrent, _options.RampUpMinutes, _options.TestDurationMinutes);

        _backgroundLoop = RunMainLoopAsync(_cts.Token);
        _statsLoop = RunStatsLoopAsync(_cts.Token);

        await Task.CompletedTask;
    }

    /// <summary>Signals stop and waits for the scheduler to finish.</summary>
    public async Task StopAsync()
    {
        if (_cts is not null && !_cts.IsCancellationRequested)
            await _cts.CancelAsync();

        if (_backgroundLoop is not null)
        {
            try { await _backgroundLoop; }
            catch (OperationCanceledException) { }
        }

        if (_statsLoop is not null)
        {
            try { await _statsLoop; }
            catch (OperationCanceledException) { }
        }

        _isRunning = false;
        _logger.LogInformation(
            "Scheduler stopped. Generated={Generated}, Completed={Completed}",
            _totalCallsGenerated, _totalCallsCompleted);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts?.Dispose();
    }

    // -------------------------------------------------------------------------
    // Internal pure helpers — exposed as internal static for unit testing
    // -------------------------------------------------------------------------

    /// <summary>
    /// Picks a scenario name using weighted random selection from <paramref name="scenarioMix"/>.
    /// </summary>
    internal static string PickScenario(Dictionary<string, int> scenarioMix, Random random)
    {
        int total = 0;
        foreach (var weight in scenarioMix.Values)
            total += weight;

        int roll = random.Next(total);
        int cumulative = 0;

        foreach (var (scenario, weight) in scenarioMix)
        {
            cumulative += weight;
            if (roll < cumulative)
                return scenario;
        }

        // Fallback: return the last entry (should never reach here with valid weights)
        string last = "NormalAnswer";
        foreach (var key in scenarioMix.Keys)
            last = key;
        return last;
    }

    /// <summary>
    /// Returns the destination extension for the given scenario name.
    /// Conference → "801", all others → "200".
    /// </summary>
    internal static string PickScenarioDestination(string scenario) =>
        string.Equals(scenario, "Conference", StringComparison.Ordinal)
            ? ConferenceDestination
            : DefaultDestination;

    /// <summary>
    /// Returns a call duration in seconds appropriate for the given scenario.
    /// Uses <paramref name="random"/> for the random element.
    /// </summary>
    internal static int PickCallDuration(
        string scenario,
        int defaultDurationSecs,
        int minDurationSecs,
        int maxDurationSecs,
        Random random)
    {
        return scenario switch
        {
            "ShortCall" => random.Next(10, 31),                        // 10–30 s
            "LongCall" => random.Next(600, 901),                       // 600–900 s
            "NormalAnswer" => PickNormalDuration(defaultDurationSecs, random),
            _ => Clamp(defaultDurationSecs, minDurationSecs, maxDurationSecs)
        };
    }

    /// <summary>
    /// Calculates the current ramp target using linear interpolation.
    /// Returns 0 at the start, <paramref name="targetConcurrent"/> after <paramref name="rampUpMinutes"/>.
    /// </summary>
    internal static int CalculateRampTarget(
        TimeSpan elapsed,
        int targetConcurrent,
        int rampUpMinutes)
    {
        if (rampUpMinutes <= 0)
            return targetConcurrent;

        double rampSecs = rampUpMinutes * 60.0;
        double fraction = elapsed.TotalSeconds / rampSecs;

        if (fraction >= 1.0)
            return targetConcurrent;

        if (fraction <= 0.0)
            return 0;

        return (int)Math.Round(fraction * targetConcurrent);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static int PickNormalDuration(int defaultSecs, Random random)
    {
        // ±50% of default
        int low = (int)(defaultSecs * 0.5);
        int high = (int)(defaultSecs * 1.5);
        return random.Next(low, high + 1);
    }

    private static int Clamp(int value, int min, int max) =>
        value < min ? min : value > max ? max : value;

    // -------------------------------------------------------------------------
    // Background loops
    // -------------------------------------------------------------------------

    private async Task RunMainLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TimeSpan elapsed = DateTime.UtcNow - _startedAt;
            int currentTarget = CalculateRampTarget(elapsed, _targetConcurrent, _options.RampUpMinutes);

            int active = Volatile.Read(ref _activeCalls);
            int deficit = currentTarget - active;

            if (deficit > 0)
            {
                _logger.LogDebug(
                    "Deficit={Deficit}, Active={Active}, Target={Target}",
                    deficit, active, currentTarget);

                for (int i = 0; i < deficit; i++)
                    _ = GenerateAndTrackCallAsync(ct);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task GenerateAndTrackCallAsync(CancellationToken ct)
    {
        string scenario = PickScenario(_options.ScenarioMix, _random);
        string destination = PickScenarioDestination(scenario);
        int durationSecs = PickCallDuration(
            scenario,
            _options.DefaultCallDurationSecs,
            _options.MinCallDurationSecs,
            _options.MaxCallDurationSecs,
            _random);

        Interlocked.Increment(ref _activeCalls);
        Interlocked.Increment(ref _totalCallsGenerated);
        _metrics?.RecordCallOriginated();
        _metrics?.RecordCallStarted();

        lock (_statsLock)
        {
            if (_scenarioCounts.ContainsKey(scenario))
                _scenarioCounts[scenario]++;
        }

        try
        {
            var result = await _generator.GenerateCallAsync(destination, cancellationToken: ct);

            if (!result.Accepted)
            {
                _logger.LogWarning(
                    "Call {CallId} not accepted: {Error}", result.CallId, result.ErrorMessage);
                _metrics?.RecordCallFailed();
            }

            // Simulate call duration — in load test scenarios the PSTN emulator
            // actually places the call; we hold the "active" slot for the expected duration
            // so the scheduler does not over-generate.
            await Task.Delay(TimeSpan.FromSeconds(durationSecs), ct);
        }
        catch (OperationCanceledException)
        {
            // Scheduler is stopping; just release the slot
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating call for scenario {Scenario}", scenario);
            _metrics?.RecordCallFailed();
        }
        finally
        {
            Interlocked.Decrement(ref _activeCalls);
            Interlocked.Increment(ref _totalCallsCompleted);
            _metrics?.RecordCallEnded();
        }
    }

    private async Task RunStatsLoopAsync(CancellationToken ct)
    {
        var statsTimer = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            TimeSpan elapsed = DateTime.UtcNow - _startedAt;
            TimeSpan remaining = TimeSpan.FromMinutes(_options.TestDurationMinutes) - elapsed;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

            double windowSecs = (DateTime.UtcNow - statsTimer).TotalSeconds;
            statsTimer = DateTime.UtcNow;

            Dictionary<string, int> countSnapshot;
            lock (_statsLock)
                countSnapshot = new Dictionary<string, int>(_scenarioCounts);

            double cpm = windowSecs > 0
                ? _totalCallsGenerated / elapsed.TotalMinutes
                : 0;

            var stats = new SchedulerStats
            {
                ActiveCalls = Volatile.Read(ref _activeCalls),
                TargetConcurrent = CalculateRampTarget(elapsed, _targetConcurrent, _options.RampUpMinutes),
                TotalGenerated = _totalCallsGenerated,
                TotalCompleted = _totalCallsCompleted,
                CallsPerMinuteActual = Math.Round(cpm, 2),
                Elapsed = elapsed,
                Remaining = remaining,
                ScenarioCounts = countSnapshot,
                Timestamp = DateTime.UtcNow
            };

            _logger.LogInformation(
                "Stats: Active={Active}/{Target}, Generated={Generated}, Elapsed={Elapsed:mm\\:ss}",
                stats.ActiveCalls, stats.TargetConcurrent, stats.TotalGenerated, stats.Elapsed);

            StatsUpdated?.Invoke(stats);
        }
    }
}
