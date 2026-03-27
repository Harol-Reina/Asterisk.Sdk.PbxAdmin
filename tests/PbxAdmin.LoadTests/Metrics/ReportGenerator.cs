using System.Text.Json;
using PbxAdmin.LoadTests.Validation;

namespace PbxAdmin.LoadTests.Metrics;

/// <summary>
/// Generates console and JSON reports from a completed test run.
/// </summary>
public static class ReportGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Writes a formatted summary table to the console.
    /// </summary>
    public static void WriteConsoleReport(ValidationReport report, MetricsSummary metrics)
    {
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine("  PbxAdmin Load Test Report");
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine();

        // Test timing
        Console.WriteLine("  TIMING");
        Console.WriteLine($"    Duration        : {metrics.Elapsed:mm\\:ss}");
        Console.WriteLine($"    Start           : {report.TestStart:yyyy-MM-dd HH:mm:ss} UTC");
        Console.WriteLine($"    End             : {report.TestEnd:yyyy-MM-dd HH:mm:ss} UTC");
        Console.WriteLine();

        // Agent metrics
        Console.WriteLine("  AGENTS");
        Console.WriteLine($"    Registered      : {metrics.TotalAgents}");
        Console.WriteLine($"    Peak In-Call     : {metrics.PeakAgentsInCall}");
        Console.WriteLine($"    Errors          : {metrics.AgentErrors}");
        Console.WriteLine();

        // Call metrics
        Console.WriteLine("  CALL METRICS");
        Console.WriteLine($"    Calls Originated: {metrics.CallsOriginated}");
        Console.WriteLine($"    Calls Answered  : {metrics.CallsAnswered}");
        Console.WriteLine($"    Calls Failed    : {metrics.CallsFailed}");
        Console.WriteLine($"    Answer Rate     : {metrics.AnswerRate:F1}%");
        Console.WriteLine($"    Calls/Minute    : {metrics.CallsPerMinute:F2}");
        Console.WriteLine($"    Peak Concurrent : {metrics.PeakConcurrentCalls}");
        Console.WriteLine();

        // Validation metrics
        Console.WriteLine("  VALIDATION");
        Console.WriteLine($"    Total Checks    : {report.TotalChecks}");
        Console.WriteLine($"    Passed          : {report.PassedChecks}");
        Console.WriteLine($"    Failed          : {report.FailedChecks}");
        Console.WriteLine($"    Pass Rate       : {report.PassRate:F1}%");
        Console.WriteLine();

        // SDK bugs
        if (report.SdkBugsFound.Count > 0)
        {
            Console.WriteLine("  SDK BUGS FOUND");
            foreach (var bug in report.SdkBugsFound)
                Console.WriteLine($"    [!] {bug}");
            Console.WriteLine();
        }

        // Top failures
        if (report.Failures.Count > 0)
        {
            int topCount = Math.Min(report.Failures.Count, 10);
            Console.WriteLine($"  TOP FAILURES (showing {topCount} of {report.Failures.Count})");
            foreach (var failure in report.Failures.Take(topCount))
            {
                Console.WriteLine($"    [{failure.ValidatorName}] Call={failure.CallId}");
                foreach (var check in failure.Checks.Where(c => !c.Passed))
                    Console.WriteLine($"      - {check.CheckName}: {check.Message}");
            }
            Console.WriteLine();
        }

        // Overall result
        bool passed = report.FailedChecks == 0 && report.SdkBugsFound.Count == 0;
        Console.WriteLine(passed
            ? "  RESULT: PASSED"
            : "  RESULT: FAILED");
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine();
    }

    /// <summary>
    /// Writes a formatted summary table to the console, including Docker infrastructure stats.
    /// </summary>
    public static void WriteConsoleReport(
        ValidationReport report,
        MetricsSummary metrics,
        DockerStatsSummary? dockerStats)
    {
        WriteConsoleReport(report, metrics);

        if (dockerStats is null) return;
        WriteDockerConsoleSection(dockerStats);
    }

    /// <summary>
    /// Writes the full report as a JSON file at <paramref name="outputPath"/>.
    /// </summary>
    public static void WriteJsonReport(
        ValidationReport report,
        MetricsSummary metrics,
        string outputPath)
    {
        WriteJsonReport(report, metrics, dockerStats: null, outputPath);
    }

    /// <summary>
    /// Writes the full report as a JSON file at <paramref name="outputPath"/>,
    /// including Docker infrastructure stats when available.
    /// </summary>
    public static void WriteJsonReport(
        ValidationReport report,
        MetricsSummary metrics,
        DockerStatsSummary? dockerStats,
        string outputPath)
    {
        var metricsSection = new
        {
            elapsed = metrics.Elapsed.ToString(),
            callsOriginated = metrics.CallsOriginated,
            callsAnswered = metrics.CallsAnswered,
            callsFailed = metrics.CallsFailed,
            answerRate = Math.Round(metrics.AnswerRate, 2),
            callsPerMinute = metrics.CallsPerMinute,
            peakConcurrentCalls = metrics.PeakConcurrentCalls,
            totalAgents = metrics.TotalAgents,
            peakAgentsInCall = metrics.PeakAgentsInCall,
            agentErrors = metrics.AgentErrors
        };

        var validationSection = new
        {
            testStart = report.TestStart,
            testEnd = report.TestEnd,
            totalCalls = report.TotalCalls,
            totalChecks = report.TotalChecks,
            passedChecks = report.PassedChecks,
            failedChecks = report.FailedChecks,
            passRate = Math.Round(report.PassRate, 2),
            sdkBugsFound = report.SdkBugsFound,
            failures = report.Failures.Select(f => new
            {
                callId = f.CallId,
                validatorName = f.ValidatorName,
                failedChecks = f.Checks
                    .Where(c => !c.Passed)
                    .Select(c => new
                    {
                        checkName = c.CheckName,
                        expected = c.Expected,
                        actual = c.Actual,
                        message = c.Message
                    })
            })
        };

        object payload;
        if (dockerStats is not null)
        {
            var containers = dockerStats.Containers
                .OrderBy(kvp => kvp.Key)
                .ToDictionary(
                    kvp => kvp.Key,
                    kvp => (object)new
                    {
                        cpu = new
                        {
                            min = Math.Round(kvp.Value.CpuMin, 2),
                            avg = Math.Round(kvp.Value.CpuAvg, 2),
                            max = Math.Round(kvp.Value.CpuMax, 2),
                            atPeakCalls = Math.Round(kvp.Value.CpuAtPeakCalls, 2)
                        },
                        memoryMb = new
                        {
                            min = Math.Round(kvp.Value.MemoryMinMb, 1),
                            avg = Math.Round(kvp.Value.MemoryAvgMb, 1),
                            max = Math.Round(kvp.Value.MemoryMaxMb, 1),
                            atPeakCalls = Math.Round(kvp.Value.MemoryAtPeakCallsMb, 1),
                            limit = Math.Round(kvp.Value.MemoryLimitMb, 1)
                        },
                        networkBytes = new
                        {
                            rx = kvp.Value.NetworkRxBytes,
                            tx = kvp.Value.NetworkTxBytes
                        },
                        peakPids = kvp.Value.PeakPids,
                        sampleCount = kvp.Value.SampleCount
                    });

            object? capacitySection = dockerStats.CapacityEstimate is not null
                ? new
                {
                    peakConcurrentCalls = dockerStats.CapacityEstimate.PeakConcurrentCalls,
                    asteriskCpuPercent = Math.Round(dockerStats.CapacityEstimate.AsteriskCpuPercent, 2),
                    asteriskMemoryMb = Math.Round(dockerStats.CapacityEstimate.AsteriskMemoryMb, 1)
                }
                : null;

            payload = new
            {
                generatedAt = DateTime.UtcNow,
                metrics = metricsSection,
                validation = validationSection,
                infrastructure = new
                {
                    containers,
                    capacityEstimate = capacitySection
                }
            };
        }
        else
        {
            payload = new
            {
                generatedAt = DateTime.UtcNow,
                metrics = metricsSection,
                validation = validationSection
            };
        }

        string json = JsonSerializer.Serialize(payload, JsonOptions);
        File.WriteAllText(outputPath, json);
    }

    private static void WriteDockerConsoleSection(DockerStatsSummary dockerStats)
    {
        Console.WriteLine();
        Console.WriteLine("  INFRASTRUCTURE (Docker)");

        foreach (var kvp in dockerStats.Containers.OrderBy(c => c.Key))
        {
            var c = kvp.Value;
            string label = c.ContainerName == DockerContainerNames.PrimaryTarget
                ? $"    {c.ContainerName}  [PRIMARY]"
                : $"    {c.ContainerName}";
            Console.WriteLine(label);
            Console.WriteLine($"      CPU%        : min={c.CpuMin:F2}  avg={c.CpuAvg:F2}  max={c.CpuMax:F2}");
            Console.WriteLine($"      Memory (MB) : min={c.MemoryMinMb:F1}  avg={c.MemoryAvgMb:F1}  max={c.MemoryMaxMb:F1} / {c.MemoryLimitMb:F1}");
            Console.WriteLine($"      Network     : RX={FormatBytes(c.NetworkRxBytes)}  TX={FormatBytes(c.NetworkTxBytes)}");
            Console.WriteLine($"      Processes   : peak={c.PeakPids}");
            Console.WriteLine();
        }

        if (dockerStats.CapacityEstimate is not null)
        {
            var cap = dockerStats.CapacityEstimate;
            Console.WriteLine("  CAPACITY ESTIMATE");
            Console.WriteLine($"    Peak Concurrent Calls : {cap.PeakConcurrentCalls}");
            Console.WriteLine($"    Asterisk CPU          : {cap.AsteriskCpuPercent:F2}%");
            Console.WriteLine($"    Asterisk Memory       : {cap.AsteriskMemoryMb:F1} MB");
        }

        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024.0:F1} kB";
        if (bytes < 1024L * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }
}
