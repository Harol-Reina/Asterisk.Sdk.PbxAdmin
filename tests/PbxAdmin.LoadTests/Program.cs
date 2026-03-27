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
using PbxAdmin.LoadTests.Sdk;
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

var talkTimeOption = new Option<int?>("--talk-time")
{
    Description = "Agent talk time in seconds (overrides appsettings AgentBehavior.TalkTimeSecs)"
};

var maxConcurrentOption = new Option<int?>("--max-concurrent")
{
    Description = "Max concurrent calls (overrides appsettings and auto-tune)"
};

var rootCommand = new RootCommand("PbxAdmin SDK Test Platform — Asterisk load and validation harness")
{
    scenarioOption,
    agentsOption,
    targetOption,
    durationOption,
    outputOption,
    talkTimeOption,
    maxConcurrentOption
};

rootCommand.SetAction(async (parseResult, ct) =>
{
    string scenario = parseResult.GetValue(scenarioOption)!;
    int agents = parseResult.GetValue(agentsOption);
    string target = parseResult.GetValue(targetOption)!;
    int duration = parseResult.GetValue(durationOption);
    string? output = parseResult.GetValue(outputOption);
    int? talkTime = parseResult.GetValue(talkTimeOption);
    int? maxConcurrent = parseResult.GetValue(maxConcurrentOption);

    Environment.ExitCode = await RunAsync(scenario, agents, target, duration, output, talkTime, maxConcurrent, ct);
});

return await rootCommand.Parse(args).InvokeAsync();

// ─── Entry point ─────────────────────────────────────────────────────────────

static async Task<int> RunAsync(
    string scenario,
    int agents,
    string target,
    int durationMinutes,
    string? outputPath,
    int? talkTime,
    int? maxConcurrent,
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

    var testScenario = ScenarioRegistry.Get(scenario);
    if (testScenario is null)
    {
        logger.LogError("Unknown scenario '{Scenario}'. Available: {Names}",
            scenario, string.Join(", ", ScenarioRegistry.Names));
        return 1;
    }

    // CLI --duration overrides appsettings.json TestDurationMinutes
    var callPatternOpts = host.Services.GetRequiredService<IOptions<CallPatternOptions>>().Value;
    callPatternOpts.TestDurationMinutes = durationMinutes;

    // Auto-tune: cap MaxConcurrentCalls to agent count (never more calls than agents)
    if (callPatternOpts.MaxConcurrentCalls > agents)
    {
        logger.LogInformation("Auto-tune: MaxConcurrentCalls {Old} → {New} (capped to agent count)",
            callPatternOpts.MaxConcurrentCalls, agents);
        callPatternOpts.MaxConcurrentCalls = agents;
    }

    // CLI overrides (take precedence over auto-tune and appsettings)
    if (maxConcurrent.HasValue)
    {
        callPatternOpts.MaxConcurrentCalls = maxConcurrent.Value;
        logger.LogInformation("CLI override: MaxConcurrentCalls = {Value}", maxConcurrent.Value);
    }

    var agentBehaviorOpts = host.Services.GetRequiredService<IOptions<AgentBehaviorOptions>>().Value;
    if (talkTime.HasValue)
    {
        agentBehaviorOpts.TalkTimeSecs = talkTime.Value;
        logger.LogInformation("CLI override: TalkTimeSecs = {Value}s", talkTime.Value);
    }

    // Always sync scheduler slot duration with the effective agent talk time.
    // Without this, the scheduler holds slots for DefaultCallDurationSecs (180s)
    // while agents hang up after TalkTimeSecs (30s) — causing a desync where
    // slots block new calls long after agents are idle.
    int cycleSecs = agentBehaviorOpts.TalkTimeSecs + agentBehaviorOpts.RingDelaySecs + agentBehaviorOpts.WrapupTimeSecs;
    callPatternOpts.DefaultCallDurationSecs = cycleSecs;
    callPatternOpts.MinCallDurationSecs = cycleSecs;
    callPatternOpts.MaxCallDurationSecs = cycleSecs;
    logger.LogInformation("Auto-tune: scheduler slot = {Cycle}s (talk={Talk}s + ring={Ring}s + wrapup={Wrap}s)",
        cycleSecs, agentBehaviorOpts.TalkTimeSecs, agentBehaviorOpts.RingDelaySecs, agentBehaviorOpts.WrapupTimeSecs);

    var context = BuildTestContext(host, loggerFactory);

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    cts.CancelAfter(TimeSpan.FromMinutes(durationMinutes + 5));

    try
    {
        // Attach metrics to services that generate calls/manage agents
        context.Scheduler.AttachMetrics(context.Metrics);

        // SDK first (provides AMI connection to target PBX for provisioning reload)
        await StartSdkAsync(context, host.Services, logger, cts.Token);

        // Provision PJSIP endpoints + queue members, reload Asterisk, then register SIP agents
        await ProvisionAgentsAsync(context, agents, target, logger, cts.Token);
        await StartAgentsAsync(context, agents, logger, cts.Token);
        await ConnectPstnEmulatorAsync(context, logger, cts.Token);

        // Start Docker stats collection
        StartDockerStats(context, loggerFactory);

        // Execute scenario
        logger.LogInformation("Executing scenario: {Name} — {Description}", testScenario.Name, testScenario.Description);
        await testScenario.ExecuteAsync(context, cts.Token);

        // Validate
        logger.LogInformation("Validating results...");
        var report = await testScenario.ValidateAsync(context, cts.Token);

        var elapsed = context.TestEndTime - context.TestStartTime;
        var metrics = context.Metrics.GetSummary(elapsed);
        var dockerStats = context.DockerStats?.GetSummary();
        ReportGenerator.WriteConsoleReport(report, metrics, dockerStats);

        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            ReportGenerator.WriteJsonReport(report, metrics, dockerStats, outputPath);
            logger.LogInformation("JSON report written to {Path}", outputPath);
        }

        var passed = report.FailedChecks == 0;
        logger.LogInformation("Scenario {Name} completed. Result={Result}", testScenario.Name, passed ? "PASSED" : "FAILED");
        return passed ? 0 : 1;
    }
    catch (OperationCanceledException ex)
    {
        logger.LogError(ex, "Test timed out.");
        return 1;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Unhandled exception during scenario '{Scenario}'.", scenario);
        return 1;
    }
    finally
    {
        if (context.DockerStats is not null)
            try { await context.DockerStats.DisposeAsync(); } catch { /* best-effort */ }
        if (context.SdkRuntime is not null)
            try { await SdkHostSetup.StopAsync(context.SdkRuntime); } catch { /* best-effort */ }
        try { await context.AgentPool.DisposeAsync(); } catch { /* best-effort */ }
        if (context.AgentProvisioning is not null)
            try { await context.AgentProvisioning.DisposeAsync(); } catch { /* best-effort */ }
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

    // SDK infrastructure (Hosting + Sessions + Live)
    SdkHostSetup.ConfigureServices(builder.Services);

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

// ─── Helper methods ──────────────────────────────────────────────────────────

static async Task ProvisionAgentsAsync(
    TestContext context,
    int agents,
    string targetServer,
    MsLogger logger,
    CancellationToken ct)
{
    logger.LogInformation("Provisioning {N} PJSIP endpoints + queue members...", agents);
    try
    {
        var provisioning = new AgentProvisioningService(
            context.Options.PostgresConnectionString,
            context.LoggerFactory);
        context.AgentProvisioning = provisioning;

        await provisioning.ProvisionAsync(agents, targetServer, ct);

        // Reload Asterisk modules so it picks up the new endpoints/members.
        // Use SDK connection if available, otherwise provisioning still works
        // (Asterisk will see the realtime data on next query).
        if (context.SdkRuntime is not null)
        {
            await provisioning.ReloadAsteriskAsync(context.SdkRuntime.Connection, ct);

            // Asterisk does not emit QueueMemberAdded AMI events for realtime-loaded
            // members, so re-query live state to discover the newly provisioned agents.
            await context.SdkRuntime.Server.RequestInitialStateAsync(ct);
        }

        logger.LogInformation("Provisioned {N} agents in PostgreSQL", agents);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Agent provisioning failed — SIP registration may fail if endpoints don't exist");
    }
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
        // Attach metrics BEFORE StartAsync so agent state transitions are tracked
        // even when scenarios call StartAsync again (chaos/load scenarios do this)
        context.AgentPool.AttachMetrics(context.Metrics);
        await context.AgentPool.StartAsync(agents, ct);

        logger.LogInformation("Agent pool ready: {Total} total, {Idle} idle",
            context.AgentPool.TotalAgents, context.AgentPool.IdleAgents);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Agent registration failed (Docker stack may not be running) — continuing");
    }
}

static async Task StartSdkAsync(
    TestContext context,
    IServiceProvider services,
    MsLogger logger,
    CancellationToken ct)
{
    logger.LogInformation("Starting SDK infrastructure (Hosting + Sessions + Live)...");
    try
    {
        var sdkRuntime = await SdkHostSetup.StartAsync(services, context.Options, ct);
        context.SdkRuntime = sdkRuntime;

        var sessionCapture = services.GetRequiredService<SessionCaptureService>();
        sessionCapture.Attach(sdkRuntime.SessionManager);
        context.SessionCapture = sessionCapture;

        context.LiveStateValidator = services.GetRequiredService<LiveStateValidator>();

        logger.LogInformation("SDK infrastructure ready");
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "SDK infrastructure startup failed — SDK scenarios will not work");
    }
}

static async Task ConnectPstnEmulatorAsync(
    TestContext context,
    MsLogger logger,
    CancellationToken ct)
{
    logger.LogInformation("Connecting to PSTN emulator AMI at {Host}:{Port}...",
        context.Options.PstnEmulatorAmi.Host, context.Options.PstnEmulatorAmi.Port);
    try
    {
        await context.CallGenerator.ConnectAsync(ct);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "PSTN emulator connection failed — call generation will not work");
    }
}

static void StartDockerStats(TestContext context, ILoggerFactory loggerFactory)
{
    var collector = new DockerStatsCollector(loggerFactory, context.Metrics);
    context.DockerStats = collector;

    // Start async but don't await — fire and forget, will be stopped in finally
    _ = collector.StartAsync(DockerContainerNames.All);
}

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
