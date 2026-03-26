using Dapper;
using Npgsql;

namespace PbxAdmin.LoadTests.Validation.Layer2.Repositories;

public sealed class DbQueueLogRepository : IQueueLogRepository
{
    private readonly string _connectionString;

    private const string QueueLogColumns = """
        id        AS "Id",
        time      AS "Time",
        callid    AS "CallId",
        queuename AS "QueueName",
        agent     AS "Agent",
        event     AS "Event",
        data1     AS "Data1",
        data2     AS "Data2",
        data3     AS "Data3",
        data4     AS "Data4",
        data5     AS "Data5"
        """;

    public DbQueueLogRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<List<QueueLogRecord>> GetByCallIdAsync(string callId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var sql = $"SELECT {QueueLogColumns} FROM queue_log WHERE callid = @CallId ORDER BY time";
        return (await conn.QueryAsync<QueueLogRecord>(
            new CommandDefinition(sql, new { CallId = callId }, cancellationToken: ct))).AsList();
    }

    public async Task<List<QueueLogRecord>> GetByQueueAndTimeRangeAsync(
        string queueName, DateTime from, DateTime to, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var sql = $"SELECT {QueueLogColumns} FROM queue_log WHERE queuename = @QueueName AND time >= @From AND time < @To ORDER BY time";
        return (await conn.QueryAsync<QueueLogRecord>(
            new CommandDefinition(sql, new { QueueName = queueName, From = from, To = to }, cancellationToken: ct))).AsList();
    }

    public async Task<List<QueueLogRecord>> GetByTimeRangeAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var sql = $"SELECT {QueueLogColumns} FROM queue_log WHERE time >= @From AND time < @To ORDER BY time";
        return (await conn.QueryAsync<QueueLogRecord>(
            new CommandDefinition(sql, new { From = from, To = to }, cancellationToken: ct))).AsList();
    }

    public async Task<int> GetCountByEventAsync(string eventName, DateTime from, DateTime to, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var sql = "SELECT COUNT(*)::int FROM queue_log WHERE event = @EventName AND time >= @From AND time < @To";
        return await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(sql, new { EventName = eventName, From = from, To = to }, cancellationToken: ct));
    }
}
