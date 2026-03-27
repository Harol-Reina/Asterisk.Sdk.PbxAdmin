namespace PbxAdmin.LoadTests.Configuration;

public sealed class CallPatternOptions
{
    public const string SectionName = "CallPattern";

    public int CallsPerMinute { get; set; } = 100;
    public int MaxConcurrentCalls { get; set; } = 300;
    public int DefaultCallDurationSecs { get; set; } = 180;
    public int MinCallDurationSecs { get; set; } = 30;
    public int MaxCallDurationSecs { get; set; } = 900;
    public bool BurstMode { get; init; }
    public int RampUpMinutes { get; set; } = 5;
    public int TestDurationMinutes { get; set; } = 60;
    public Dictionary<string, int> ScenarioMix { get; init; } = new()
    {
        ["NormalAnswer"] = 60,
        ["ShortCall"] = 10,
        ["LongCall"] = 5,
        ["Transfer"] = 5,
        ["Hold"] = 5,
        ["IvrNavigation"] = 5,
        ["NoAnswer"] = 3,
        ["Busy"] = 3,
        ["Voicemail"] = 2,
        ["Conference"] = 2
    };
}
