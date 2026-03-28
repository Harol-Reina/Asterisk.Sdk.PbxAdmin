using FluentAssertions;
using PbxAdmin.LoadTests.Configuration;

namespace PbxAdmin.Tests.LoadTests;

public sealed class AgentBehaviorOptionsTests
{
    [Fact]
    public void RingDelayMaxSecs_ShouldDefaultTo5()
    {
        var opts = new AgentBehaviorOptions();
        opts.RingDelayMaxSecs.Should().Be(5);
    }

    [Fact]
    public void TalkTimeVariancePercent_ShouldDefaultTo20()
    {
        var opts = new AgentBehaviorOptions();
        opts.TalkTimeVariancePercent.Should().Be(20);
    }

    [Fact]
    public void WrapupMaxSecs_ShouldDefaultTo10()
    {
        var opts = new AgentBehaviorOptions();
        opts.WrapupMaxSecs.Should().Be(10);
    }

    [Fact]
    public void WaveSize_ShouldDefaultTo20()
    {
        var opts = new AgentBehaviorOptions();
        opts.WaveSize.Should().Be(20);
    }

    [Fact]
    public void WaveStabilizationSecs_ShouldDefaultTo30()
    {
        var opts = new AgentBehaviorOptions();
        opts.WaveStabilizationSecs.Should().Be(30);
    }
}
