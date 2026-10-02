using Dapper;
using Luna.Models;
using Microsoft.Data.Sqlite;

namespace Luna.Services;

public class MessageRepository
{
    private readonly DatabaseInitializer _db;
    public MessageRepository(DatabaseInitializer db) => _db = db;

    public async Task InsertAsync(Message m)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        await conn.ExecuteAsync(
            @"INSERT INTO Messages 
              (SessionId, TurnId, Role, Content, ReasoningContent, ToolCallsJson, ContentType, CreatedAtUtc, LogicalDate)
              VALUES 
              (@SessionId, @TurnId, @Role, @Content, @ReasoningContent, @ToolCallsJson, @ContentType, @CreatedAtUtc, @LogicalDate)", m);
    }

    public async Task<List<Message>> GetBySessionAsync(string sessionId)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        var rows = await conn.QueryAsync<Message>(
            "SELECT * FROM Messages WHERE SessionId = @Id ORDER BY Id",
            new { Id = sessionId });
        return rows.ToList();
    }
}