using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Metrics;

namespace PbxAdmin.LoadTests.Auditing;

/// <summary>
/// Background service that periodically snapshots all Docker stack components
/// and writes results to JSONL (streaming) and JSON (consolidated) files.
/// </summary>
public sealed class AuditMonitorService : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private static readonly JsonSerializerOptions JsonPrettyOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private readonly ILogger<AuditMonitorService> _logger;
    private readonly AsteriskCliCollector _asteriskCollector;
    private readonly ContainerLogCollector _logCollector;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private DateTime _startedAt;
    private string _jsonlPath = "";
    private string _jsonPath = "";
    private int _intervalSeconds;
    private int _sequenceNumber;
    private readonly List<AuditSnapshot> _snapshots = [];
    private readonly string _queueName;

    public int SnapshotCount => _snapshots.Count;
    public int TotalErrors => _snapshots.Sum(s => s.Errors.Length);

    public AuditMonitorService(ILoggerFactory loggerFactory, string queueName = "loadtest")
    {
        _logger = loggerFactory.CreateLogger<AuditMonitorService>();
        _asteriskCollector = new AsteriskCliCollector(
            loggerFactory.CreateLogger<AsteriskCliCollector>());
        _logCollector = new ContainerLogCollector(
            loggerFactory.CreateLogger<ContainerLogCollector>());
        _queueName = queueName;
    }

    /// <summary>Starts the background audit loop.</summary>
    public Task StartAsync(int intervalSeconds, string outputBasePath, CancellationToken ct)
    {
        if (intervalSeconds <= 0)
        {
            _logger.LogInformation("Audit monitor disabled (interval=0)");
            return Task.CompletedTask;
        }

        _intervalSeconds = Math.Max(intervalSeconds, 5);
        _jsonlPath = outputBasePath + ".audit.jsonl";
        _jsonPath = outputBasePath + ".audit.json";
        _startedAt = DateTime.UtcNow;
        _sequenceNumber = 0;

        _cts?.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = RunLoopAsync(_cts.Token);

        _logger.LogInformation(
            "Audit monitor started: interval={Interval}s, jsonl={Jsonl}, json={Json}",
            _intervalSeconds, _jsonlPath, _jsonPath);

        return Task.CompletedTask;
    }

    /// <summary>Stops the loop and writes the consolidated JSON report.</summary>
    public async Task<AuditReport> StopAsync()
    {
        if (_cts is not null)
            await _cts.CancelAsync();

        if (_loop is not null)
        {
            try { await _loop; }
            catch (OperationCanceledException) { }
            _loop = null;
        }

        _cts?.Dispose();
        _cts = null;

        var report = new AuditReport
        {
            TestStarted = _startedAt,
            TestEnded = DateTime.UtcNow,
            IntervalSeconds = _intervalSeconds,
            SnapshotCount = _snapshots.Count,
            Snapshots = _snapshots.ToArray()
        };

        // Write consolidated JSON
        if (!string.IsNullOrEmpty(_jsonPath) && _snapshots.Count > 0)
        {
            try
            {
                string json = JsonSerializer.Serialize(report, JsonPrettyOptions);
                await File.WriteAllTextAsync(_jsonPath, json);
                _logger.LogInformation("Audit report written: {Path} ({Count} snapshots)",
                    _jsonPath, _snapshots.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write consolidated audit JSON");
            }
        }

        return report;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    // ── Background loop ───────────────────────────────────────────────────

    private async Task RunLoopAsync(CancellationToken ct)
    {
        _logger.LogInformation("Audit loop started, ct.IsCancellationRequested={IsCancelled}", ct.IsCancellationRequested);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // Spurious cancellation — continue loop
                continue;
            }

            try
            {
                var snapshot = await CollectSnapshotAsync(ct);
                _snapshots.Add(snapshot);
                await AppendJsonlAsync(snapshot);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Audit snapshot #{Seq} failed — continuing", _sequenceNumber);
            }
        }

        _logger.LogInformation("Audit loop ended after {Count} snapshots. ct.IsCancellationRequested={IsCancelled}",
            _snapshots.Count, ct.IsCancellationRequested);
    }

    private async Task<AuditSnapshot> CollectSnapshotAsync(CancellationToken ct)
    {
        _sequenceNumber++;
        double elapsed = (DateTime.UtcNow - _startedAt).TotalSeconds;

        // Collect all in parallel
        var dockerStatsTask = CollectDockerStatsAsync(ct);
        var realtimeTask = _asteriskCollector.CollectRealtimeAsync(_queueName, ct);
        var pstnTask = _asteriskCollector.CollectBasicAsync(DockerContainerNames.Pstn, ct);
        var fileTask = _asteriskCollector.CollectBasicAsync(DockerContainerNames.PbxFile, ct);
        var errorsTask = _logCollector.CollectNewErrorsAsync(ct);

        await Task.WhenAll(dockerStatsTask, realtimeTask, pstnTask, fileTask, errorsTask);

        return new AuditSnapshot
        {
            Timestamp = DateTime.UtcNow,
            ElapsedSeconds = elapsed,
            SequenceNumber = _sequenceNumber,
            Containers = await dockerStatsTask,
            Realtime = await realtimeTask,
            Pstn = await pstnTask,
            File = await fileTask,
            Errors = await errorsTask
        };
    }

    private async Task<ContainerSnapshot[]> CollectDockerStatsAsync(CancellationToken ct)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"stats --no-stream --format \"{{{{.Name}}}}|{{{{.CPUPerc}}}}|{{{{.MemUsage}}}}|{{{{.NetIO}}}}\" {string.Join(' ', DockerContainerNames.All)}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
                return [];

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(10_000);

            string output = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            }

            return ParseDockerStats(output);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "docker stats collection failed");
            return [];
        }
    }

    /// <summary>Parses docker stats pipe-separated output.</summary>
    internal static ContainerSnapshot[] ParseDockerStats(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return [];

        var snapshots = new List<ContainerSnapshot>();

        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            string[] parts = trimmed.Split('|');
            if (parts.Length < 4)
                continue;

            string name = parts[0].Trim();
            double cpu = DockerStatsSample.ParsePercent(parts[1]);

            string[] memParts = parts[2].Split(" / ");
            double memUsage = memParts.Length >= 1
                ? DockerStatsSample.ParseByteSize(memParts[0].Trim()) / 1_048_576.0
                : 0;
            double memLimit = memParts.Length >= 2
                ? DockerStatsSample.ParseByteSize(memParts[1].Trim()) / 1_048_576.0
                : 0;

            string[] netParts = parts[3].Split(" / ");
            double netIn = netParts.Length >= 1
                ? DockerStatsSample.ParseByteSize(netParts[0].Trim()) / 1_048_576.0
                : 0;
            double netOut = netParts.Length >= 2
                ? DockerStatsSample.ParseByteSize(netParts[1].Trim()) / 1_048_576.0
                : 0;

            snapshots.Add(new ContainerSnapshot
            {
                Name = name,
                CpuPercent = cpu,
                MemUsageMB = Math.Round(memUsage, 2),
                MemLimitMB = Math.Round(memLimit, 2),
                NetInputMB = Math.Round(netIn, 2),
                NetOutputMB = Math.Round(netOut, 2)
            });
        }

        return snapshots.ToArray();
    }

    private async Task AppendJsonlAsync(AuditSnapshot snapshot)
    {
        if (string.IsNullOrEmpty(_jsonlPath))
            return;

        try
        {
            string line = JsonSerializer.Serialize(snapshot, JsonOptions);
            await File.AppendAllTextAsync(_jsonlPath, line + "\n");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append audit JSONL");
        }
    }
}
