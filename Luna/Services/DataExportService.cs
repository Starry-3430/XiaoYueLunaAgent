using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Luna.Services;

/// <summary>
/// 将 Luna 的用户数据导出为可读的 ZIP 备份：日记、长期事实、会话与消息、待办、附件元数据及附件文件。
/// 不包含 settings.json（其中含加密后的密钥，跨设备不可直接复用）。
/// </summary>
public sealed class DataExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly DatabaseInitializer _db;
    private readonly ILogger<DataExportService> _logger;

    public DataExportService(DatabaseInitializer db, ILogger<DataExportService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>导出全部用户数据到指定的 ZIP 文件，已存在时覆盖。</summary>
    public async Task ExportAllAsync(string targetZipPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetZipPath))
            throw new ArgumentException("备份路径为空。", nameof(targetZipPath));

        var targetPath = Path.GetFullPath(targetZipPath);
        var targetDirectory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(targetDirectory))
            Directory.CreateDirectory(targetDirectory);

        var tempDir = Path.Combine(Path.GetTempPath(), "LunaExport_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var manifest = await WriteDatabaseExportAsync(tempDir, ct);
            ct.ThrowIfCancellationRequested();

            await File.WriteAllTextAsync(
                Path.Combine(tempDir, "manifest.json"),
                JsonSerializer.Serialize(manifest, JsonOptions),
                Utf8NoBom, ct);

            ct.ThrowIfCancellationRequested();
            if (File.Exists(targetPath)) File.Delete(targetPath);
            ZipFile.CreateFromDirectory(tempDir, targetPath, CompressionLevel.Optimal, includeBaseDirectory: false);

            _logger.LogInformation(
                "数据备份完成：{Path}（日记 {Diaries}，事实 {Facts}，会话 {Sessions}，消息 {Messages}，待办 {Tasks}，附件 {Attachments}）",
                targetPath, manifest.Diaries, manifest.Facts, manifest.Sessions,
                manifest.Messages, manifest.Tasks, manifest.Attachments);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private async Task<ExportManifest> WriteDatabaseExportAsync(string tempDir, CancellationToken ct)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        await conn.OpenAsync(ct);

        List<DiaryRow> diaries;
        List<FactRow> facts;
        List<SessionRow> sessions;
        List<MessageRow> messages;
        List<TaskRow> tasks;
        List<TurnSummaryRow> turnSummaries;
        List<AttachmentRow> attachments;

        // 在同一个读事务内取数，避免导出过程中新消息写入造成跨表不一致。
        using (var tx = conn.BeginTransaction())
        {
            diaries = (await QueryAsync<DiaryRow>(conn, tx,
                "SELECT LogicalDate, Content FROM DiaryEntries ORDER BY LogicalDate DESC", ct)).ToList();
            facts = (await QueryAsync<FactRow>(conn, tx,
                "SELECT Id, Key, Value, Confidence, SourceTurnId, CreatedAtUtc, UpdatedAtUtc FROM Facts ORDER BY UpdatedAtUtc DESC", ct)).ToList();
            sessions = (await QueryAsync<SessionRow>(conn, tx,
                "SELECT Id, Title, CreatedAtUtc, UpdatedAtUtc, IsArchived FROM Sessions ORDER BY UpdatedAtUtc DESC", ct)).ToList();
            messages = (await QueryAsync<MessageRow>(conn, tx,
                @"SELECT Id, SessionId, TurnId, Role, Content, ReasoningContent, ToolCallsJson,
                         ToolCallId, ErrorMessage, ContentType, IsActive, VersionGroupId, CreatedAtUtc, LogicalDate
                  FROM Messages ORDER BY CreatedAtUtc, Id", ct)).ToList();
            tasks = (await QueryAsync<TaskRow>(conn, tx,
                @"SELECT Id, Title, Notes, DueAtUtc, RemindAtUtc, Status, Priority, ReminderCount,
                         LastRemindedAtUtc, LastFollowUpUtc, CreatedAtUtc, UpdatedAtUtc
                  FROM Tasks ORDER BY CreatedAtUtc DESC", ct)).ToList();
            turnSummaries = (await QueryAsync<TurnSummaryRow>(conn, tx,
                @"SELECT TurnId, SessionId, TurnIndex, LogicalDate, SummaryJson, Model, TokenCount, CreatedAtUtc
                  FROM TurnSummaries ORDER BY CreatedAtUtc DESC", ct)).ToList();
            attachments = (await QueryAsync<AttachmentRow>(conn, tx,
                @"SELECT Id, SessionId, TurnId, FileName, FileExtension, FileSize, Sha256,
                         StoredPath, ConvertedMarkdown, CreatedAtUtc
                  FROM Attachments ORDER BY CreatedAtUtc DESC", ct)).ToList();

            tx.Commit();
        }

        ct.ThrowIfCancellationRequested();

        var diaryDir = Path.Combine(tempDir, "Diaries");
        Directory.CreateDirectory(diaryDir);
        foreach (var diary in diaries)
        {
            ct.ThrowIfCancellationRequested();
            var date = string.IsNullOrWhiteSpace(diary.LogicalDate) ? "unknown" : diary.LogicalDate;
            var fileName = SanitizeFileName(date) + ".md";
            var content = $"# {date}\n\n{diary.Content}";
            await File.WriteAllTextAsync(Path.Combine(diaryDir, fileName), content, Utf8NoBom, ct);
        }

        await WriteJsonAsync(Path.Combine(tempDir, "Facts.json"), facts, ct);
        await WriteJsonAsync(Path.Combine(tempDir, "Tasks.json"), tasks, ct);
        await WriteJsonAsync(Path.Combine(tempDir, "TurnSummaries.json"), turnSummaries, ct);
        await WriteJsonAsync(Path.Combine(tempDir, "Attachments.json"), attachments, ct);

        var sessionsDir = Path.Combine(tempDir, "Sessions");
        Directory.CreateDirectory(sessionsDir);
        var messagesBySession = messages
            .GroupBy(m => m.SessionId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        foreach (var session in sessions)
        {
            ct.ThrowIfCancellationRequested();
            var title = string.IsNullOrWhiteSpace(session.Title) ? "New Chat" : session.Title;
            var safeTitle = SanitizeFileName(title).Trim();
            if (safeTitle.Length > 80) safeTitle = safeTitle[..80];
            if (string.IsNullOrEmpty(safeTitle)) safeTitle = "Chat";

            var fileName = $"{safeTitle}_{session.Id}.json";
            var payload = new
            {
                Session = session,
                Messages = messagesBySession.GetValueOrDefault(session.Id, []),
            };
            await WriteJsonAsync(Path.Combine(sessionsDir, fileName), payload, ct);
        }

        var blobCount = await CopyAttachmentBlobsAsync(tempDir, attachments, ct);

        return new ExportManifest(
            ExportedAtUtc: DateTime.UtcNow.ToString("O"),
            Diaries: diaries.Count,
            Facts: facts.Count,
            Sessions: sessions.Count,
            Messages: messages.Count,
            Tasks: tasks.Count,
            TurnSummaries: turnSummaries.Count,
            Attachments: attachments.Count,
            Blobs: blobCount);
    }

    private async Task<int> CopyAttachmentBlobsAsync(
        string tempDir, IReadOnlyList<AttachmentRow> attachments, CancellationToken ct)
    {
        var copied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blobDir = Path.Combine(tempDir, "Blobs");
        Directory.CreateDirectory(blobDir);

        foreach (var attachment in attachments)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(attachment.Sha256) ||
                string.IsNullOrWhiteSpace(attachment.StoredPath) ||
                !copied.Add(attachment.Sha256))
            {
                continue;
            }

            if (!File.Exists(attachment.StoredPath))
            {
                _logger.LogWarning("备份时附件文件缺失：{Path}", attachment.StoredPath);
                continue;
            }

            var destination = Path.Combine(blobDir, attachment.Sha256);
            await using var source = File.OpenRead(attachment.StoredPath);
            await using var target = File.Create(destination);
            await source.CopyToAsync(target, ct);
        }

        return copied.Count;
    }

    private static Task<IEnumerable<T>> QueryAsync<T>(
        SqliteConnection conn, SqliteTransaction tx, string sql, CancellationToken ct)
        => conn.QueryAsync<T>(new CommandDefinition(sql, transaction: tx, cancellationToken: ct));

    private static Task WriteJsonAsync<T>(string path, T value, CancellationToken ct)
        => File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), Utf8NoBom, ct);

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
            builder.Append(invalid.Contains(c) ? '_' : c);
        return builder.ToString();
    }

    private sealed record ExportManifest(
        string ExportedAtUtc,
        int Diaries,
        int Facts,
        int Sessions,
        int Messages,
        int Tasks,
        int TurnSummaries,
        int Attachments,
        int Blobs);

    private sealed class DiaryRow
    {
        public string LogicalDate { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
    }

    private sealed class FactRow
    {
        public long Id { get; set; }
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public string? SourceTurnId { get; set; }
        public string CreatedAtUtc { get; set; } = string.Empty;
        public string UpdatedAtUtc { get; set; } = string.Empty;
    }

    private sealed class SessionRow
    {
        public string Id { get; set; } = string.Empty;
        public string? Title { get; set; }
        public string CreatedAtUtc { get; set; } = string.Empty;
        public string UpdatedAtUtc { get; set; } = string.Empty;
        public long IsArchived { get; set; }
    }

    private sealed class MessageRow
    {
        public long Id { get; set; }
        public string SessionId { get; set; } = string.Empty;
        public string TurnId { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public string ReasoningContent { get; set; } = string.Empty;
        public string ToolCallsJson { get; set; } = string.Empty;
        public string ToolCallId { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;
        public long IsActive { get; set; }
        public string? VersionGroupId { get; set; }
        public string CreatedAtUtc { get; set; } = string.Empty;
        public string LogicalDate { get; set; } = string.Empty;
    }

    private sealed class TaskRow
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? Notes { get; set; }
        public string? DueAtUtc { get; set; }
        public string? RemindAtUtc { get; set; }
        public string Status { get; set; } = string.Empty;
        public long Priority { get; set; }
        public long ReminderCount { get; set; }
        public string? LastRemindedAtUtc { get; set; }
        public string? LastFollowUpUtc { get; set; }
        public string CreatedAtUtc { get; set; } = string.Empty;
        public string UpdatedAtUtc { get; set; } = string.Empty;
    }

    private sealed class TurnSummaryRow
    {
        public string TurnId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public long TurnIndex { get; set; }
        public string LogicalDate { get; set; } = string.Empty;
        public string SummaryJson { get; set; } = string.Empty;
        public string? Model { get; set; }
        public long? TokenCount { get; set; }
        public string CreatedAtUtc { get; set; } = string.Empty;
    }

    private sealed class AttachmentRow
    {
        public string Id { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string? TurnId { get; set; }
        public string FileName { get; set; } = string.Empty;
        public string? FileExtension { get; set; }
        public long? FileSize { get; set; }
        public string? Sha256 { get; set; }
        public string? StoredPath { get; set; }
        public string? ConvertedMarkdown { get; set; }
        public string CreatedAtUtc { get; set; } = string.Empty;
    }
}
