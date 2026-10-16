using Dapper;
using Microsoft.Data.Sqlite;

namespace Luna.Services;

public class SearchRepository
{
    private readonly DatabaseInitializer _db;
    public SearchRepository(DatabaseInitializer db) => _db = db;

    public async Task<List<SearchHit>> SearchAsync(string keyword, int limit)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        var rows = await conn.QueryAsync<SearchHit>(
            @"SELECT m.Id AS MessageId, m.SessionId AS SessionId, m.Role AS Role,
                     m.Content AS Content, m.CreatedAtUtc AS CreatedAtUtc,
                     COALESCE(s.Title, '') AS SessionTitle
              FROM Messages m
              JOIN Sessions s ON s.Id = m.SessionId
              WHERE s.IsArchived = 0
                AND m.Role IN ('user', 'assistant')
                AND m.Content LIKE '%' || @Keyword || '%' ESCAPE '\'
              ORDER BY m.CreatedAtUtc DESC
              LIMIT @Limit",
            new { Keyword = EscapeLike(keyword), Limit = limit });
        return rows.ToList();
    }

    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\")
        .Replace("%", "\\%")
        .Replace("_", "\\_");

    public sealed class SearchHit
    {
        public long MessageId { get; set; }
        public string SessionId { get; set; } = string.Empty;
        public string SessionTitle { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
    }
}
