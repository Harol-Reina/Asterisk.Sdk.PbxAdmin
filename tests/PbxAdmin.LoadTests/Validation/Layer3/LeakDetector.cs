using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Responses;
using PbxAdmin.LoadTests.AgentEmulation;

namespace PbxAdmin.LoadTests.Validation.Layer3;

/// <summary>
/// Post-test resource leak detection. Queries Asterisk via AMI CLI commands and
/// inspects the AgentPoolService to verify all resources cleaned up after a load test.
/// </summary>
public sealed class LeakDetector
{
    private const string LeakDetectorCallId = "system";
    private const string NoParkedCallsLabel = "0 parked calls";
    private const string NoActiveChannelsLabel = "0 active channels";

    private readonly IAmiConnection _connection;

    public LeakDetector(IAmiConnection connection)
    {
        _connection = connection;
    }

    // -------------------------------------------------------------------------
    // Asterisk resource leak detection (requires live AMI connection)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Queries Asterisk via AMI to verify no active channels or parked calls remain.
    /// </summary>
    public async Task<ValidationResult> DetectLeaksAsync(CancellationToken ct = default)
    {
        var checks = new List<ValidationCheck>();

        // Check 1: No active channels
        var channelCheck = await CheckActiveChannelsAsync(ct);
        checks.Add(channelCheck);

        // Check 2: No parked calls
        var parkCheck = await CheckParkedCallsAsync(ct);
        checks.Add(parkCheck);

        bool allPassed = checks.All(c => c.Passed);

        return new ValidationResult
        {
            CallId = LeakDetectorCallId,
            ValidatorName = nameof(LeakDetector),
            Passed = allPassed,
            Checks = checks
        };
    }

    // -------------------------------------------------------------------------
    // Agent pool leak detection (no AMI needed)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Inspects the AgentPoolService to verify all agents returned to Idle/Offline state.
    /// Does not require an AMI connection.
    /// </summary>
    public static ValidationResult DetectAgentLeaks(AgentPoolService pool)
    {
        var checks = new List<ValidationCheck>();

        // Check 1: All agents must be Idle or Offline
        int ringingCount = pool.RingingAgents;
        bool noRinging = ringingCount == 0;
        checks.Add(new ValidationCheck
        {
            CheckName = "NoRingingAgents",
            Passed = noRinging,
            Expected = "0 ringing agents",
            Actual = $"{ringingCount} ringing agent(s)",
            Message = noRinging ? null : $"{ringingCount} agent(s) stuck in Ringing state after test — possible INVITE leak"
        });

        // Check 2: No agents stuck in InCall/OnHold
        int inCallCount = pool.InCallAgents;
        bool noInCall = inCallCount == 0;
        checks.Add(new ValidationCheck
        {
            CheckName = "NoInCallAgents",
            Passed = noInCall,
            Expected = "0 in-call agents",
            Actual = $"{inCallCount} in-call agent(s)",
            Message = noInCall ? null : $"{inCallCount} agent(s) stuck in InCall/OnHold state after test — possible channel leak"
        });

        // Check 3: All agents are Idle or Offline
        int total = pool.TotalAgents;
        int idleOrOffline = pool.Agents.Count(a => a.State == AgentState.Idle || a.State == AgentState.Offline);
        bool allIdle = (idleOrOffline == total);
        checks.Add(new ValidationCheck
        {
            CheckName = "AllAgentsIdle",
            Passed = allIdle,
            Expected = $"All {total} agents Idle or Offline",
            Actual = $"{idleOrOffline}/{total} agents Idle or Offline",
            Message = allIdle ? null : $"{total - idleOrOffline} agent(s) are in unexpected states after test"
        });

        bool allPassed = checks.All(c => c.Passed);

        return new ValidationResult
        {
            CallId = LeakDetectorCallId,
            ValidatorName = $"{nameof(LeakDetector)}.Agents",
            Passed = allPassed,
            Checks = checks
        };
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private async Task<ValidationCheck> CheckActiveChannelsAsync(CancellationToken ct)
    {
        try
        {
            var response = await _connection.SendActionAsync<CommandResponse>(
                new CommandAction { Command = "core show channels count" }, ct);

            string output = response.Output ?? "";

            // Asterisk outputs: "0 active channels\n0 active calls\n0 calls processed"
            // Parse the first number on the first line
            int activeChannels = ParseFirstInteger(output);
            bool passed = activeChannels == 0;

            return new ValidationCheck
            {
                CheckName = "NoActiveChannels",
                Passed = passed,
                Expected = NoActiveChannelsLabel,
                Actual = $"{activeChannels} active channel(s)",
                Message = passed ? null : $"{activeChannels} channel(s) still active after test — possible channel leak: {output.Trim()}"
            };
        }
        catch (Exception ex)
        {
            return new ValidationCheck
            {
                CheckName = "NoActiveChannels",
                Passed = false,
                Expected = NoActiveChannelsLabel,
                Actual = "AMI query failed",
                Message = $"Failed to query active channels: {ex.Message}"
            };
        }
    }

    private async Task<ValidationCheck> CheckParkedCallsAsync(CancellationToken ct)
    {
        try
        {
            var response = await _connection.SendActionAsync<CommandResponse>(
                new CommandAction { Command = "parkedcalls show" }, ct);

            string output = response.Output ?? "";

            // Asterisk outputs one line per parked call, plus a summary line.
            // "0 parked calls." or lines with parked call details.
            bool noParkedCalls = output.Contains(NoParkedCallsLabel, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(output);

            return new ValidationCheck
            {
                CheckName = "NoParkedCalls",
                Passed = noParkedCalls,
                Expected = NoParkedCallsLabel,
                Actual = noParkedCalls ? NoParkedCallsLabel : $"Parked calls found: {output.Trim()}",
                Message = noParkedCalls ? null : $"Parked calls remain after test: {output.Trim()}"
            };
        }
        catch (Exception ex)
        {
            return new ValidationCheck
            {
                CheckName = "NoParkedCalls",
                Passed = false,
                Expected = NoParkedCallsLabel,
                Actual = "AMI query failed",
                Message = $"Failed to query parked calls: {ex.Message}"
            };
        }
    }

    private static int ParseFirstInteger(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        // Find first sequence of digits in the output
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
