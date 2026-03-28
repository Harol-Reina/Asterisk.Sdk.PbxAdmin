namespace PbxAdmin.LoadTests.Configuration;

public sealed class AgentBehaviorOptions
{
    public const string SectionName = "AgentBehavior";

    public int AgentCount { get; init; } = 20;
    public int MinAgents { get; init; } = 1;
    public int MaxAgents { get; init; } = 300;
    public int RingDelaySecs { get; init; } = 2;
    public int RingDelayMaxSecs { get; init; } = 5;
    public int TalkTimeSecs { get; set; } = 30;
    public int TalkTimeVariancePercent { get; init; } = 20;
    public int WrapupTimeSecs { get; init; } = 5;
    public int WrapupMaxSecs { get; init; } = 10;
    public bool AutoAnswer { get; init; } = true;
    public int WaveSize { get; init; } = 20;
    public int WaveStabilizationSecs { get; init; } = 30;
}
