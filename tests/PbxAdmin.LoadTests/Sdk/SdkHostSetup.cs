using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Connection;
using Asterisk.Sdk.Hosting;
using Asterisk.Sdk.Live.Server;
using Asterisk.Sdk.Sessions.Manager;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Configuration;
using SdkAmiConnectionOptions = Asterisk.Sdk.Ami.Connection.AmiConnectionOptions;

namespace PbxAdmin.LoadTests.Sdk;

/// <summary>
/// Registers SDK services (Hosting + Sessions) in the DI container and manages
/// the startup/shutdown lifecycle for the target PBX connection.
/// </summary>
internal static class SdkHostSetup
{
    /// <summary>
    /// Registers IAmiConnectionFactory, ICallSessionManager, SessionCaptureService,
    /// and LiveStateValidator in the service collection.
    /// </summary>
    public static void ConfigureServices(IServiceCollection services)
    {
        services.AddAsteriskMultiServer();
        services.AddAsteriskSessionsMultiServer(opts =>
        {
            opts.InboundContextPatterns = ["from-trunk", "from-pstn"];
            opts.CompletedRetention = TimeSpan.FromMinutes(10);
            opts.MaxCompletedSessions = 5000;
        });
        services.AddSingleton<SessionCaptureService>();
        services.AddSingleton<LiveStateValidator>();
    }

    /// <summary>
    /// Connects to the target PBX via IAmiConnectionFactory, creates AsteriskServer,
    /// starts live-state tracking, and attaches the call session manager.
    /// </summary>
    public static async Task<SdkRuntime> StartAsync(
        IServiceProvider services,
        LoadTestOptions options,
        CancellationToken ct)
    {
        var factory = services.GetRequiredService<IAmiConnectionFactory>();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger(typeof(SdkHostSetup));

        var connectionOptions = new SdkAmiConnectionOptions
        {
            Hostname = options.TargetPbxAmi.Host,
            Port = options.TargetPbxAmi.Port,
            Username = options.TargetPbxAmi.Username,
            Password = options.TargetPbxAmi.Password,
            AutoReconnect = true
        };

        logger.LogInformation("SDK: Connecting to target PBX at {Host}:{Port}...",
            options.TargetPbxAmi.Host, options.TargetPbxAmi.Port);

        var connection = await factory.CreateAndConnectAsync(connectionOptions, ct);

        var server = new AsteriskServer(connection, loggerFactory.CreateLogger<AsteriskServer>());
        server.ConnectionLost += ex =>
            logger.LogWarning(ex, "SDK: Target PBX connection lost");
        await server.StartAsync(ct);

        var sessionManager = services.GetRequiredService<ICallSessionManager>();
        sessionManager.AttachToServer(server, "target-pbx");

        logger.LogInformation("SDK: Connected, AsteriskServer started, session manager attached");

        return new SdkRuntime(connection, server, sessionManager);
    }

    /// <summary>Best-effort shutdown of SDK infrastructure.</summary>
    public static async Task StopAsync(SdkRuntime runtime)
    {
        await runtime.DisposeAsync();
    }
}
