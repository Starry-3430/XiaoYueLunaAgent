using Dapper;
using Luna.Models;
using Microsoft.Data.Sqlite;

namespace Luna.Services.Data;

/// <summary>长期事实的数据库访问（Facts 表）。</summary>
public class FactRepository
{
    private readonly DatabaseInitializer _db;
    public FactRepository(DatabaseInitializer db) => _db = db;

    /// <summary>全部事实，按最近更新倒序。</summary>
    public async Task<List<Fact>> GetAllAsync()
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        var rows = await conn.QueryAsync<Fact>(
            "SELECT * FROM Facts ORDER BY UpdatedAtUtc DESC");
        return rows.ToList();
    }

    /// <summary>按键查询单条事实。</summary>
    public async Task<Fact?> GetByKeyAsync(string key)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        return await conn.QueryFirstOrDefaultAsync<Fact>(
            "SELECT * FROM Facts WHERE Key = @Key", new { Key = key });
    }

    /// <summary>
    /// 按键插入或更新（原子 Upsert）。已存在则覆盖 Value / SourceTurnId 并刷新 UpdatedAtUtc。
    /// 同义事实由调用方归一到同一 Key，从而实现去重。
    /// </summary>
    public async Task UpsertAsync(string key, string value, string? sourceTurnId)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        await conn.ExecuteAsync(
            @"INSERT INTO Facts (Key, Value, Confidence, SourceTurnId, CreatedAtUtc, UpdatedAtUtc)
              VALUES (@Key, @Value, 1.0, @SourceTurnId, @Now, @Now)
              ON CONFLICT(Key) DO UPDATE SET
                  Value        = excluded.Value,
                  SourceTurnId = excluded.SourceTurnId,
                  UpdatedAtUtc = excluded.UpdatedAtUtc",
            new { Key = key, Value = value, SourceTurnId = sourceTurnId, Now = DateTime.UtcNow.ToString("O") });
    }

    /// <summary>按键删除；返回是否确实删除了至少一行。</summary>
    public async Task<bool> DeleteAsync(string key)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        var affected = await conn.ExecuteAsync("DELETE FROM Facts WHERE Key = @Key", new { Key = key });
        return affected > 0;
    }

    /// <summary>按关键词模糊检索 Key 或 Value，按最近更新倒序。</summary>
    public async Task<List<Fact>> SearchAsync(string keyword)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        var rows = await conn.QueryAsync<Fact>(
            @"SELECT * FROM Facts
              WHERE Key LIKE '%' || @Keyword || '%' OR Value LIKE '%' || @Keyword || '%'
              ORDER BY UpdatedAtUtc DESC",
            new { Keyword = keyword });
        return rows.ToList();
    }
}
