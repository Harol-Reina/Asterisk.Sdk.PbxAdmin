using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace PbxAdmin.LoadTests.Metrics;

/// <summary>
/// Periodically runs <c>docker stats --no-stream</c> to collect container resource metrics.
/// Follows the LiveStateValidator pattern: background loop + ConcurrentBag + Start/Stop lifecycle.
/// </summary>
public sealed class DockerStatsCollector : IAsyncDisposable
{
    private readonly ILogger<DockerStatsCollector> _logger;
    private readonly ConcurrentBag<DockerStatsSample> _samples = [];
    private readonly MetricsCollector _metrics;
    private CancellationTokenSource? _cts;
    private Task? _samplingLoop;
    private bool _dockerAvailable = true;

    public DockerStatsCollector(ILoggerFactory loggerFactory, MetricsCollector metrics)
    {
        _logger = loggerFactory.CreateLogger<DockerStatsCollector>();
        _metrics = metrics;
    }

    /// <summary>
    /// Probes Docker availability and starts the background sampling loop.
    /// If Docker is unavailable, logs a warning and returns immediately.
    /// </summary>
    public async Task StartAsync(string[] containerNames, int intervalSeconds = 5, CancellationToken ct = default)
    {
        if (containerNames.Length == 0)
        {
            _logger.LogWarning("No container names specified — Docker stats collection disabled");
            _dockerAvailable = false;
            return;
        }

        // Probe Docker availability with the first container
        bool probeOk = await ProbeDockerAsync(containerNames[0]);
        if (!probeOk)
        {
            _dockerAvailable = false;
            _logger.LogWarning("Docker is not available — Docker stats collection disabled");
            return;
        }

        _cts?.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _samplingLoop = SamplingLoopAsync(containerNames, intervalSeconds, _cts.Token);

        _logger.LogInformation("Docker stats collection started (interval={Interval}s, containers={Containers})",
            intervalSeconds, string.Join(", ", containerNames));
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

    /// <summary>Returns all collected samples as a read-only list.</summary>
    public IReadOnlyList<DockerStatsSample> GetSamples() =>
        _samples.ToArray();

    /// <summary>
    /// Computes aggregate Docker stats from all collected samples.
    /// Returns null if no samples were collected.
    /// </summary>
    public DockerStatsSummary? GetSummary()
    {
        var samples = GetSamples();
        return samples.Count == 0 ? null : DockerStatsSummary.Compute(samples);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private async Task<bool> ProbeDockerAsync(string containerName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"stats --no-stream --format \"{{{{json .}}}}\" {containerName}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
                return false;

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Win32Exception: docker binary not found
            // InvalidOperationException: process could not be started
            return false;
        }
    }

    private async Task SamplingLoopAsync(string[] containerNames, int intervalSeconds, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await CollectSamplesAsync(containerNames, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception)
            {
                _dockerAvailable = false;
                _logger.LogWarning("Docker command not found — stopping stats collection");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error collecting Docker stats — continuing");
            }
        }
    }

    private async Task CollectSamplesAsync(string[] containerNames, CancellationToken ct)
    {
        if (!_dockerAvailable)
            return;

        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = $"stats --no-stream --format \"{{{{json .}}}}\" {string.Join(' ', containerNames)}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process is null)
            return;

        var timestamp = DateTime.UtcNow;
        int concurrentCalls = _metrics.CurrentActiveCalls;

        while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var sample = DockerStatsSample.TryParse(line, timestamp, concurrentCalls);
            if (sample is not null)
                _samples.Add(sample);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout waiting for process exit — kill it
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
        }
    }
}
