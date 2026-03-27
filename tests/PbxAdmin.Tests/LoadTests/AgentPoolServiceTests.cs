using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using PbxAdmin.LoadTests.AgentEmulation;
using PbxAdmin.LoadTests.Configuration;

namespace PbxAdmin.Tests.LoadTests;

/// <summary>
/// Unit tests for AgentPoolService and AgentPoolStats.
///
/// SIPTransport lifecycle (RegisterAsync, real network I/O) is covered by
/// integration tests against the Docker stack.  These tests focus on:
///   - Validation guards (min/max agents)
///   - Statistics record correctness
///   - Extension ID and password naming conventions (via the internal static helper)
/// </summary>
public sealed class AgentPoolServiceTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static AgentPoolService CreateService(
        string targetServer = "realtime",
        int minAgents = 20,
        int maxAgents = 300)
    {
        var loadOptions = Options.Create(new LoadTestOptions
        {
            TargetServer = targetServer,
            TargetPbxAmi = new AmiConnectionOptions { Host = "localhost", Port = 5038 }
        });

        var behaviorOptions = Options.Create(new AgentBehaviorOptions
        {
            AgentCount = 20,
            MinAgents = minAgents,
            MaxAgents = maxAgents,
            RingDelaySecs = 0,
            TalkTimeSecs = 30,
            WrapupTimeSecs = 5,
            AutoAnswer = true
        });

        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        return new AgentPoolService(loadOptions, behaviorOptions, loggerFactory);
    }

    // -------------------------------------------------------------------------
    // AgentPoolStats — construction and defaults
    // -------------------------------------------------------------------------

    [Fact]
    public void AgentPoolStats_ShouldHaveCorrectDefaults()
    {
        var stats = new AgentPoolStats();

        stats.Total.Should().Be(0);
        stats.Idle.Should().Be(0);
        stats.Ringing.Should().Be(0);
        stats.InCall.Should().Be(0);
        stats.OnHold.Should().Be(0);
        stats.Wrapup.Should().Be(0);
        stats.Error.Should().Be(0);
        stats.TotalCallsHandled.Should().Be(0);
    }

    [Fact]
    public void GetStats_ShouldReturnCorrectCounts()
    {
        var stats = new AgentPoolStats
        {
            Total = 5,
            Idle = 2,
            Ringing = 1,
            InCall = 1,
            OnHold = 0,
            Wrapup = 1,
            Error = 0,
            TotalCallsHandled = 10,
            Timestamp = new DateTime(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc)
        };

        stats.Total.Should().Be(5);
        stats.Idle.Should().Be(2);
        stats.Ringing.Should().Be(1);
        stats.InCall.Should().Be(1);
        stats.OnHold.Should().Be(0);
        stats.Wrapup.Should().Be(1);
        stats.Error.Should().Be(0);
        stats.TotalCallsHandled.Should().Be(10);
    }

    // -------------------------------------------------------------------------
    // StartAsync — validation guards
    // -------------------------------------------------------------------------

    [Fact]
    public async Task StartAsync_ShouldRejectBelowMinAgents()
    {
        var service = CreateService(minAgents: 20, maxAgents: 300);

        Func<Task> act = () => service.StartAsync(agentCount: 5, ct: CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithMessage("*agentCount*");
    }

    [Fact]
    public async Task StartAsync_ShouldRejectAboveMaxAgents()
    {
        var service = CreateService(minAgents: 20, maxAgents: 300);

        Func<Task> act = () => service.StartAsync(agentCount: 301, ct: CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithMessage("*agentCount*");
    }

    [Fact]
    public async Task StartAsync_ShouldRejectExactlyBelowMin()
    {
        var service = CreateService(minAgents: 20, maxAgents: 300);

        Func<Task> act = () => service.StartAsync(agentCount: 19, ct: CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task StartAsync_ShouldRejectExactlyAboveMax()
    {
        var service = CreateService(minAgents: 20, maxAgents: 300);

        Func<Task> act = () => service.StartAsync(agentCount: 300 + 1, ct: CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    // -------------------------------------------------------------------------
    // Extension ID naming conventions
    // -------------------------------------------------------------------------

    [Fact]
    public void ExtensionId_ShouldFollowNamingConvention_ForRealtimeServer()
    {
        var (ext0, _) = AgentPoolService.GetAgentCredentials(0, "realtime");
        var (ext1, _) = AgentPoolService.GetAgentCredentials(1, "realtime");
        var (extLast, _) = AgentPoolService.GetAgentCredentials(299, "realtime");

        ext0.Should().Be("2100");
        ext1.Should().Be("2101");
        extLast.Should().Be("2399");
    }

    [Fact]
    public void ExtensionId_ShouldFollowNamingConvention_ForFileServer()
    {
        var (ext0, _) = AgentPoolService.GetAgentCredentials(0, "file");
        var (ext1, _) = AgentPoolService.GetAgentCredentials(1, "file");
        var (extLast, _) = AgentPoolService.GetAgentCredentials(299, "file");

        ext0.Should().Be("4100");
        ext1.Should().Be("4101");
        extLast.Should().Be("4399");
    }

    [Fact]
    public void ExtensionId_ShouldDefaultToRealtimeBase_WhenTargetIsUnrecognized()
    {
        var (ext, _) = AgentPoolService.GetAgentCredentials(0, "unknown");

        ext.Should().Be("2100");
    }

    // -------------------------------------------------------------------------
    // Password conventions
    // -------------------------------------------------------------------------

    [Fact]
    public void Password_ShouldFollowConvention_ForRealtimeServer()
    {
        var (ext, password) = AgentPoolService.GetAgentCredentials(0, "realtime");

        password.Should().Be($"loadtest{ext}");
        password.Should().Be("loadtest2100");
    }

    [Fact]
    public void Password_ShouldFollowConvention_ForFileServer()
    {
        var (ext, password) = AgentPoolService.GetAgentCredentials(0, "file");

        password.Should().Be($"loadtest{ext}");
        password.Should().Be("loadtest4100");
    }

    [Theory]
    [InlineData(0, "realtime", "loadtest2100")]
    [InlineData(1, "realtime", "loadtest2101")]
    [InlineData(99, "realtime", "loadtest2199")]
    [InlineData(0, "file", "loadtest4100")]
    [InlineData(50, "file", "loadtest4150")]
    public void Password_ShouldFollowConvention(int index, string targetServer, string expectedPassword)
    {
        var (_, password) = AgentPoolService.GetAgentCredentials(index, targetServer);

        password.Should().Be(expectedPassword);
    }

    // -------------------------------------------------------------------------
    // Adaptive batch size
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(20, 10)]   // ≤50 agents → batch of 10
    [InlineData(50, 10)]   // ≤50 agents → batch of 10
    [InlineData(51, 20)]   // 51-150 agents → batch of 20
    [InlineData(100, 20)]  // 51-150 agents → batch of 20
    [InlineData(150, 20)]  // 51-150 agents → batch of 20
    [InlineData(151, 30)]  // >150 agents → batch of 30
    [InlineData(300, 30)]  // >150 agents → batch of 30
    public void CalculateBatchSize_ShouldScaleWithAgentCount(int agentCount, int expectedBatchSize)
    {
        int batchSize = AgentPoolService.CalculateBatchSize(agentCount);

        batchSize.Should().Be(expectedBatchSize);
    }

    // -------------------------------------------------------------------------
    // Initial state of pool (before StartAsync)
    // -------------------------------------------------------------------------

    [Fact]
    public void TotalAgents_ShouldBeZero_BeforeStart()
    {
        var service = CreateService();

        service.TotalAgents.Should().Be(0);
    }

    [Fact]
    public void IdleAgents_ShouldBeZero_BeforeStart()
    {
        var service = CreateService();

        service.IdleAgents.Should().Be(0);
    }

    [Fact]
    public void Agents_ShouldBeEmpty_BeforeStart()
    {
        var service = CreateService();

        service.Agents.Should().BeEmpty();
    }

    [Fact]
    public void GetStats_ShouldReturnZeroCounts_BeforeStart()
    {
        var service = CreateService();

        var stats = service.GetStats();

        stats.Total.Should().Be(0);
        stats.Idle.Should().Be(0);
        stats.Error.Should().Be(0);
        stats.TotalCallsHandled.Should().Be(0);
    }

    // -------------------------------------------------------------------------
    // Readiness parameters
    // -------------------------------------------------------------------------

    [Fact]
    public void MinReadyPercent_ShouldDefaultTo80()
    {
        AgentPoolService.MinReadyPercent.Should().Be(80);
    }

    [Fact]
    public void MaxRetryWaves_ShouldDefaultTo2()
    {
        AgentPoolService.MaxRetryWaves.Should().Be(2);
    }

    [Fact]
    public void ReadinessTimeoutSecs_ShouldDefaultTo60()
    {
        AgentPoolService.ReadinessTimeoutSecs.Should().Be(60);
    }

    [Fact]
    public void ReadinessPollIntervalSecs_ShouldDefaultTo2()
    {
        AgentPoolService.ReadinessPollIntervalSecs.Should().Be(2);
    }
}
