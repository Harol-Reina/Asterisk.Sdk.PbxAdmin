using FluentAssertions;
using PbxAdmin.LoadTests.Auditing;

namespace PbxAdmin.Tests.LoadTests;

public sealed class AsteriskCliCollectorTests
{
    // ── ParseChannelCount ──────────────────────────────────────────────────

    [Fact]
    public void ParseChannelCount_ShouldParseAllThreeValues()
    {
        string output = """
            950 active channels
            750 active calls
            1103 calls processed
            """;

        var result = AsteriskCliCollector.ParseChannelCount(output);

        result.ActiveChannels.Should().Be(950);
        result.ActiveCalls.Should().Be(750);
        result.CallsProcessed.Should().Be(1103);
    }

    [Fact]
    public void ParseChannelCount_ShouldReturnZeros_WhenOutputIsEmpty()
    {
        var result = AsteriskCliCollector.ParseChannelCount("");

        result.ActiveChannels.Should().Be(0);
        result.ActiveCalls.Should().Be(0);
        result.CallsProcessed.Should().Be(0);
    }

    // ── ParseOdbcShow ──────────────────────────────────────────────────────

    [Fact]
    public void ParseOdbcShow_ShouldParseActiveAndMax()
    {
        string output = """
            ODBC DSN Settings
            -----------------

              Name:   asterisk
              DSN:    asterisk-connector
                Number of active connections: 27 (out of 30)
                Cache Type: stack (last release, first re-use)
                Cache Usage: 27 cached out of 30
                Logging: Disabled
            """;

        var (active, max) = AsteriskCliCollector.ParseOdbcShow(output);

        active.Should().Be(27);
        max.Should().Be(30);
    }

    [Fact]
    public void ParseOdbcShow_ShouldReturnZeros_WhenOutputIsEmpty()
    {
        var (active, max) = AsteriskCliCollector.ParseOdbcShow("");

        active.Should().Be(0);
        max.Should().Be(0);
    }

    // ── ParseQueueShow ─────────────────────────────────────────────────────

    [Fact]
    public void ParseQueueShow_ShouldParseHeaderAndMemberCounts()
    {
        string output = """
            loadtest has 3 calls (max unlimited) in 'rrmemory' strategy (12s holdtime, 45s talktime), W:0, C:150, A:5, SL:95.0%, SL2:90.0% within 20s
               Members:
                  Agent 1 (PJSIP/2100) (Not in use)
                  Agent 2 (PJSIP/2101) (In use)
                  Agent 3 (PJSIP/2102) (Ringing)
                  Agent 4 (PJSIP/2103) (Unavailable)
                  Agent 5 (PJSIP/2104) (Not in use)
            """;

        var result = AsteriskCliCollector.ParseQueueShow(output);

        result.CallsWaiting.Should().Be(3);
        result.Completed.Should().Be(150);
        result.Abandoned.Should().Be(5);
        result.Holdtime.Should().Be(12);
        result.Talktime.Should().Be(45);
        result.MembersIdle.Should().Be(2);
        result.MembersInUse.Should().Be(1);
        result.MembersRinging.Should().Be(1);
        result.MembersUnavailable.Should().Be(1);
    }

    [Fact]
    public void ParseQueueShow_ShouldReturnDefaults_WhenOutputIsEmpty()
    {
        var result = AsteriskCliCollector.ParseQueueShow("");

        result.CallsWaiting.Should().Be(0);
        result.Completed.Should().Be(0);
    }

    // ── ParseEndpointCount ─────────────────────────────────────────────────

    [Fact]
    public void ParseEndpointCount_ShouldParseObjectsFound()
    {
        string output = """
             Endpoint:  2100/2100       Not in use    0 of inf
             Endpoint:  2101/2101       Not in use    0 of inf

            Objects found: 208
            """;

        AsteriskCliCollector.ParseEndpointCount(output).Should().Be(208);
    }

    [Fact]
    public void ParseEndpointCount_ShouldReturn0_WhenNoMatch()
    {
        AsteriskCliCollector.ParseEndpointCount("").Should().Be(0);
    }

    // ── StripAnsi ─────────────────────────────────────────────────────────

    [Fact]
    public void StripAnsi_ShouldRemoveColorCodes()
    {
        string input = "\u001b[1;31;40mUnavailable\u001b[0m";
        AsteriskCliCollector.StripAnsi(input).Should().Be("Unavailable");
    }

    [Fact]
    public void StripAnsi_ShouldPreservePlainText()
    {
        AsteriskCliCollector.StripAnsi("(Not in use)").Should().Be("(Not in use)");
    }

    [Fact]
    public void ParseQueueShow_ShouldParseMembers_WithAnsiColorCodes()
    {
        // Real Asterisk output includes ANSI color codes around member states
        string output = "loadtest has 5 calls (max unlimited) in 'rrmemory' strategy (12s holdtime, 30s talktime), W:0, C:100, A:3, SL:95.0%, SL2:90.0% within 20s\n"
            + "   Members:\n"
            + "      Load Agent 1 (PJSIP/2100) (ringinuse disabled)\u001b[0m\u001b[1;35;40m (realtime)\u001b[0m\u001b[0m (\u001b[1;32;40mNot in use\u001b[0m) has taken 6 calls\n"
            + "      Load Agent 2 (PJSIP/2101) (ringinuse disabled)\u001b[0m\u001b[1;35;40m (realtime)\u001b[0m\u001b[0m (\u001b[1;33;40mIn use\u001b[0m) has taken 3 calls\n"
            + "      Load Agent 3 (PJSIP/2102) (ringinuse disabled)\u001b[0m\u001b[1;35;40m (realtime)\u001b[0m\u001b[0m (\u001b[1;31;40mUnavailable\u001b[0m) has taken 0 calls\n";

        // StripAnsi is applied in RunDockerExecAsync before parsing,
        // so simulate that here
        string cleaned = AsteriskCliCollector.StripAnsi(output);
        var result = AsteriskCliCollector.ParseQueueShow(cleaned);

        result.CallsWaiting.Should().Be(5);
        result.Completed.Should().Be(100);
        result.Abandoned.Should().Be(3);
        result.MembersIdle.Should().Be(1);
        result.MembersInUse.Should().Be(1);
        result.MembersUnavailable.Should().Be(1);
    }
}
