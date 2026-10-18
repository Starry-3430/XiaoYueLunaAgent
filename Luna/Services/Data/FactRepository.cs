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

    /// <summary>
    /// 按用户原始输入检索相关事实。无法提炼关键词或没有匹配时，
    /// 回退到最近更新的 <paramref name="limit"/> 条，避免完全失去上下文。
    /// </summary>
    public async Task<List<Fact>> SearchRelevantAsync(string query, int limit = 5)
    {
        if (limit <= 0)
            return [];

        if (string.IsNullOrWhiteSpace(query))
            return await GetRecentAsync(limit);

        var keywords = FactQueryKeywords.Extract(query);
        if (keywords.Count == 0)
            return await GetRecentAsync(limit);

        try
        {
            var facts = await SearchRelevantAsync(keywords, limit);
            return facts.Count > 0 ? facts : await GetRecentAsync(limit);
        }
        catch (SqliteException)
        {
            // FTS5 不可用或查询异常：退化为按时间取最近事实。
            return await GetRecentAsync(limit);
        }
    }

    /// <summary>最近更新的若干条事实（检索兜底）。</summary>
    public async Task<List<Fact>> GetRecentAsync(int limit)
    {
        if (limit <= 0)
            return [];

        using var conn = new SqliteConnection(_db.ConnectionString);
        var rows = await conn.QueryAsync<Fact>(
            "SELECT * FROM Facts ORDER BY UpdatedAtUtc DESC LIMIT @Limit",
            new { Limit = limit });
        return rows.ToList();
    }

    /// <summary>
    /// 按关键词检索最相关的事实：优先走 FTS5（trigram，适配 3 字及以上中文/英文），
    /// 2 字及以下关键词用 LIKE 兜底。结果去重后最多返回 <paramref name="limit"/> 条。
    /// </summary>
    public async Task<List<Fact>> SearchRelevantAsync(IReadOnlyList<string> keywords, int limit)
    {
        if (keywords.Count == 0 || limit <= 0)
            return [];

        var terms = keywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (terms.Count == 0)
            return [];

        var results = new List<Fact>();
        var seen = new HashSet<long>();
        using var conn = new SqliteConnection(_db.ConnectionString);

        // 3 字符及以上：FTS5 trigram 子串匹配，bm25 排序（Key 权重更高）。
        var ftsTerms = terms.Where(k => k.Length >= 3).Take(8).ToList();
        if (ftsTerms.Count > 0)
        {
            try
            {
                var query = string.Join(" OR ", ftsTerms.Select(QuoteFtsTerm));
                var rows = await conn.QueryAsync<Fact>(
                    @"SELECT f.*
                      FROM FactsFts
                      JOIN Facts f ON f.Id = FactsFts.rowid
                      WHERE FactsFts MATCH @Query
                      ORDER BY bm25(FactsFts, 4.0, 1.0)
                      LIMIT @Limit",
                    new { Query = query, Limit = limit * 3 });

                foreach (var row in rows)
                {
                    if (seen.Add(row.Id))
                        results.Add(row);
                }
            }
            catch (SqliteException)
            {
                // FTS5 查询失败时继续用下面的 LIKE 兜底。
            }
        }

        // 2 字及以下：trigram 无法命中，用 LIKE 兜底（事实表很小，代价可接受）。
        var shortTerms = terms.Where(k => k.Length < 3).Take(12).ToList();
        if (shortTerms.Count > 0)
        {
            var conditions = new List<string>();
            var parameters = new DynamicParameters();

            for (var i = 0; i < shortTerms.Count; i++)
            {
                conditions.Add($"(Key LIKE @K{i} ESCAPE '\\' OR Value LIKE @K{i} ESCAPE '\\')");
                parameters.Add($"K{i}", $"%{EscapeLike(shortTerms[i])}%");
            }

            parameters.Add("Limit", limit * 3);
            var rows = await conn.QueryAsync<Fact>(
                $@"SELECT * FROM Facts
                   WHERE {string.Join(" OR ", conditions)}
                   ORDER BY UpdatedAtUtc DESC
                   LIMIT @Limit",
                parameters);

            // LIKE 结果按命中关键词数重排（Key 权重高于 Value），避免旧的无关事实挤掉真正相关的事实。
            var ranked = rows
                .Select(row => (Fact: row, Score: shortTerms.Sum(term => MatchScore(row, term))))
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Fact.UpdatedAtUtc)
                .Select(x => x.Fact);

            foreach (var row in ranked)
            {
                if (seen.Add(row.Id))
                    results.Add(row);
            }
        }

        return results.Take(limit).ToList();
    }

    /// <summary>关键词在 Key / Value 中的命中权重。</summary>
    private static int MatchScore(Fact fact, string term)
    {
        var score = 0;
        if (fact.Key.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 3;
        if (fact.Value.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 1;
        return score;
    }

    /// <summary>把关键词包装成 FTS5 短语查询；内部引号加倍以避免语法错误。</summary>
    private static string QuoteFtsTerm(string term)
        => "\"" + term.Replace("\"", "\"\"") + "\"";

    /// <summary>转义 LIKE 中的通配符。</summary>
    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
