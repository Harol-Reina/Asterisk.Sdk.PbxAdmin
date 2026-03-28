using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Metrics;

namespace PbxAdmin.LoadTests.Auditing;

/// <summary>
/// Captures ERROR, FATAL, and WARNING log lines from Docker containers
/// using <c>docker logs --since</c>.
/// </summary>
public sealed class ContainerLogCollector
{
    private const int ProcessTimeoutMs = 5000;
    private readonly ILogger _logger;
    private DateTime _lastCollected = DateTime.UtcNow;

    public ContainerLogCollector(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Collects new error/warning log lines from all monitored containers
    /// since the last collection.
    /// </summary>
    public async Task<ErrorEntry[]> CollectNewErrorsAsync(CancellationToken ct)
    {
        string since = _lastCollected.ToString("yyyy-MM-ddTHH:mm:ssZ");
        _lastCollected = DateTime.UtcNow;

        var tasks = DockerContainerNames.All.Select(container =>
            CollectContainerLogsAsync(container, since, ct));

        var results = await Task.WhenAll(tasks);
        return results.SelectMany(r => r).ToArray();
    }

    /// <summary>Filters log output for ERROR, FATAL, and WARNING lines. Testable.</summary>
    internal static ErrorEntry[] FilterErrors(string output, string containerName)
    {
        if (string.IsNullOrWhiteSpace(output))
            return [];

        var errors = new List<ErrorEntry>();

        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            if (trimmed.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("FATAL", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("WARNING", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new ErrorEntry
                {
                    Container = containerName,
                    Message = trimmed
                });
            }
        }

        return errors.ToArray();
    }

    private async Task<ErrorEntry[]> CollectContainerLogsAsync(
        string containerName, string since, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"logs --since {since} {containerName}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
                return [];

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ProcessTimeoutMs);

            // Docker logs writes to stderr for container output
            string stderr = await process.StandardError.ReadToEndAsync(timeoutCts.Token);
            string stdout = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            }

            string combined = stdout + "\n" + stderr;
            return FilterErrors(combined, containerName);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "docker logs failed for {Container}", containerName);
            return [];
        }
    }
}
