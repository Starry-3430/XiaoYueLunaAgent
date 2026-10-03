using Dapper;
using Luna.Models;
using Microsoft.Data.Sqlite;

namespace Luna.Services;

public class MessageRepository
{
    private readonly DatabaseInitializer _db;
    public MessageRepository(DatabaseInitializer db) => _db = db;

    public async Task<long> InsertAsync(Message m)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        var id = await conn.ExecuteScalarAsync<long>(
            @"INSERT INTO Messages 
              (SessionId, TurnId, Role, Content, ReasoningContent, ToolCallsJson, ToolCallId, ContentType, CreatedAtUtc, LogicalDate)
              VALUES 
              (@SessionId, @TurnId, @Role, @Content, @ReasoningContent, @ToolCallsJson, @ToolCallId, @ContentType, @CreatedAtUtc, @LogicalDate);
              SELECT last_insert_rowid();", m);
        return id;
    }

    public async Task DeleteAfterAsync(string sessionId, long afterId)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        await conn.ExecuteAsync(
            "DELETE FROM Messages WHERE SessionId = @SessionId AND Id > @Id",
            new { SessionId = sessionId, Id = afterId });
    }

    public async Task<List<Message>> GetBySessionAsync(string sessionId)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        var rows = await conn.QueryAsync<Message>(
            "SELECT * FROM Messages WHERE SessionId = @Id ORDER BY Id",
            new { Id = sessionId });
        return rows.ToList();
    }

    public async Task<List<Message>> GetByTurnAsync(string turnId)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        var rows = await conn.QueryAsync<Message>(
            "SELECT * FROM Messages WHERE TurnId = @Id ORDER BY Id",
            new { Id = turnId });
        return rows.ToList();
    }

    /// <summary>重写时把复用的用户消息重新归属到新轮次，避免摘要按 TurnId 取不到用户文本。</summary>
    public async Task ReassignTurnAsync(long messageId, string turnId, string logicalDate)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        await conn.ExecuteAsync(
            "UPDATE Messages SET TurnId = @TurnId, LogicalDate = @LogicalDate WHERE Id = @Id",
            new { Id = messageId, TurnId = turnId, LogicalDate = logicalDate });
    }
}