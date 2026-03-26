namespace PbxAdmin.LoadTests.Configuration;

public sealed class AgentBehaviorOptions
{
    public const string SectionName = "AgentBehavior";

    public int AgentCount { get; init; } = 20;
    public int MinAgents { get; init; } = 20;
    public int MaxAgents { get; init; } = 300;
    public int RingDelaySecs { get; init; } = 2;
    public int TalkTimeSecs { get; init; } = 30;
    public int WrapupTimeSecs { get; init; } = 5;
    public bool AutoAnswer { get; init; } = true;
}
