using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Responses;
using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Validation;
using PbxAdmin.LoadTests.Validation.Layer3;

namespace PbxAdmin.LoadTests.Scenarios.Sdk;

/// <summary>
/// Abstract base class for all SDK test scenarios. Implements <see cref="ITestScenario"/>
/// and provides shared helpers used across smoke, functional, scale, and endurance tiers.
/// </summary>
public abstract class SdkScenarioBase : ITestScenario
{
    // ── Abstract surface ─────────────────────────────────────────────────────

    public abstract string Name { get; }
    public abstract string Description { get; }

    /// <summary>
    /// Scenario tier: "smoke", "functional", "scale", or "endurance".
    /// </summary>
    public abstract string Level { get; }

    public abstract Task ExecuteAsync(TestContext context, CancellationToken ct);
    public abstract Task<ValidationReport> ValidateAsync(TestContext context, CancellationToken ct);

    // ── GenerateCallsAsync ───────────────────────────────────────────────────

    /// <summary>
    /// Generates <paramref name="count"/> calls to <paramref name="destination"/>,
    /// waiting <paramref name="delayBetween"/> between each origination.
    /// Registers each call with <c>EventCapture</c> and records it in <c>Metrics</c>.
    /// </summary>
    protected static async Task GenerateCallsAsync(
        TestContext context,
        string destination,
        int count,
        ILogger logger,
        CancellationToken ct,
        TimeSpan? delayBetween = null)
    {
        var delay = delayBetween ?? TimeSpan.FromSeconds(2);

        try
        {
            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var result = await context.CallGenerator.GenerateCallAsync(destination, cancellationToken: ct);
                context.EventCapture.RegisterCall(result.CallId, result.Caller.Number, result.Destination, result.Timestamp);
                context.Metrics.RecordCallOriginated();

                if (result.Accepted)
                    logger.LogDebug("Call {N}/{Total} to {Dest} accepted: {CallId}", i + 1, count, destination, result.CallId);
                else
                    logger.LogWarning("Call {N}/{Total} to {Dest} rejected: {Error}", i + 1, count, destination, result.ErrorMessage);

                if (i < count - 1)
                    await Task.Delay(delay, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Call generation to {Dest} failed", destination);
        }
    }

    // ── SetAgentsPausedAsync ─────────────────────────────────────────────────

    /// <summary>
    /// Pauses or unpauses all registered agents in the loadtest queue via AMI
    /// <c>QueuePauseAction</c>. Uses the SDK connection to the target PBX.
    /// </summary>
    protected static async Task SetAgentsPausedAsync(
        TestContext context,
        bool paused,
        ILogger logger,
        CancellationToken ct)
    {
        var connection = context.SdkRuntime!.Connection;
        int agentCount = context.AgentPool.TotalAgents;
        string targetServer = context.Options.TargetServer;
        int baseExt = targetServer.Equals("file", StringComparison.OrdinalIgnoreCase) ? 4100 : 2100;

        logger.LogDebug("{Action} {Count} agents in loadtest queue",
            paused ? "Pausing" : "Unpausing", agentCount);

        for (int i = 0; i < agentCount; i++)
        {
            string iface = $"PJSIP/{baseExt + i}";
            try
            {
                await connection.SendActionAsync(new QueuePauseAction
                {
                    Queue = "loadtest",
                    Interface = iface,
                    Paused = paused
                }, ct);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to {Action} {Interface}", paused ? "pause" : "unpause", iface);
            }
        }
    }

    // ── WaitForDrainAsync ────────────────────────────────────────────────────

    /// <summary>
    /// Polls AMI "core show channels count" until active channels reach 0, or times
    /// out after 60 seconds. Prevents interference from channels left by previous runs.
    /// </summary>
    protected static async Task WaitForDrainAsync(
        IAmiConnection connection,
        ILogger logger,
        CancellationToken ct,
        int timeoutMs = 60_000,
        int pollMs = 3_000)
    {
        int elapsed = 0;

        while (elapsed < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var response = await connection.SendActionAsync<CommandResponse>(
                    new CommandAction { Command = "core show channels count" }, ct);
                int channels = ParseFirstInteger(response.Output ?? "");

                if (channels == 0)
                {
                    logger.LogDebug("Channels drained after {Elapsed}ms", elapsed);
                    return;
                }

                logger.LogDebug("Waiting for {Channels} active channels to drain...", channels);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Channel drain query failed, retrying...");
            }

            await Task.Delay(pollMs, ct);
            elapsed += pollMs;
        }

        logger.LogWarning("Channel drain timed out after {Timeout}ms — proceeding anyway", timeoutMs);
    }

    // ── DetectLeaksAsync ─────────────────────────────────────────────────────

    /// <summary>
    /// Runs <see cref="LeakDetector.DetectLeaksAsync"/> (Asterisk resource leaks) and
    /// <see cref="LeakDetector.DetectAgentLeaks"/> (agent pool state leaks) and returns
    /// both results combined in a single list.
    /// </summary>
    protected static async Task<List<ValidationResult>> DetectLeaksAsync(
        TestContext context,
        CancellationToken ct)
    {
        var detector = new LeakDetector(context.SdkRuntime!.Connection);
        var asteriskLeaks = await detector.DetectLeaksAsync(ct);
        var agentLeaks = LeakDetector.DetectAgentLeaks(context.AgentPool);

        return [asteriskLeaks, agentLeaks];
    }

    // ── BuildReport ──────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="ValidationReport"/> from a list of <see cref="ValidationResult"/>
    /// objects and the test timestamps stored in <paramref name="context"/>.
    /// </summary>
    protected static ValidationReport BuildReport(TestContext context, List<ValidationResult> results) => new()
    {
        TestStart = context.TestStartTime,
        TestEnd = context.TestEndTime,
        Duration = context.TestEndTime - context.TestStartTime,
        TotalCalls = results.Count(r => r.CallId != "system" && r.CallId != "aggregate" && r.CallId != "reconnect-aggregate"),
        TotalChecks = results.SelectMany(r => r.Checks).Count(),
        PassedChecks = results.SelectMany(r => r.Checks).Count(c => c.Passed),
        FailedChecks = results.SelectMany(r => r.Checks).Count(c => !c.Passed),
        Results = results
    };

    // ── ForceAmiDisconnectAsync ──────────────────────────────────────────────

    /// <summary>
    /// Sends "manager reload" via AMI to force-disconnect the current AMI session.
    /// The connection may drop before a response is received — this is expected.
    /// </summary>
    protected static async Task ForceAmiDisconnectAsync(
        IAmiConnection connection,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            await connection.SendActionAsync(
                new CommandAction { Command = "manager reload" }, ct);
            logger.LogInformation("'manager reload' sent successfully");
        }
        catch (Exception ex)
        {
            // Connection may drop immediately — this is expected behavior.
            logger.LogWarning(ex, "'manager reload' threw (connection may have dropped immediately)");
        }
    }

    // ── WaitForReconnectAsync ────────────────────────────────────────────────

    /// <summary>
    /// Polls AMI with "core show version" until the connection responds or
    /// <paramref name="timeoutMs"/> elapses. Returns whether reconnection succeeded
    /// and how many milliseconds it took.
    /// </summary>
    protected static async Task<(bool Reconnected, int ElapsedMs)> WaitForReconnectAsync(
        IAmiConnection connection,
        ILogger logger,
        CancellationToken ct,
        int timeoutMs = 10_000,
        int pollMs = 500)
    {
        int elapsed = 0;

        logger.LogInformation("Polling for reconnect (timeout={Timeout}ms)", timeoutMs);

        while (elapsed < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await connection.SendActionAsync<CommandResponse>(
                    new CommandAction { Command = "core show version" }, ct);
                logger.LogInformation("Reconnected after {Elapsed}ms", elapsed);
                return (true, elapsed);
            }
            catch
            {
                await Task.Delay(pollMs, ct);
                elapsed += pollMs;
            }
        }

        logger.LogWarning("Did not reconnect within {Timeout}ms", timeoutMs);
        return (false, elapsed);
    }

    // ── ParseFirstInteger ────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the first contiguous run of decimal digits from <paramref name="text"/>
    /// and returns it as an integer, or 0 if no digits are found.
    /// </summary>
    internal static int ParseFirstInteger(string text)
    {
        int start = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsDigit(text[i])) { start = i; break; }
        }
        if (start < 0) return 0;
        int end = start;
        while (end < text.Length && char.IsDigit(text[end])) end++;
        return int.TryParse(text[start..end], out int value) ? value : 0;
    }
}
