using Dapper;
using Npgsql;

namespace PbxAdmin.LoadTests.Validation.Layer2.Repositories;

public sealed class DbCdrReadRepository : ICdrReadRepository
{
    private readonly string _connectionString;

    private const string CdrColumns = """
        id          AS "Id",
        calldate    AS "CallDate",
        clid        AS "Clid",
        src         AS "Src",
        dst         AS "Dst",
        dcontext    AS "DContext",
        channel     AS "Channel",
        dstchannel  AS "DstChannel",
        lastapp     AS "LastApp",
        lastdata    AS "LastData",
        duration    AS "Duration",
        billsec     AS "BillSec",
        disposition AS "Disposition",
        uniqueid    AS "UniqueId",
        linkedid    AS "LinkedId"
        """;

    public DbCdrReadRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<List<CdrRecord>> GetByTimeRangeAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var sql = $"SELECT {CdrColumns} FROM cdr WHERE calldate >= @From AND calldate < @To ORDER BY calldate";
        return (await conn.QueryAsync<CdrRecord>(
            new CommandDefinition(sql, new { From = from, To = to }, cancellationToken: ct))).AsList();
    }

    public async Task<CdrRecord?> GetByUniqueIdAsync(string uniqueId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var sql = $"SELECT {CdrColumns} FROM cdr WHERE uniqueid = @UniqueId LIMIT 1";
        return await conn.QueryFirstOrDefaultAsync<CdrRecord>(
            new CommandDefinition(sql, new { UniqueId = uniqueId }, cancellationToken: ct));
    }

    public async Task<List<CdrRecord>> GetBySrcAsync(string src, DateTime after, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var sql = $"SELECT {CdrColumns} FROM cdr WHERE src = @Src AND calldate >= @After ORDER BY calldate";
        return (await conn.QueryAsync<CdrRecord>(
            new CommandDefinition(sql, new { Src = src, After = after }, cancellationToken: ct))).AsList();
    }

    public async Task<List<CdrRecord>> GetByLinkedIdAsync(string linkedId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var sql = $"SELECT {CdrColumns} FROM cdr WHERE linkedid = @LinkedId ORDER BY calldate";
        return (await conn.QueryAsync<CdrRecord>(
            new CommandDefinition(sql, new { LinkedId = linkedId }, cancellationToken: ct))).AsList();
    }

    public async Task<int> GetCountByTimeRangeAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var sql = "SELECT COUNT(*)::int FROM cdr WHERE calldate >= @From AND calldate < @To";
        return await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(sql, new { From = from, To = to }, cancellationToken: ct));
    }
}
