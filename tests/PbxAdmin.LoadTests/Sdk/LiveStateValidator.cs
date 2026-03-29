using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Responses;
using Asterisk.Sdk.Live.Server;
using SdkAgentState = Asterisk.Sdk.Live.Agents.AgentState;

namespace PbxAdmin.LoadTests.Sdk;

/// <summary>
/// Periodically samples the SDK's in-memory channel, queue, and agent state and compares
/// against Asterisk's AMI ground truth. Collects samples for post-test drift analysis
/// via <see cref="GetSummary"/>.
/// </summary>
public sealed partial class LiveStateValidator : IAsyncDisposable
{
    private readonly ConcurrentBag<LiveStateSample> _samples = [];
    private CancellationTokenSource? _cts;
    private Task? _samplingLoop;
    private string? _queueName;

    /// <summary>Returns all collected samples as a read-only list.</summary>
    public IReadOnlyList<LiveStateSample> GetSamples() =>
        _samples.ToArray();

    /// <summary>Computes aggregate drift statistics from all collected samples.</summary>
    public LiveStateSummary GetSummary() =>
        LiveStateSummary.Compute(GetSamples());

    /// <summary>
    /// Starts a background loop that collects one <see cref="LiveStateSample"/> per interval.
    /// </summary>
    public Task StartAsync(
        AsteriskServer server,
        IAmiConnection connection,
        int intervalSeconds = 5,
        CancellationToken ct = default,
        string? queueName = null)
    {
        _queueName = queueName;
        _cts?.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _samplingLoop = SamplingLoopAsync(server, connection, intervalSeconds, _cts.Token);
        return Task.CompletedTask;
    }

    /// <summary>Stops the background sampling loop and waits for it to finish.</summary>
    public async Task StopAsync()
    {
        if (_cts is not null)
            await _cts.CancelAsync();

        if (_samplingLoop is not null)
        {
            try
            {
                await _samplingLoop;
            }
            catch (OperationCanceledException)
            {
                // Expected on cancellation
            }

            _samplingLoop = null;
        }

        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private async Task SamplingLoopAsync(AsteriskServer server, IAmiConnection connection, int intervalSeconds, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var sample = await CollectSampleAsync(server, connection, _queueName, ct);
                _samples.Add(sample);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Swallow transient AMI errors; continue sampling
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static async Task<LiveStateSample> CollectSampleAsync(
        AsteriskServer server,
        IAmiConnection connection,
        string? queueName,
        CancellationToken ct)
    {
        int asteriskChannels = await QueryAsteriskChannelCountAsync(connection, ct);
        int sdkChannels = server.Channels.ChannelCount;

        int sdkQueueCallers = 0;
        int sdkQueueMembers = 0;
        int asteriskQueueCallers = 0;
        int asteriskQueueMembers = 0;
        int asteriskAgentsInUse = 0;

        if (queueName is not null)
        {
            // SDK queue state
            var queue = server.Queues.GetByName(queueName);
            if (queue is not null)
            {
                sdkQueueCallers = queue.EntryCount;
                sdkQueueMembers = queue.MemberCount;
            }

            // AMI queue state
            var amiQueue = await QueryAsteriskQueueAsync(connection, queueName, ct);
            asteriskQueueCallers = amiQueue.CallersWaiting;
            asteriskQueueMembers = amiQueue.MemberCount;
            asteriskAgentsInUse = amiQueue.InUseCount;
        }

        // SDK agent state: count agents not Available/Paused (i.e. actively handling calls)
        int sdkAgentsInCall = server.Agents.Agents
            .Count(a => a.State != SdkAgentState.Available && a.State != SdkAgentState.Paused);

        return new LiveStateSample
        {
            Timestamp = DateTime.UtcNow,
            SdkChannelCount = sdkChannels,
            AsteriskChannelCount = asteriskChannels,
            SdkQueueCallerCount = sdkQueueCallers,
            AsteriskQueueCallerCount = asteriskQueueCallers,
            SdkQueueMemberCount = sdkQueueMembers,
            AsteriskQueueMemberCount = asteriskQueueMembers,
            SdkAgentsInCall = sdkAgentsInCall,
            AsteriskAgentsInUse = asteriskAgentsInUse
        };
    }

    private static async Task<int> QueryAsteriskChannelCountAsync(IAmiConnection connection, CancellationToken ct)
    {
        var response = await connection.SendActionAsync<CommandResponse>(
            new CommandAction { Command = "core show channels count" }, ct);
        return ParseFirstInteger(response.Output ?? "");
    }

    /// <summary>
    /// Sends "queue show {queueName}" via AMI and parses callers waiting, member count,
    /// and in-use count from the output.
    /// </summary>
    private static async Task<AsteriskQueueSnapshot> QueryAsteriskQueueAsync(
        IAmiConnection connection,
        string queueName,
        CancellationToken ct)
    {
        var response = await connection.SendActionAsync<CommandResponse>(
            new CommandAction { Command = $"queue show {queueName}" }, ct);

        string output = response.Output ?? "";
        return ParseQueueShowOutput(output);
    }

    internal static AsteriskQueueSnapshot ParseQueueShowOutput(string output)
    {
        int callersWaiting = 0;
        int memberCount = 0;
        int inUseCount = 0;

        // Parse "has N calls" for callers waiting
        var callsMatch = CallsWaitingRegex().Match(output);
        if (callsMatch.Success && int.TryParse(callsMatch.Groups[1].Value, out int calls))
            callersWaiting = calls;

        // Parse member lines: lines containing device state indicators
        var lines = output.Split('\n');
        foreach (var line in lines)
        {
            string trimmed = line.Trim();

            // Member lines contain state indicators like "(In use)", "(Not in use)", etc.
            if (trimmed.Contains("(In use)", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("(Not in use)", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("(Unavailable)", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("(Ringing)", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("(Ring, in use)", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("(On Hold)", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("(Busy)", StringComparison.OrdinalIgnoreCase))
            {
                memberCount++;

                // Count in-use (actively handling calls)
                if (trimmed.Contains("(In use)", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Contains("(Ringing)", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Contains("(Ring, in use)", StringComparison.OrdinalIgnoreCase))
                {
                    inUseCount++;
                }
            }
        }

        return new AsteriskQueueSnapshot(callersWaiting, memberCount, inUseCount);
    }

    [GeneratedRegex(@"has\s+(\d+)\s+calls?", RegexOptions.IgnoreCase)]
    private static partial Regex CallsWaitingRegex();

    private static int ParseFirstInteger(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        int start = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsDigit(text[i]))
            {
                start = i;
                break;
            }
        }

        if (start < 0)
            return 0;

        int end = start;
        while (end < text.Length && char.IsDigit(text[end]))
            end++;

        return int.TryParse(text[start..end], out int value) ? value : 0;
    }
}

/// <summary>Parsed result from AMI "queue show" command.</summary>
internal readonly record struct AsteriskQueueSnapshot(int CallersWaiting, int MemberCount, int InUseCount);

/// <summary>
/// A single point-in-time comparison between SDK state and Asterisk's AMI ground truth
/// for channels, queues, and agents.
/// </summary>
public sealed record LiveStateSample
{
    public required DateTime Timestamp { get; init; }
    public int SdkChannelCount { get; init; }
    public int AsteriskChannelCount { get; init; }
    public int SdkQueueCallerCount { get; init; }
    public int AsteriskQueueCallerCount { get; init; }
    public int SdkQueueMemberCount { get; init; }
    public int AsteriskQueueMemberCount { get; init; }
    public int SdkAgentsInCall { get; init; }
    public int AsteriskAgentsInUse { get; init; }

    public int ChannelDrift => Math.Abs(SdkChannelCount - AsteriskChannelCount);
    public int QueueCallerDrift => Math.Abs(SdkQueueCallerCount - AsteriskQueueCallerCount);
    public int QueueMemberDrift => Math.Abs(SdkQueueMemberCount - AsteriskQueueMemberCount);
    public int AgentDrift => Math.Abs(SdkAgentsInCall - AsteriskAgentsInUse);

    public bool WithinTolerance => ChannelDrift <= 2;
}

/// <summary>
/// Aggregate drift statistics computed from a set of <see cref="LiveStateSample"/> records.
/// </summary>
public sealed record LiveStateSummary
{
    public int TotalSamples { get; init; }
    public int SamplesWithinTolerance { get; init; }
    public int MaxDrift { get; init; }
    public double AverageDrift { get; init; }
    public double DriftRate { get; init; } // percentage (0-100) of samples outside tolerance

    // Queue caller drift
    public double QueueCallerDriftRate { get; init; }
    public int MaxQueueCallerDrift { get; init; }

    // Queue member drift
    public double QueueMemberDriftRate { get; init; }
    public int MaxQueueMemberDrift { get; init; }

    // Agent drift
    public double AgentDriftRate { get; init; }
    public int MaxAgentDrift { get; init; }

    public bool Passed { get; init; } // DriftRate < 5%

    public static LiveStateSummary Compute(IReadOnlyList<LiveStateSample> samples)
    {
        if (samples.Count == 0)
            return new LiveStateSummary { Passed = true };

        int withinTolerance = samples.Count(s => s.WithinTolerance);
        int maxDrift = samples.Max(s => s.ChannelDrift);
        double avgDrift = samples.Average(s => (double)s.ChannelDrift);
        double driftRate = (double)(samples.Count - withinTolerance) / samples.Count * 100;

        // Queue caller drift: samples where drift > 2
        int queueCallerOutside = samples.Count(s => s.QueueCallerDrift > 2);
        double queueCallerDriftRate = (double)queueCallerOutside / samples.Count * 100;
        int maxQueueCallerDrift = samples.Max(s => s.QueueCallerDrift);

        // Queue member drift: samples where drift > 2
        int queueMemberOutside = samples.Count(s => s.QueueMemberDrift > 2);
        double queueMemberDriftRate = (double)queueMemberOutside / samples.Count * 100;
        int maxQueueMemberDrift = samples.Max(s => s.QueueMemberDrift);

        // Agent drift: samples where drift > 2
        int agentOutside = samples.Count(s => s.AgentDrift > 2);
        double agentDriftRate = (double)agentOutside / samples.Count * 100;
        int maxAgentDrift = samples.Max(s => s.AgentDrift);

        return new LiveStateSummary
        {
            TotalSamples = samples.Count,
            SamplesWithinTolerance = withinTolerance,
            MaxDrift = maxDrift,
            AverageDrift = avgDrift,
            DriftRate = driftRate,
            QueueCallerDriftRate = queueCallerDriftRate,
            MaxQueueCallerDrift = maxQueueCallerDrift,
            QueueMemberDriftRate = queueMemberDriftRate,
            MaxQueueMemberDrift = maxQueueMemberDrift,
            AgentDriftRate = agentDriftRate,
            MaxAgentDrift = maxAgentDrift,
            Passed = driftRate < 5.0
        };
    }
}
