using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.AgentEmulation;
using PbxAdmin.LoadTests.CallGeneration;
using PbxAdmin.LoadTests.Configuration;
using PbxAdmin.LoadTests.Metrics;
using PbxAdmin.LoadTests.Validation.Layer1;
using PbxAdmin.LoadTests.Validation.Layer2;

namespace PbxAdmin.LoadTests.Scenarios;

/// <summary>
/// Shared state bag passed to every test scenario, providing access to all services,
/// readers, and configuration objects for the duration of a test run.
/// </summary>
public sealed class TestContext
{
    public required CallGeneratorService CallGenerator { get; init; }
    public required AgentPoolService AgentPool { get; init; }
    public required CallPatternScheduler Scheduler { get; init; }
    public required SdkEventCapture EventCapture { get; init; }
    public required CdrReader CdrReader { get; init; }
    public required CelReader CelReader { get; init; }
    public required QueueLogReader QueueLogReader { get; init; }
    public required MetricsCollector Metrics { get; init; }
    public required LoadTestOptions Options { get; init; }
    public required CallPatternOptions CallPattern { get; init; }
    public required AgentBehaviorOptions AgentBehavior { get; init; }
    public required ILoggerFactory LoggerFactory { get; init; }

    public DateTime TestStartTime { get; set; }
    public DateTime TestEndTime { get; set; }
}
