using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Metrics;

namespace PbxAdmin.LoadTests.Auditing;

/// <summary>
/// Collects Asterisk metrics by running <c>docker exec</c> CLI commands and parsing output.
/// All parse methods are <c>internal static</c> for unit testing.
/// </summary>
public sealed class AsteriskCliCollector
{
    private const int ProcessTimeoutMs = 5000;
    private readonly ILogger _logger;

    public AsteriskCliCollector(ILogger logger)
    {
        _logger = logger;
    }

    // ── Public collection methods ──────────────────────────────────────────

    public async Task<AsteriskSnapshot> CollectRealtimeAsync(string queueName, CancellationToken ct)
    {
        var channelsTask = RunDockerExecAsync(DockerContainerNames.PbxRealtime, "core show channels count", ct);
        var odbcTask = RunDockerExecAsync(DockerContainerNames.PbxRealtime, "odbc show", ct);
        var queueTask = RunDockerExecAsync(DockerContainerNames.PbxRealtime, $"queue show {queueName}", ct);
        var endpointsTask = RunDockerExecAsync(DockerContainerNames.PbxRealtime, "pjsip show endpoints", ct);
        var rtpTask = RunDockerExecAsync(DockerContainerNames.PbxRealtime, "pjsip show channelstats", ct);

        await Task.WhenAll(channelsTask, odbcTask, queueTask, endpointsTask, rtpTask);

        var channels = ParseChannelCount(await channelsTask);
        var (odbcActive, odbcMax) = ParseOdbcShow(await odbcTask);
        var queue = ParseQueueShow(await queueTask);
        int endpoints = ParseEndpointCount(await endpointsTask);
        string? rtpOutput = (await rtpTask)?.Trim();
        if (string.IsNullOrEmpty(rtpOutput)) rtpOutput = null;

        return new AsteriskSnapshot
        {
            ActiveChannels = channels.ActiveChannels,
            ActiveCalls = channels.ActiveCalls,
            CallsProcessed = channels.CallsProcessed,
            OdbcActiveConnections = odbcActive,
            OdbcMaxConnections = odbcMax,
            EndpointCount = endpoints,
            Queue = queue,
            RtpRawOutput = rtpOutput
        };
    }

    public async Task<AsteriskBasicSnapshot> CollectBasicAsync(string containerName, CancellationToken ct)
    {
        string output = await RunDockerExecAsync(containerName, "core show channels count", ct);
        return ParseChannelCount(output);
    }

    // ── Internal static parsers (testable) ─────────────────────────────────

    internal static AsteriskBasicSnapshot ParseChannelCount(string output)
    {
        int channels = 0, calls = 0, processed = 0;

        if (string.IsNullOrWhiteSpace(output))
            return new AsteriskBasicSnapshot();

        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Contains("active channels", StringComparison.Ordinal))
                int.TryParse(ExtractLeadingInt(trimmed), out channels);
            else if (trimmed.Contains("active calls", StringComparison.Ordinal))
                int.TryParse(ExtractLeadingInt(trimmed), out calls);
            else if (trimmed.Contains("calls processed", StringComparison.Ordinal))
                int.TryParse(ExtractLeadingInt(trimmed), out processed);
        }

        return new AsteriskBasicSnapshot
        {
            ActiveChannels = channels,
            ActiveCalls = calls,
            CallsProcessed = processed
        };
    }

    internal static (int Active, int Max) ParseOdbcShow(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return (0, 0);

        var match = Regex.Match(output, @"active connections:\s*(\d+)\s*\(out of\s*(\d+)\)");
        if (match.Success)
        {
            int active = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            int max = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            return (active, max);
        }

        return (0, 0);
    }

    internal static QueueSnapshot ParseQueueShow(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return new QueueSnapshot();

        int callsWaiting = 0, completed = 0, abandoned = 0, holdtime = 0, talktime = 0;
        int idle = 0, inUse = 0, ringing = 0, unavailable = 0;

        var headerMatch = Regex.Match(output, @"has\s+(\d+)\s+calls");
        if (headerMatch.Success)
            callsWaiting = int.Parse(headerMatch.Groups[1].Value, CultureInfo.InvariantCulture);

        var holdMatch = Regex.Match(output, @"(\d+)s holdtime");
        if (holdMatch.Success)
            holdtime = int.Parse(holdMatch.Groups[1].Value, CultureInfo.InvariantCulture);

        var talkMatch = Regex.Match(output, @"(\d+)s talktime");
        if (talkMatch.Success)
            talktime = int.Parse(talkMatch.Groups[1].Value, CultureInfo.InvariantCulture);

        var cMatch = Regex.Match(output, @"C:(\d+)");
        if (cMatch.Success)
            completed = int.Parse(cMatch.Groups[1].Value, CultureInfo.InvariantCulture);

        var aMatch = Regex.Match(output, @"A:(\d+)");
        if (aMatch.Success)
            abandoned = int.Parse(aMatch.Groups[1].Value, CultureInfo.InvariantCulture);

        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Contains("(Not in use)", StringComparison.Ordinal)) idle++;
            else if (trimmed.Contains("(In use)", StringComparison.Ordinal)) inUse++;
            else if (trimmed.Contains("(Ringing)", StringComparison.Ordinal)) ringing++;
            else if (trimmed.Contains("(Unavailable)", StringComparison.Ordinal)) unavailable++;
        }

        return new QueueSnapshot
        {
            CallsWaiting = callsWaiting,
            Completed = completed,
            Abandoned = abandoned,
            Holdtime = holdtime,
            Talktime = talktime,
            MembersIdle = idle,
            MembersInUse = inUse,
            MembersRinging = ringing,
            MembersUnavailable = unavailable
        };
    }

    internal static int ParseEndpointCount(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return 0;

        var match = Regex.Match(output, @"Objects found:\s*(\d+)");
        return match.Success
            ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
            : 0;
    }

    // ── Private helpers ────────────────────────────────────────────────────

    private static string ExtractLeadingInt(string line)
    {
        var match = Regex.Match(line, @"^(\d+)");
        return match.Success ? match.Groups[1].Value : "0";
    }

    private async Task<string> RunDockerExecAsync(string container, string asteriskCmd, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"exec {container} asterisk -rx \"{asteriskCmd}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
                return "";

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ProcessTimeoutMs);

            string output = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                _logger.LogWarning("docker exec timed out: {Container} {Cmd}", container, asteriskCmd);
            }

            return output;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "docker exec failed: {Container} {Cmd}", container, asteriskCmd);
            return "";
        }
    }
}
