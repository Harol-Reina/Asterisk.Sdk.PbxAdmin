using Dapper;
using Npgsql;

namespace PbxAdmin.LoadTests.Validation.Layer2.Repositories;

public sealed class DbCelReadRepository : ICelReadRepository
{
    private readonly string _connectionString;

    private const string CelColumns = """
        id        AS "Id",
        eventtype AS "EventType",
        eventtime AS "EventTime",
        cid_name  AS "CidName",
        cid_num   AS "CidNum",
        exten     AS "Exten",
        context   AS "Context",
        channame  AS "ChanName",
        appname   AS "AppName",
        appdata   AS "AppData",
        uniqueid  AS "UniqueId",
        linkedid  AS "LinkedId",
        peer      AS "Peer",
        extra     AS "Extra"
        """;

    public DbCelReadRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<List<CelRecord>> GetByLinkedIdAsync(string linkedId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var sql = $"SELECT {CelColumns} FROM cel WHERE linkedid = @LinkedId ORDER BY eventtime";
        return (await conn.QueryAsync<CelRecord>(
            new CommandDefinition(sql, new { LinkedId = linkedId }, cancellationToken: ct))).AsList();
    }

    public async Task<List<CelRecord>> GetByUniqueIdAsync(string uniqueId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var sql = $"SELECT {CelColumns} FROM cel WHERE uniqueid = @UniqueId ORDER BY eventtime";
        return (await conn.QueryAsync<CelRecord>(
            new CommandDefinition(sql, new { UniqueId = uniqueId }, cancellationToken: ct))).AsList();
    }

    public async Task<List<CelRecord>> GetByTimeRangeAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var sql = $"SELECT {CelColumns} FROM cel WHERE eventtime >= @From AND eventtime < @To ORDER BY eventtime";
        return (await conn.QueryAsync<CelRecord>(
            new CommandDefinition(sql, new { From = from, To = to }, cancellationToken: ct))).AsList();
    }

    public async Task<int> GetCountByTimeRangeAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var sql = "SELECT COUNT(*)::int FROM cel WHERE eventtime >= @From AND eventtime < @To";
        return await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(sql, new { From = from, To = to }, cancellationToken: ct));
    }
}
