namespace PbxAdmin.LoadTests.Configuration;

public sealed class ColombianNumberOptions
{
    public const string SectionName = "ColombianNumbers";

    public int MobileWeight { get; init; } = 70;
    public int LandlineWeight { get; init; } = 30;
    public Dictionary<string, MobileOperatorOptions> Operators { get; init; } = new();
    public Dictionary<string, LandlineOptions> Landlines { get; init; } = new();
}

public sealed class MobileOperatorOptions
{
    public List<string> Prefixes { get; init; } = new();
    public int Weight { get; init; } = 0;
}

public sealed class LandlineOptions
{
    public string Prefix { get; init; } = string.Empty;
    public int Weight { get; init; } = 0;
}
