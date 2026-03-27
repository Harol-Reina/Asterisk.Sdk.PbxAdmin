using System.Collections.Concurrent;
using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Responses;
using Asterisk.Sdk.Live.Server;

namespace PbxAdmin.LoadTests.Sdk;

/// <summary>
/// Periodically samples the SDK's in-memory channel count and compares it against
/// Asterisk's AMI ground truth ("core show channels count"). Collects samples for
/// post-test drift analysis via <see cref="GetSummary"/>.
/// </summary>
public sealed class LiveStateValidator : IAsyncDisposable
{
    private readonly ConcurrentBag<LiveStateSample> _samples = [];
    private CancellationTokenSource? _cts;
    private Task? _samplingLoop;

    /// <summary>Returns all collected samples as a read-only list.</summary>
    public IReadOnlyList<LiveStateSample> GetSamples() =>
        _samples.ToArray();

    /// <summary>Computes aggregate drift statistics from all collected samples.</summary>
    public LiveStateSummary GetSummary() =>
        LiveStateSummary.Compute(GetSamples());

    /// <summary>
    /// Starts a background loop that collects one <see cref="LiveStateSample"/> per interval.
    /// </summary>
    public Task StartAsync(AsteriskServer server, IAmiConnection connection, int intervalSeconds = 5, CancellationToken ct = default)
    {
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
                var sample = await CollectSampleAsync(server, connection, ct);
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

    private static async Task<LiveStateSample> CollectSampleAsync(AsteriskServer server, IAmiConnection connection, CancellationToken ct)
    {
        int asteriskChannels = await QueryAsteriskChannelCountAsync(connection, ct);
        int sdkChannels = server.Channels.ChannelCount;

        return new LiveStateSample
        {
            Timestamp = DateTime.UtcNow,
            SdkChannelCount = sdkChannels,
            AsteriskChannelCount = asteriskChannels
        };
    }

    private static async Task<int> QueryAsteriskChannelCountAsync(IAmiConnection connection, CancellationToken ct)
    {
        var response = await connection.SendActionAsync<CommandResponse>(
            new CommandAction { Command = "core show channels count" }, ct);
        return ParseFirstInteger(response.Output ?? "");
    }

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

/// <summary>
/// A single point-in-time comparison between SDK channel count and Asterisk's AMI ground truth.
/// </summary>
public sealed record LiveStateSample
{
    public required DateTime Timestamp { get; init; }
    public int SdkChannelCount { get; init; }
    public int AsteriskChannelCount { get; init; }
    public int SdkQueueCallerCount { get; init; }
    public int AsteriskQueueCallerCount { get; init; }
    public int ChannelDrift => Math.Abs(SdkChannelCount - AsteriskChannelCount);
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
    public bool Passed { get; init; }      // DriftRate < 5%

    public static LiveStateSummary Compute(IReadOnlyList<LiveStateSample> samples)
    {
        if (samples.Count == 0)
            return new LiveStateSummary { Passed = true };

        int withinTolerance = samples.Count(s => s.WithinTolerance);
        int maxDrift = samples.Max(s => s.ChannelDrift);
        double avgDrift = samples.Average(s => (double)s.ChannelDrift);
        double driftRate = (double)(samples.Count - withinTolerance) / samples.Count * 100;

        return new LiveStateSummary
        {
            TotalSamples = samples.Count,
            SamplesWithinTolerance = withinTolerance,
            MaxDrift = maxDrift,
            AverageDrift = avgDrift,
            DriftRate = driftRate,
            Passed = driftRate < 5.0
        };
    }
}
