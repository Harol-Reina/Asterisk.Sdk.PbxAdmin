using PbxAdmin.LoadTests.Configuration;

namespace PbxAdmin.LoadTests.CallGeneration;

public sealed class ColombianNumberGenerator
{
    private static readonly string[] FirstNames =
    [
        "Juan", "Carlos", "Maria", "Ana", "Pedro",
        "Luis", "Sofia", "Diego", "Valentina", "Andres",
        "Camila", "Santiago", "Isabella", "Miguel", "Daniela",
        "Sebastian", "Laura", "Nicolas", "Gabriela", "Felipe"
    ];

    private static readonly string[] LastNames =
    [
        "Garcia", "Rodriguez", "Martinez", "Lopez", "Gonzalez",
        "Hernandez", "Perez", "Sanchez", "Ramirez", "Torres",
        "Flores", "Rivera", "Gomez", "Diaz", "Morales"
    ];

    private readonly int _mobileWeight;
    private readonly int _landlineWeight;
    private readonly WeightedEntry<OperatorConfig>[] _operators;
    private readonly WeightedEntry<LandlineConfig>[] _landlines;

    public ColombianNumberGenerator(ColombianNumberOptions? options = null)
    {
        _mobileWeight = options?.MobileWeight ?? 70;
        _landlineWeight = options?.LandlineWeight ?? 30;

        _operators = BuildOperators(options);
        _landlines = BuildLandlines(options);
    }

    public CallerProfile Generate()
    {
        int total = _mobileWeight + _landlineWeight;
        int roll = Random.Shared.Next(total);
        return roll < _mobileWeight ? GenerateMobile() : GenerateLandline();
    }

    public CallerProfile GenerateMobile()
    {
        var op = PickWeighted(_operators);
        string prefix = op.Prefixes[Random.Shared.Next(op.Prefixes.Length)];
        string suffix = Random.Shared.Next(0, 10_000_000).ToString("D7");
        return new CallerProfile
        {
            Number = $"57{prefix}{suffix}",
            DisplayName = GenerateDisplayName(),
            Operator = op.Name,
            Type = CallerType.Mobile
        };
    }

    public CallerProfile GenerateLandline()
    {
        var line = PickWeighted(_landlines);
        string suffix = Random.Shared.Next(0, 10_000_000).ToString("D7");
        return new CallerProfile
        {
            Number = $"57{line.Prefix}{suffix}",
            DisplayName = GenerateDisplayName(),
            Operator = line.City,
            Type = CallerType.Landline
        };
    }

    public IReadOnlyList<CallerProfile> GenerateBatch(int count)
    {
        var list = new List<CallerProfile>(count);
        for (int i = 0; i < count; i++)
            list.Add(Generate());
        return list;
    }

    // --- private helpers ---

    private static string GenerateDisplayName()
    {
        string first = FirstNames[Random.Shared.Next(FirstNames.Length)];
        string last = LastNames[Random.Shared.Next(LastNames.Length)];
        return $"{first} {last}";
    }

    private static T PickWeighted<T>(WeightedEntry<T>[] entries)
    {
        int total = 0;
        foreach (var e in entries) total += e.Weight;
        int roll = Random.Shared.Next(total);
        int cumulative = 0;
        foreach (var e in entries)
        {
            cumulative += e.Weight;
            if (roll < cumulative) return e.Value;
        }
        return entries[^1].Value;
    }

    private static WeightedEntry<OperatorConfig>[] BuildOperators(ColombianNumberOptions? options)
    {
        if (options?.Operators is { Count: > 0 } overrides)
        {
            return [.. overrides.Select(kvp => new WeightedEntry<OperatorConfig>(
                kvp.Value.Weight,
                new OperatorConfig(kvp.Key, [.. kvp.Value.Prefixes])))];
        }

        return
        [
            new(45, new OperatorConfig("Claro",    ["310","311","312","313","314","320","321","322","323"])),
            new(25, new OperatorConfig("Movistar", ["315","316","317","318"])),
            new(15, new OperatorConfig("Tigo",     ["300","301","302","303","304"])),
            new(15, new OperatorConfig("WOM",      ["350","351"]))
        ];
    }

    private static WeightedEntry<LandlineConfig>[] BuildLandlines(ColombianNumberOptions? options)
    {
        if (options?.Landlines is { Count: > 0 } overrides)
        {
            return [.. overrides.Select(kvp => new WeightedEntry<LandlineConfig>(
                kvp.Value.Weight,
                new LandlineConfig(kvp.Key, kvp.Value.Prefix)))];
        }

        return
        [
            new(50, new LandlineConfig("Bogota",   "601")),
            new(25, new LandlineConfig("Medellin", "604")),
            new(25, new LandlineConfig("Cali",     "602"))
        ];
    }

    // --- nested types ---

    private sealed record WeightedEntry<T>(int Weight, T Value);

    private sealed record OperatorConfig(string Name, string[] Prefixes);

    private sealed record LandlineConfig(string City, string Prefix);
}
