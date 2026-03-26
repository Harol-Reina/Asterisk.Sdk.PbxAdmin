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
    /// Writes the full report as a JSON file at <paramref name="outputPath"/>.
    /// </summary>
    public static void WriteJsonReport(
        ValidationReport report,
        MetricsSummary metrics,
        string outputPath)
    {
        var payload = new
        {
            generatedAt = DateTime.UtcNow,
            metrics = new
            {
                elapsed = metrics.Elapsed.ToString(),
                callsOriginated = metrics.CallsOriginated,
                callsAnswered = metrics.CallsAnswered,
                callsFailed = metrics.CallsFailed,
                answerRate = Math.Round(metrics.AnswerRate, 2),
                callsPerMinute = metrics.CallsPerMinute,
                peakConcurrentCalls = metrics.PeakConcurrentCalls
            },
            validation = new
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
            }
        };

        string json = JsonSerializer.Serialize(payload, JsonOptions);
        File.WriteAllText(outputPath, json);
    }
}
