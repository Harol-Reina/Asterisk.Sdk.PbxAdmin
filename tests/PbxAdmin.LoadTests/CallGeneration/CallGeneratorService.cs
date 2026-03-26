using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PbxAdmin.LoadTests.Configuration;
using SdkAmiConnection = Asterisk.Sdk.Ami.Connection.AmiConnection;
using SdkAmiConnectionOptions = Asterisk.Sdk.Ami.Connection.AmiConnectionOptions;

namespace PbxAdmin.LoadTests.CallGeneration;

/// <summary>
/// Generates inbound calls to the PBX by issuing AMI Originate commands on the PSTN emulator.
/// </summary>
public sealed class CallGeneratorService : IAsyncDisposable
{
    private const string RealtimeContext = "pstn-to-realtime-dynamic";
    private const string FileContext = "pstn-to-file-dynamic";

    private readonly LoadTestOptions _options;
    private readonly ColombianNumberGenerator _numberGenerator;
    private readonly ILogger<CallGeneratorService> _logger;
    private readonly Func<SdkAmiConnection> _connectionFactory;

    private SdkAmiConnection? _connection;
    private int _callCounter;

    public CallGeneratorService(
        IOptions<LoadTestOptions> options,
        ColombianNumberGenerator numberGenerator,
        ILogger<CallGeneratorService> logger,
        Func<SdkAmiConnection>? connectionFactory = null)
    {
        _options = options.Value;
        _numberGenerator = numberGenerator;
        _logger = logger;
        _connectionFactory = connectionFactory ?? CreateDefaultConnection;
    }

    /// <summary>
    /// Establishes the AMI connection to the PSTN emulator.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _connection = _connectionFactory();
        await _connection.ConnectAsync(cancellationToken);
        _logger.LogInformation(
            "Connected to PSTN emulator at {Host}:{Port}",
            _options.PstnEmulatorAmi.Host,
            _options.PstnEmulatorAmi.Port);
    }

    /// <summary>
    /// Originates a single inbound call to the specified destination.
    /// If <paramref name="caller"/> is null, a random Colombian caller profile is generated.
    /// </summary>
    public async Task<CallGenerationResult> GenerateCallAsync(
        string destination,
        CallerProfile? caller = null,
        CancellationToken cancellationToken = default)
    {
        if (_connection is null)
            throw new InvalidOperationException("Not connected. Call ConnectAsync first.");

        caller ??= _numberGenerator.Generate();
        int counter = Interlocked.Increment(ref _callCounter);
        string callId = $"loadtest-{counter:D6}-{DateTime.UtcNow:yyyyMMddHHmmss}";

        var action = BuildOriginateAction(destination, caller, _options.TargetServer, callId);

        _logger.LogDebug(
            "Originating call {CallId}: {CallerNum} -> {Destination} via {Channel}",
            callId, caller.Number, destination, action.Channel);

        try
        {
            var response = await _connection.SendActionAsync(action, cancellationToken);
            bool accepted = response is not null;

            _logger.LogInformation(
                "Call {CallId} {Status}: {CallerNum} ({CallerName}) -> {Destination}",
                callId,
                accepted ? "accepted" : "rejected",
                caller.Number,
                caller.DisplayName,
                destination);

            return new CallGenerationResult
            {
                CallId = callId,
                Caller = caller,
                Destination = destination,
                Timestamp = DateTime.UtcNow,
                Accepted = accepted,
                ErrorMessage = accepted ? null : "AMI returned null response"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to originate call {CallId}", callId);
            return new CallGenerationResult
            {
                CallId = callId,
                Caller = caller,
                Destination = destination,
                Timestamp = DateTime.UtcNow,
                Accepted = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Originates <paramref name="count"/> calls rapidly to the specified destination.
    /// </summary>
    public async Task<IReadOnlyList<CallGenerationResult>> GenerateBurstAsync(
        int count,
        string destination,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating burst of {Count} calls to {Destination}", count, destination);

        var tasks = new Task<CallGenerationResult>[count];
        for (int i = 0; i < count; i++)
            tasks[i] = GenerateCallAsync(destination, cancellationToken: cancellationToken);

        var results = await Task.WhenAll(tasks);

        int accepted = results.Count(r => r.Accepted);
        _logger.LogInformation(
            "Burst complete: {Accepted}/{Total} calls accepted to {Destination}",
            accepted, count, destination);

        return results;
    }

    /// <summary>
    /// Closes the AMI connection.
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
            _logger.LogInformation("Disconnected from PSTN emulator.");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }

    /// <summary>
    /// Builds the AMI OriginateAction for the given parameters.
    /// Extracted as internal static for unit testing.
    /// </summary>
    internal static OriginateAction BuildOriginateAction(
        string destination,
        CallerProfile caller,
        string targetServer,
        string callId)
    {
        string context = string.Equals(targetServer, "file", StringComparison.OrdinalIgnoreCase)
            ? FileContext
            : RealtimeContext;

        var action = new OriginateAction
        {
            Channel = $"Local/{destination}@{context}",
            Application = "Wait",
            Data = "1",
            IsAsync = true,
            ActionId = callId
        };

        action.SetVariable("CALLER_NUM", caller.Number);
        action.SetVariable("CALLER_NAME", caller.DisplayName);

        return action;
    }

    private SdkAmiConnection CreateDefaultConnection()
    {
        var sdkOptions = Options.Create(new SdkAmiConnectionOptions
        {
            Hostname = _options.PstnEmulatorAmi.Host,
            Port = _options.PstnEmulatorAmi.Port,
            Username = _options.PstnEmulatorAmi.Username,
            Password = _options.PstnEmulatorAmi.Password,
            DefaultResponseTimeout = TimeSpan.FromSeconds(15),
            AutoReconnect = false
        });

        return new SdkAmiConnection(
            sdkOptions,
            new PipelineSocketConnectionFactory(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SdkAmiConnection>.Instance);
    }
}
