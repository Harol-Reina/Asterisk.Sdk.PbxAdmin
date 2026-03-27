using PbxAdmin.LoadTests.Scenarios.Chaos;
using PbxAdmin.LoadTests.Scenarios.Functional;
using PbxAdmin.LoadTests.Scenarios.Load;
using PbxAdmin.LoadTests.Scenarios.Soak;

namespace PbxAdmin.LoadTests.Scenarios;

/// <summary>
/// Registry of all available test scenarios keyed by their CLI name.
/// Look up by name via <see cref="Get"/> or enumerate all names via <see cref="Names"/>.
/// </summary>
public static class ScenarioRegistry
{
    public static IReadOnlyDictionary<string, ITestScenario> All { get; } =
        new Dictionary<string, ITestScenario>(StringComparer.OrdinalIgnoreCase)
        {
            // Functional scenarios
            ["inbound-answer"] = new InboundAnswerScenario(),
            ["inbound-busy"] = new InboundBusyScenario(),
            ["queue-distribution"] = new QueueDistributionScenario(),
            ["ivr-navigation"] = new IvrNavigationScenario(),
            ["transfer"] = new TransferScenario(),
            ["hold"] = new HoldScenario(),
            ["conference"] = new ConferenceScenario(),
            ["parking"] = new ParkingScenario(),
            ["voicemail"] = new VoicemailScenario(),
            ["dtmf"] = new DtmfScenario(),
            ["outbound-call"] = new OutboundCallScenario(),
            ["time-condition"] = new TimeConditionScenario(),
            // Load scenarios
            ["ramp-up"] = new RampUpScenario(),
            ["sustained-load"] = new SustainedLoadScenario(),
            ["peak-hour"] = new PeakHourScenario(),
            // Soak scenarios
            ["eight-hour-soak"] = new EightHourSoakScenario(),
            // Chaos scenarios
            ["agent-crash"] = new AgentCrashScenario(),
            ["trunk-failure"] = new TrunkFailureScenario(),
            ["rapid-reregister"] = new RapidReregisterScenario(),
            // SDK validation scenarios
            ["sdk-session-accuracy"] = new SdkSessionAccuracyScenario(),
            ["sdk-live-drift"] = new SdkLiveDriftScenario(),
            // Aliases
            ["smoke"] = new InboundAnswerScenario(),
            ["load"] = new SustainedLoadScenario(),
            ["soak"] = new EightHourSoakScenario(),
            ["chaos"] = new AgentCrashScenario(),
        };

    public static ITestScenario? Get(string name) => All.GetValueOrDefault(name);

    public static IEnumerable<string> Names => All.Keys;
}
