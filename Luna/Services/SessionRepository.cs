using Dapper;
using Microsoft.Data.Sqlite;
using Luna.Models;

namespace Luna.Services;

public class SessionRepository
{
    private readonly DatabaseInitializer _db;
    public SessionRepository(DatabaseInitializer db) => _db = db;

    public async Task<List<Session>> GetAllAsync()
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        var rows = await conn.QueryAsync<Session>(
            @"SELECT s.*,
               (SELECT Content FROM Messages WHERE SessionId = s.Id AND Role = 'user' ORDER BY Id LIMIT 1) AS Preview
               FROM Sessions s WHERE IsArchived = 0 ORDER BY UpdatedAtUtc DESC");
        return rows.ToList();
    }

    public async Task<Session?> GetByIdAsync(string id)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        return await conn.QueryFirstOrDefaultAsync<Session>(
            "SELECT * FROM Sessions WHERE Id = @Id", new { Id = id });
    }

    public async Task InsertAsync(Session s)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        await conn.ExecuteAsync(
            @"INSERT INTO Sessions (Id, Title, CreatedAtUtc, UpdatedAtUtc, IsArchived)
              VALUES (@Id, @Title, @CreatedAtUtc, @UpdatedAtUtc, @IsArchived)", s);
    }

    public async Task UpdateTitleAsync(string id, string title)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        await conn.ExecuteAsync(
            "UPDATE Sessions SET Title = @Title WHERE Id = @Id",
            new { Id = id, Title = title });
    }

    public async Task TouchAsync(string id)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        await conn.ExecuteAsync(
            "UPDATE Sessions SET UpdatedAtUtc = @Now WHERE Id = @Id",
            new { Id = id, Now = DateTime.UtcNow.ToString("O") });
    }

    /// <summary>
    /// 删除会话及其全部关联数据：消息、附件引用、轮次摘要。
    /// 已生成的日记（DiaryEntries/DiaryDays）不在此范围，保持不受影响。
    /// </summary>
    public async Task DeleteAsync(string id)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        conn.Open();
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync("DELETE FROM Messages WHERE SessionId = @Id", new { Id = id }, tx);
        await conn.ExecuteAsync("DELETE FROM Attachments WHERE SessionId = @Id", new { Id = id }, tx);
        await conn.ExecuteAsync("DELETE FROM TurnSummaries WHERE SessionId = @Id", new { Id = id }, tx);
        await conn.ExecuteAsync("DELETE FROM Sessions WHERE Id = @Id", new { Id = id }, tx);
        tx.Commit();
    }
}