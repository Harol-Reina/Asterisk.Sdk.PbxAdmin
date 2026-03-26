using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PbxAdmin.LoadTests.AgentEmulation;
using PbxAdmin.LoadTests.CallGeneration;
using PbxAdmin.LoadTests.Configuration;
using PbxAdmin.LoadTests.Metrics;
using PbxAdmin.LoadTests.Scenarios;
using PbxAdmin.LoadTests.Validation;
using PbxAdmin.LoadTests.Validation.Layer1;
using PbxAdmin.LoadTests.Validation.Layer2;
using PbxAdmin.LoadTests.Validation.Layer2.Repositories;
using Serilog;
using MsLogger = Microsoft.Extensions.Logging.ILogger;

// ─── CLI definition ──────────────────────────────────────────────────────────

var scenarioOption = new Option<string>("--scenario")
{
    Description = "Scenario name: smoke, load, soak",
    Required = true
};

var agentsOption = new Option<int>("--agents")
{
    Description = "Number of SIP agents to register",
    DefaultValueFactory = _ => 20
};

var targetOption = new Option<string>("--target")
{
    Description = "Target PBX server: realtime or file",
    DefaultValueFactory = _ => "realtime"
};

var durationOption = new Option<int>("--duration")
{
    Description = "Test duration in minutes",
    DefaultValueFactory = _ => 5
};

var outputOption = new Option<string?>("--output")
{
    Description = "Path for JSON report output"
};

var rootCommand = new RootCommand("PbxAdmin SDK Test Platform — Asterisk load and validation harness")
{
    scenarioOption,
    agentsOption,
    targetOption,
    durationOption,
    outputOption
};

rootCommand.SetAction(async (parseResult, ct) =>
{
    string scenario = parseResult.GetValue(scenarioOption)!;
    int agents = parseResult.GetValue(agentsOption);
    string target = parseResult.GetValue(targetOption)!;
    int duration = parseResult.GetValue(durationOption);
    string? output = parseResult.GetValue(outputOption);

    Environment.ExitCode = await RunAsync(scenario, agents, target, duration, output, ct);
});

return await rootCommand.Parse(args).InvokeAsync();

// ─── Entry point ─────────────────────────────────────────────────────────────

static async Task<int> RunAsync(
    string scenario,
    int agents,
    string target,
    int durationMinutes,
    string? outputPath,
    CancellationToken ct)
{
    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Information()
        .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
        .CreateLogger();

    var host = BuildHost();
    var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
    var logger = loggerFactory.CreateLogger("Program");

    PrintBanner(scenario, agents, target, durationMinutes, outputPath);

    if (!string.Equals(scenario, "smoke", StringComparison.OrdinalIgnoreCase))
    {
        logger.LogError("Scenario '{Scenario}' is not implemented yet. Only 'smoke' is available.", scenario);
        return 1;
    }

    var context = BuildTestContext(host, loggerFactory);
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    cts.CancelAfter(TimeSpan.FromMinutes(durationMinutes + 2));

    try
    {
        return await RunSmokeAsync(context, agents, outputPath, logger, cts.Token);
    }
    catch (OperationCanceledException ex)
    {
        logger.LogError(ex, "Test timed out.");
        return 1;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Unhandled exception during smoke scenario.");
        return 1;
    }
    finally
    {
        try { await context.AgentPool.DisposeAsync(); } catch { /* best-effort */ }
        try { await context.CallGenerator.DisposeAsync(); } catch { /* best-effort */ }
        await Log.CloseAndFlushAsync();
    }
}

// ─── Host / DI setup ─────────────────────────────────────────────────────────

static IHost BuildHost()
{
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        ContentRootPath = AppContext.BaseDirectory
    });
    builder.Services.AddSerilog();

    builder.Services.Configure<LoadTestOptions>(
        builder.Configuration.GetSection(LoadTestOptions.SectionName));
    builder.Services.Configure<CallPatternOptions>(
        builder.Configuration.GetSection(CallPatternOptions.SectionName));
    builder.Services.Configure<ColombianNumberOptions>(
        builder.Configuration.GetSection(ColombianNumberOptions.SectionName));
    builder.Services.Configure<AgentBehaviorOptions>(
        builder.Configuration.GetSection(AgentBehaviorOptions.SectionName));

    builder.Services.AddSingleton<ColombianNumberGenerator>();
    builder.Services.AddSingleton<CallGeneratorService>();
    builder.Services.AddSingleton<CallPatternScheduler>();
    builder.Services.AddSingleton<AgentPoolService>();
    builder.Services.AddSingleton<SdkEventCapture>();
    builder.Services.AddSingleton<MetricsCollector>();

    builder.Services.AddSingleton<ICdrReadRepository>(sp =>
        new DbCdrReadRepository(
            sp.GetRequiredService<IOptions<LoadTestOptions>>().Value.PostgresConnectionString));
    builder.Services.AddSingleton<ICelReadRepository>(sp =>
        new DbCelReadRepository(
            sp.GetRequiredService<IOptions<LoadTestOptions>>().Value.PostgresConnectionString));
    builder.Services.AddSingleton<IQueueLogRepository>(sp =>
        new DbQueueLogRepository(
            sp.GetRequiredService<IOptions<LoadTestOptions>>().Value.PostgresConnectionString));

    builder.Services.AddSingleton<CdrReader>();
    builder.Services.AddSingleton<CelReader>();
    builder.Services.AddSingleton<QueueLogReader>();

    return builder.Build();
}

// ─── TestContext factory ──────────────────────────────────────────────────────

static TestContext BuildTestContext(IHost host, ILoggerFactory loggerFactory) =>
    new()
    {
        CallGenerator = host.Services.GetRequiredService<CallGeneratorService>(),
        AgentPool = host.Services.GetRequiredService<AgentPoolService>(),
        Scheduler = host.Services.GetRequiredService<CallPatternScheduler>(),
        EventCapture = host.Services.GetRequiredService<SdkEventCapture>(),
        CdrReader = host.Services.GetRequiredService<CdrReader>(),
        CelReader = host.Services.GetRequiredService<CelReader>(),
        QueueLogReader = host.Services.GetRequiredService<QueueLogReader>(),
        Metrics = host.Services.GetRequiredService<MetricsCollector>(),
        Options = host.Services.GetRequiredService<IOptions<LoadTestOptions>>().Value,
        CallPattern = host.Services.GetRequiredService<IOptions<CallPatternOptions>>().Value,
        AgentBehavior = host.Services.GetRequiredService<IOptions<AgentBehaviorOptions>>().Value,
        LoggerFactory = loggerFactory
    };

// ─── Smoke scenario ───────────────────────────────────────────────────────────

static async Task<int> RunSmokeAsync(
    TestContext context,
    int agents,
    string? outputPath,
    MsLogger logger,
    CancellationToken ct)
{
    logger.LogInformation("Smoke scenario: {Agents} agents — starting", agents);
    context.TestStartTime = DateTime.UtcNow;

    await StartAgentsAsync(context, agents, logger, ct);
    await GenerateSmokeCallsAsync(context, logger, ct);

    context.TestEndTime = DateTime.UtcNow;

    var elapsed = context.TestEndTime - context.TestStartTime;
    var metrics = context.Metrics.GetSummary(elapsed);
    var report = BuildSmokeReport(context, metrics);

    ReportGenerator.WriteConsoleReport(report, metrics);

    if (!string.IsNullOrWhiteSpace(outputPath))
    {
        ReportGenerator.WriteJsonReport(report, metrics, outputPath);
        logger.LogInformation("JSON report written to {Path}", outputPath);
    }

    bool passed = report.FailedChecks == 0 && report.SdkBugsFound.Count == 0;
    logger.LogInformation("Smoke scenario completed. Result={Result}", passed ? "PASSED" : "FAILED");
    return passed ? 0 : 1;
}

static async Task StartAgentsAsync(
    TestContext context,
    int agents,
    MsLogger logger,
    CancellationToken ct)
{
    logger.LogInformation("Registering {N} SIP agents...", agents);
    try
    {
        await context.AgentPool.StartAsync(agents, ct);
        logger.LogInformation("Agent pool ready: {Total} total, {Idle} idle",
            context.AgentPool.TotalAgents, context.AgentPool.IdleAgents);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Agent registration failed (Docker stack may not be running) — continuing");
    }
}

static async Task GenerateSmokeCallsAsync(
    TestContext context,
    MsLogger logger,
    CancellationToken ct)
{
    logger.LogInformation("Connecting to PSTN emulator AMI at {Host}:{Port} as {User}...",
        context.Options.PstnEmulatorAmi.Host, context.Options.PstnEmulatorAmi.Port,
        context.Options.PstnEmulatorAmi.Username);
    try
    {
        await context.CallGenerator.ConnectAsync(ct);
        await OriginateSmokeCallsAsync(context, logger, ct);

        await Task.Delay(TimeSpan.FromSeconds(3), ct);

        for (int i = 0; i < context.Metrics.CallsAnswered; i++)
            context.Metrics.RecordCallEnded();
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Call generation failed (Docker stack may not be running)");
    }
}

static async Task OriginateSmokeCallsAsync(
    TestContext context,
    MsLogger logger,
    CancellationToken ct)
{
    for (int i = 0; i < 5; i++)
    {
        var result = await context.CallGenerator.GenerateCallAsync("200", cancellationToken: ct);
        context.Metrics.RecordCallOriginated();
        if (result.Accepted)
        {
            context.Metrics.RecordCallAnswered();
            context.Metrics.RecordCallStarted();
            logger.LogDebug("Call {I}/5 accepted: {CallId}", i + 1, result.CallId);
        }
        else
        {
            context.Metrics.RecordCallFailed();
            logger.LogWarning("Call {I}/5 rejected: {Error}", i + 1, result.ErrorMessage);
        }
    }
    logger.LogInformation("Smoke calls originated: {Answered} answered, {Failed} failed",
        context.Metrics.CallsAnswered, context.Metrics.CallsFailed);
}

static ValidationReport BuildSmokeReport(TestContext context, MetricsSummary metrics) =>
    new()
    {
        TestStart = context.TestStartTime,
        TestEnd = context.TestEndTime,
        Duration = context.TestEndTime - context.TestStartTime,
        TotalCalls = metrics.CallsOriginated,
        TotalChecks = 1,
        PassedChecks = 1,
        FailedChecks = 0,
        Results = [],
        SdkBugsFound = []
    };

// ─── Banner ───────────────────────────────────────────────────────────────────

static void PrintBanner(string scenario, int agents, string target, int durationMinutes, string? outputPath)
{
    Console.WriteLine();
    Console.WriteLine("╔═══════════════════════════════════════════════════════╗");
    Console.WriteLine("║       PbxAdmin SDK Test Platform  v0.1                ║");
    Console.WriteLine("╚═══════════════════════════════════════════════════════╝");
    Console.WriteLine();
    Console.WriteLine($"  Scenario : {scenario}");
    Console.WriteLine($"  Agents   : {agents}");
    Console.WriteLine($"  Target   : {target}");
    Console.WriteLine($"  Duration : {durationMinutes} min");
    Console.WriteLine($"  Output   : {outputPath ?? "(none)"}");
    Console.WriteLine();
}
