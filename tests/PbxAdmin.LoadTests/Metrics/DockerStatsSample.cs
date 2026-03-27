using System.Globalization;
using System.Text.Json;

namespace PbxAdmin.LoadTests.Metrics;

/// <summary>
/// One point-in-time measurement for one container, parsed from
/// <c>docker stats --no-stream --format '{{json .}}'</c> JSON output.
/// </summary>
public sealed record DockerStatsSample
{
    public required DateTime Timestamp { get; init; }
    public required string ContainerName { get; init; }
    public double CpuPercent { get; init; }
    public double MemoryUsageMb { get; init; }
    public double MemoryLimitMb { get; init; }
    public double MemoryPercent { get; init; }
    public long NetworkRxBytes { get; init; }
    public long NetworkTxBytes { get; init; }
    public int Pids { get; init; }
    public int ConcurrentCalls { get; init; }

    /// <summary>Parses one JSON line from docker stats output.</summary>
    public static DockerStatsSample? TryParse(string json, DateTime timestamp, int concurrentCalls = 0)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string name = root.GetProperty("Name").GetString()!;
            double cpuPercent = ParsePercent(root.GetProperty("CPUPerc").GetString() ?? "");
            double memPercent = ParsePercent(root.GetProperty("MemPerc").GetString() ?? "");
            int pids = int.Parse(root.GetProperty("PIDs").GetString() ?? "0", CultureInfo.InvariantCulture);

            // MemUsage: "83.71MiB / 60.49GiB"
            string memUsage = root.GetProperty("MemUsage").GetString() ?? "";
            string[] memParts = memUsage.Split(" / ");
            double memUsageMb = memParts.Length >= 1 ? ParseByteSize(memParts[0]) / 1_048_576.0 : 0;
            double memLimitMb = memParts.Length >= 2 ? ParseByteSize(memParts[1]) / 1_048_576.0 : 0;

            // NetIO: "56.8MB / 111MB"
            string netIo = root.GetProperty("NetIO").GetString() ?? "";
            string[] netParts = netIo.Split(" / ");
            long netRx = netParts.Length >= 1 ? ParseByteSize(netParts[0]) : 0;
            long netTx = netParts.Length >= 2 ? ParseByteSize(netParts[1]) : 0;

            return new DockerStatsSample
            {
                Timestamp = timestamp,
                ContainerName = name,
                CpuPercent = cpuPercent,
                MemoryUsageMb = memUsageMb,
                MemoryLimitMb = memLimitMb,
                MemoryPercent = memPercent,
                NetworkRxBytes = netRx,
                NetworkTxBytes = netTx,
                Pids = pids,
                ConcurrentCalls = concurrentCalls
            };
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Parses a human-readable byte size ("83.71MiB", "56.8MB", "573kB", "0B") to bytes.</summary>
    internal static long ParseByteSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        text = text.Trim();

        if (text == "0B")
            return 0;

        // Order matters: check longer suffixes first to avoid partial matches
        if (text.EndsWith("GiB", StringComparison.Ordinal))
        {
            double value = double.Parse(text[..^3], CultureInfo.InvariantCulture);
            return (long)(value * 1_073_741_824);
        }

        if (text.EndsWith("MiB", StringComparison.Ordinal))
        {
            double value = double.Parse(text[..^3], CultureInfo.InvariantCulture);
            return (long)(value * 1_048_576);
        }

        if (text.EndsWith("kB", StringComparison.Ordinal))
        {
            double value = double.Parse(text[..^2], CultureInfo.InvariantCulture);
            return (long)(value * 1_000);
        }

        if (text.EndsWith("MB", StringComparison.Ordinal))
        {
            double value = double.Parse(text[..^2], CultureInfo.InvariantCulture);
            return (long)(value * 1_000_000);
        }

        if (text.EndsWith("GB", StringComparison.Ordinal))
        {
            double value = double.Parse(text[..^2], CultureInfo.InvariantCulture);
            return (long)(value * 1_000_000_000);
        }

        if (text.EndsWith("B", StringComparison.Ordinal))
        {
            double value = double.Parse(text[..^1], CultureInfo.InvariantCulture);
            return (long)value;
        }

        return 0;
    }

    /// <summary>Parses a percentage string ("0.19%") to double.</summary>
    internal static double ParsePercent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0.0;

        text = text.Trim();

        if (text.EndsWith('%'))
        {
            return double.Parse(text[..^1], CultureInfo.InvariantCulture);
        }

        return 0.0;
    }
}
