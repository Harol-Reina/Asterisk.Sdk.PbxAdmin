using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using PbxAdmin.LoadTests.AgentEmulation;
using PbxAdmin.LoadTests.Configuration;
using PbxAdmin.LoadTests.Validation.Layer3;

namespace PbxAdmin.Tests.LoadTests;

public sealed class LeakDetectorTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static AgentPoolService CreatePool(
        string targetServer = "realtime",
        int minAgents = 1,
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
    // DetectAgentLeaks — all idle (pool not started = 0 agents, all counts 0)
    // -------------------------------------------------------------------------

    [Fact]
    public void DetectAgentLeaks_ShouldPass_WhenAllIdle()
    {
        // Pool not started: TotalAgents = 0, RingingAgents = 0, InCallAgents = 0
        var pool = CreatePool();

        var result = LeakDetector.DetectAgentLeaks(pool);

        result.Passed.Should().BeTrue();
        result.ValidatorName.Should().Contain(nameof(LeakDetector));
    }

    [Fact]
    public void DetectAgentLeaks_ShouldReturnCorrectValidatorName()
    {
        var pool = CreatePool();

        var result = LeakDetector.DetectAgentLeaks(pool);

        result.ValidatorName.Should().Contain(nameof(LeakDetector));
    }

    [Fact]
    public void DetectAgentLeaks_ShouldHaveThreeChecks()
    {
        var pool = CreatePool();

        var result = LeakDetector.DetectAgentLeaks(pool);

        result.Checks.Should().HaveCount(3);
    }

    [Fact]
    public void DetectAgentLeaks_ShouldHaveExpectedCheckNames()
    {
        var pool = CreatePool();

        var result = LeakDetector.DetectAgentLeaks(pool);

        result.Checks.Select(c => c.CheckName).Should().Contain([
            "NoRingingAgents",
            "NoInCallAgents",
            "AllAgentsIdle"
        ]);
    }

    [Fact]
    public void DetectAgentLeaks_ShouldUseSystemCallId()
    {
        var pool = CreatePool();

        var result = LeakDetector.DetectAgentLeaks(pool);

        // The leak detector uses a fixed sentinel call ID, not a real call
        result.CallId.Should().Be("system");
    }
}
