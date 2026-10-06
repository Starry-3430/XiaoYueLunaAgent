using Dapper;
using Luna.Models;
using Microsoft.Data.Sqlite;

namespace Luna.Services.Data;

/// <summary>附件的数据库访问（Attachments 表）。</summary>
public class AttachmentRepository
{
    private readonly DatabaseInitializer _db;
    public AttachmentRepository(DatabaseInitializer db) => _db = db;

    /// <summary>插入一条附件记录。</summary>
    public async Task AddAsync(Attachment a)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        await conn.ExecuteAsync(
            @"INSERT INTO Attachments
              (Id, SessionId, TurnId, FileName, FileExtension, FileSize, Sha256, StoredPath, ConvertedMarkdown, CreatedAtUtc)
              VALUES
              (@Id, @SessionId, @TurnId, @FileName, @FileExtension, @FileSize, @Sha256, @StoredPath, @ConvertedMarkdown, @CreatedAtUtc)", a);
    }

    /// <summary>按主键查询。</summary>
    public async Task<Attachment?> GetAsync(string id)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        return await conn.QueryFirstOrDefaultAsync<Attachment>(
            "SELECT * FROM Attachments WHERE Id = @Id", new { Id = id });
    }

    /// <summary>按内容哈希查询（内容寻址去重用）。</summary>
    public async Task<Attachment?> GetBySha256Async(string sha256)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        return await conn.QueryFirstOrDefaultAsync<Attachment>(
            "SELECT * FROM Attachments WHERE Sha256 = @Sha256 ORDER BY CreatedAtUtc LIMIT 1",
            new { Sha256 = sha256 });
    }

    /// <summary>查询某个会话下的全部附件。</summary>
    public async Task<List<Attachment>> GetBySessionAsync(string sessionId)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        var rows = await conn.QueryAsync<Attachment>(
            "SELECT * FROM Attachments WHERE SessionId = @SessionId ORDER BY CreatedAtUtc",
            new { SessionId = sessionId });
        return rows.ToList();
    }

    /// <summary>查询某个轮次下的全部附件。</summary>
    public async Task<List<Attachment>> GetByTurnAsync(string turnId)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        var rows = await conn.QueryAsync<Attachment>(
            "SELECT * FROM Attachments WHERE TurnId = @TurnId ORDER BY CreatedAtUtc",
            new { TurnId = turnId });
        return rows.ToList();
    }

    /// <summary>更新附件转换后的 Markdown 内容。</summary>
    public async Task<int> UpdateConvertedMarkdownAsync(string id, string? markdown)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        return await conn.ExecuteAsync(
            "UPDATE Attachments SET ConvertedMarkdown = @Markdown WHERE Id = @Id",
            new { Id = id, Markdown = markdown });
    }

    /// <summary>删除一条附件记录。</summary>
    public async Task<int> DeleteAsync(string id)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        return await conn.ExecuteAsync("DELETE FROM Attachments WHERE Id = @Id", new { Id = id });
    }
}
