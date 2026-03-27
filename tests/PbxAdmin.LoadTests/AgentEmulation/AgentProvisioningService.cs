using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Responses;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace PbxAdmin.LoadTests.AgentEmulation;

/// <summary>
/// Dynamically creates and destroys PJSIP endpoints + queue members in PostgreSQL
/// for load testing. Endpoints are created on demand based on --agents N and cleaned
/// up after the test completes. This avoids pre-loading 300 static endpoints that
/// waste Asterisk resources.
/// </summary>
public sealed class AgentProvisioningService : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<AgentProvisioningService> _logger;

    private int _baseExtension;
    private int _agentCount;
    private bool _provisioned;

    public AgentProvisioningService(string connectionString, ILoggerFactory loggerFactory)
    {
        _connectionString = connectionString;
        _logger = loggerFactory.CreateLogger<AgentProvisioningService>();
    }

    // -------------------------------------------------------------------------
    // Provision
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates PJSIP endpoints, auths, AORs, and queue members in PostgreSQL
    /// for the specified number of agents. Idempotent — cleans residuals first.
    /// </summary>
    public async Task ProvisionAsync(int agentCount, string targetServer, CancellationToken ct)
    {
        if (_provisioned)
            throw new InvalidOperationException("Already provisioned. Call DeprovisionAsync first.");

        _baseExtension = targetServer.Equals("file", StringComparison.OrdinalIgnoreCase) ? 4100 : 2100;
        _agentCount = agentCount;

        _logger.LogInformation(
            "Provisioning {Count} PJSIP endpoints starting at {Base}",
            agentCount, _baseExtension);

        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            // 1. Clean residuals (idempotent)
            await conn.ExecuteAsync(new CommandDefinition(
                """
                DELETE FROM queue_members WHERE queue_name = 'loadtest';
                DELETE FROM queue_members WHERE queue_name = 'sales' AND interface LIKE 'PJSIP/21%';
                """,
                cancellationToken: ct));

            // Build batch parameters
            var agents = Enumerable.Range(0, agentCount).Select(i =>
            {
                string id = (_baseExtension + i).ToString();
                return new
                {
                    Id = id,
                    Password = $"loadtest{id}",
                    CallerId = $"\"Load Agent {i + 1}\" <{id}>",
                    Interface = $"PJSIP/{id}",
                    MemberName = $"Load Agent {i + 1}",
                    Index = i + 1
                };
            }).ToList();

            // 2. Clean old PJSIP data for this range
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM ps_endpoints WHERE id = @Id",
                agents, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM ps_auths WHERE id = @Id",
                agents, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM ps_aors WHERE id = @Id",
                agents, cancellationToken: ct));

            // 3. Insert endpoints
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO ps_endpoints (id, transport, aors, auth, context, disallow, allow, direct_media, callerid)
                VALUES (@Id, 'transport-udp', @Id, @Id, 'default', 'all', 'ulaw,alaw', 'no', @CallerId)
                ON CONFLICT (id) DO NOTHING
                """,
                agents, cancellationToken: ct));

            // 4. Insert auths
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO ps_auths (id, auth_type, username, password)
                VALUES (@Id, 'userpass', @Id, @Password)
                ON CONFLICT (id) DO NOTHING
                """,
                agents, cancellationToken: ct));

            // 5. Insert AORs
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO ps_aors (id, max_contacts, remove_existing, qualify_frequency)
                VALUES (@Id, 1, 'yes', 0)
                ON CONFLICT (id) DO NOTHING
                """,
                agents, cancellationToken: ct));

            // 6. Insert queue members (all penalty 0 — no artificial tiers)
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO queue_members (queue_name, interface, membername, penalty)
                VALUES ('loadtest', @Interface, @MemberName, 0)
                ON CONFLICT (queue_name, interface) DO NOTHING
                """,
                agents, cancellationToken: ct));

            _provisioned = true;
            _logger.LogInformation(
                "Provisioned {Count} agents: extensions {First}–{Last}",
                agentCount, _baseExtension, _baseExtension + agentCount - 1);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to provision agents — partial data may exist, DeprovisionAsync will clean up");
            throw;
        }
    }

    // -------------------------------------------------------------------------
    // Reload Asterisk
    // -------------------------------------------------------------------------

    /// <summary>
    /// Reloads PJSIP and Queue modules via AMI so Asterisk picks up the new endpoints.
    /// </summary>
    public async Task ReloadAsteriskAsync(IAmiConnection connection, CancellationToken ct)
    {
        _logger.LogInformation("Reloading PJSIP and Queue modules via AMI");

        await connection.SendActionAsync<CommandResponse>(
            new CommandAction { Command = "module reload res_pjsip.so" }, ct);
        await Task.Delay(TimeSpan.FromSeconds(2), ct);

        await connection.SendActionAsync<CommandResponse>(
            new CommandAction { Command = "module reload app_queue.so" }, ct);
        await Task.Delay(TimeSpan.FromSeconds(1), ct);

        _logger.LogInformation("PJSIP and Queue modules reloaded");
    }

    // -------------------------------------------------------------------------
    // Deprovision
    // -------------------------------------------------------------------------

    /// <summary>
    /// Removes all PJSIP endpoints, auths, AORs, and queue members created by ProvisionAsync.
    /// Best-effort — never throws.
    /// </summary>
    public async Task DeprovisionAsync(CancellationToken ct)
    {
        if (!_provisioned && _agentCount == 0)
            return;

        try
        {
            _logger.LogInformation(
                "Deprovisioning {Count} agents starting at {Base}",
                _agentCount, _baseExtension);

            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            var agents = Enumerable.Range(0, _agentCount)
                .Select(i => new { Id = (_baseExtension + i).ToString() })
                .ToList();

            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM queue_members WHERE queue_name = 'loadtest'",
                cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM ps_endpoints WHERE id = @Id",
                agents, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM ps_auths WHERE id = @Id",
                agents, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM ps_aors WHERE id = @Id",
                agents, cancellationToken: ct));

            _provisioned = false;
            _agentCount = 0;
            _logger.LogInformation("Deprovisioning complete");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Deprovision failed (best-effort cleanup)");
        }
    }

    // -------------------------------------------------------------------------
    // IAsyncDisposable
    // -------------------------------------------------------------------------

    public async ValueTask DisposeAsync()
    {
        try
        {
            await DeprovisionAsync(CancellationToken.None);
        }
        catch
        {
            // Best-effort — DeprovisionAsync already swallows exceptions,
            // but guard against any unexpected edge case.
        }
    }
}
