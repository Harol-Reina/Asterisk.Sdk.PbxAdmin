namespace PbxAdmin.LoadTests.Scenarios;

internal static class LevelRunner
{
    public static readonly IReadOnlyDictionary<string, string[]> Levels =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["smoke"] = ["sdk-smoke"],
            ["functional"] = ["sdk-smoke", "sdk-state-sync", "sdk-sessions", "sdk-reconnect"],
            ["scale"] = [
                "sdk-smoke", "sdk-state-sync", "sdk-sessions", "sdk-reconnect",
                "sdk-scale-channels", "sdk-scale-queues", "sdk-scale-agents", "sdk-scale-sessions"
            ],
            ["all"] = [
                "sdk-smoke", "sdk-state-sync", "sdk-sessions", "sdk-reconnect",
                "sdk-scale-channels", "sdk-scale-queues", "sdk-scale-agents", "sdk-scale-sessions",
                "sdk-endurance"
            ],
        };

    public static string[] GetScenarios(string level) =>
        Levels.TryGetValue(level, out var scenarios) ? scenarios : [];
}
