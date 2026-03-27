namespace PbxAdmin.LoadTests.Metrics;

public sealed record MetricsSummary
{
    public int CallsOriginated { get; init; }
    public int CallsAnswered { get; init; }
    public int CallsFailed { get; init; }
    public int PeakConcurrentCalls { get; init; }
    public double AnswerRate => CallsOriginated > 0 ? (double)CallsAnswered / CallsOriginated * 100 : 0;
    public double CallsPerMinute { get; init; }
    public TimeSpan Elapsed { get; init; }
    public int TotalAgents { get; init; }
    public int PeakAgentsInCall { get; init; }
    public int AgentErrors { get; init; }
}
