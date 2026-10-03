using System.Globalization;
using System.IO;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Luna.Services;

public class DatabaseInitializer
{
    /// <summary>当前架构版本，用于执行一次性数据迁移。</summary>
    private const long CurrentSchemaVersion = 1;

    private readonly string _dbPath;
    private readonly ILogger<DatabaseInitializer> _logger;

    public string ConnectionString => $"Data Source={_dbPath}";

    public DatabaseInitializer(ILogger<DatabaseInitializer> logger)
    {
        _logger = logger;
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Luna", "database");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "LunaData.db");
    }

    public void Initialize()
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        conn.Execute("PRAGMA journal_mode = WAL;");
        conn.Execute("PRAGMA synchronous = NORMAL;");
        conn.Execute("PRAGMA foreign_keys = ON;");

        var check = conn.ExecuteScalar<string>("PRAGMA quick_check;");
        if (check != "ok")
        {
            _logger.LogError("数据库损坏：{Result}", check);
            throw new InvalidOperationException("数据库损坏");
        }

        conn.Execute(CreateTablesSql);  // 全部 IF NOT EXISTS
        AddReasoningContentColumn(conn);
        AddToolCallsJsonColumn(conn);
        AddToolCallIdColumn(conn);
        MigrateLogicalDates(conn);
        _logger.LogInformation("数据库初始化完成：{Path}", _dbPath);
    }

    /// <summary>
    /// 一次性迁移：早期版本用 UTC 日期写入 LogicalDate，现统一改为
    /// <see cref="LogicalDate"/>（本地时间 -4 小时）。迁移依据 CreatedAtUtc 重算，天然幂等，
    /// 由 PRAGMA user_version 控制只执行一次。
    /// </summary>
    private void MigrateLogicalDates(SqliteConnection conn)
    {
        var version = conn.ExecuteScalar<long>("PRAGMA user_version;");
        if (version >= CurrentSchemaVersion) return;

        var messages = RecomputeMessageLogicalDates(conn);
        var summaries = RecomputeTurnSummaryLogicalDates(conn);

        conn.Execute($"PRAGMA user_version = {CurrentSchemaVersion};");

        if (messages > 0 || summaries > 0)
            _logger.LogInformation("逻辑日期迁移完成：Messages {Messages} 行，TurnSummaries {Summaries} 行",
                messages, summaries);
    }

    private static int RecomputeMessageLogicalDates(SqliteConnection conn)
    {
        var rows = conn.Query<LogicalDateRow>(
            "SELECT Id, CreatedAtUtc FROM Messages").ToList();
        if (rows.Count == 0) return 0;

        using var tx = conn.BeginTransaction();
        var count = 0;
        foreach (var row in rows)
        {
            if (!TryParseUtc(row.CreatedAtUtc, out var utc)) continue;
            conn.Execute(
                "UPDATE Messages SET LogicalDate = @LogicalDate WHERE Id = @Id",
                new { LogicalDate = LogicalDate.FromUtc(utc), row.Id }, tx);
            count++;
        }
        tx.Commit();
        return count;
    }

    private static int RecomputeTurnSummaryLogicalDates(SqliteConnection conn)
    {
        var rows = conn.Query<TurnSummaryLogicalDateRow>(
            "SELECT TurnId, CreatedAtUtc FROM TurnSummaries").ToList();
        if (rows.Count == 0) return 0;

        using var tx = conn.BeginTransaction();
        var count = 0;
        foreach (var row in rows)
        {
            if (!TryParseUtc(row.CreatedAtUtc, out var utc)) continue;
            conn.Execute(
                "UPDATE TurnSummaries SET LogicalDate = @LogicalDate WHERE TurnId = @TurnId",
                new { LogicalDate = LogicalDate.FromUtc(utc), row.TurnId }, tx);
            count++;
        }
        tx.Commit();
        return count;
    }

    private static bool TryParseUtc(string? value, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsed))
            return false;

        utc = parsed.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : parsed.ToUniversalTime();
        return true;
    }

    private sealed class LogicalDateRow
    {
        public long Id { get; set; }
        public string CreatedAtUtc { get; set; } = string.Empty;
    }

    private sealed class TurnSummaryLogicalDateRow
    {
        public string TurnId { get; set; } = string.Empty;
        public string CreatedAtUtc { get; set; } = string.Empty;
    }

    private void AddReasoningContentColumn(SqliteConnection conn)
    {
        try
        {
            conn.Execute("ALTER TABLE Messages ADD COLUMN ReasoningContent TEXT NOT NULL DEFAULT ''");
        }
        catch (SqliteException)
        {
            // 列已存在，忽略
        }
    }

    private void AddToolCallsJsonColumn(SqliteConnection conn)
    {
        try
        {
            conn.Execute("ALTER TABLE Messages ADD COLUMN ToolCallsJson TEXT NOT NULL DEFAULT ''");
        }
        catch (SqliteException)
        {
            // 列已存在，忽略
        }
    }

    private void AddToolCallIdColumn(SqliteConnection conn)
    {
        try
        {
            conn.Execute("ALTER TABLE Messages ADD COLUMN ToolCallId TEXT NOT NULL DEFAULT ''");
        }
        catch (SqliteException)
        {
            // 列已存在，忽略
        }
    }

    private const string CreateTablesSql = """
        CREATE TABLE IF NOT EXISTS Sessions (
            Id            TEXT PRIMARY KEY,
            Title         TEXT,
            CreatedAtUtc  TEXT NOT NULL,
            UpdatedAtUtc  TEXT NOT NULL,
            IsArchived    INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS IX_Sessions_UpdatedAtUtc ON Sessions(UpdatedAtUtc);

        CREATE TABLE IF NOT EXISTS Messages (
            Id            INTEGER PRIMARY KEY AUTOINCREMENT,
            SessionId     TEXT NOT NULL,
            TurnId        TEXT NOT NULL,
            Role          TEXT NOT NULL,
            Content       TEXT NOT NULL,
            ContentType   TEXT NOT NULL DEFAULT 'text',
            CreatedAtUtc  TEXT NOT NULL,
            LogicalDate   TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_Messages_TurnId_SessionId_LogicalDate
            ON Messages(TurnId, SessionId, LogicalDate);

        CREATE TABLE IF NOT EXISTS TurnSummaries (
            TurnId        TEXT PRIMARY KEY,
            SessionId     TEXT NOT NULL,
            TurnIndex     INTEGER NOT NULL,
            LogicalDate   TEXT NOT NULL,
            SummaryJson   TEXT NOT NULL,
            Model         TEXT,
            TokenCount    INTEGER,
            CreatedAtUtc  TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_TurnSummaries_LogicalDate_SessionId
            ON TurnSummaries(LogicalDate, SessionId);

        CREATE TABLE IF NOT EXISTS DiaryEntries (
            LogicalDate      TEXT PRIMARY KEY,
            Content          TEXT NOT NULL,
            Model            TEXT,
            SourceSessionIds TEXT,
            SourceTurnIds    TEXT,
            TokenCount       INTEGER,
            CreatedAtUtc     TEXT NOT NULL,
            UpdatedAtUtc     TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS DiaryDays (
            LogicalDate   TEXT PRIMARY KEY,
            Status        TEXT NOT NULL DEFAULT 'Pending',
            LastError     TEXT,
            UpdatedAtUtc  TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS Attachments (
            Id            TEXT PRIMARY KEY,
            MessageId     INTEGER NOT NULL,
            FileName      TEXT NOT NULL,
            MimeType      TEXT,
            SizeBytes     INTEGER,
            Sha256        TEXT,
            StoredPath    TEXT,
            CreatedAtUtc  TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_Attachments_MessageId ON Attachments(MessageId);

        CREATE TABLE IF NOT EXISTS Tasks (
            Id                TEXT PRIMARY KEY,
            Title             TEXT NOT NULL,
            Notes             TEXT,
            DueAtUtc          TEXT,
            RemindAtUtc       TEXT,
            Status            TEXT NOT NULL DEFAULT 'pending',
            Priority          INTEGER NOT NULL DEFAULT 0,
            ReminderCount     INTEGER NOT NULL DEFAULT 0,
            LastRemindedAtUtc TEXT,
            CreatedAtUtc      TEXT NOT NULL,
            UpdatedAtUtc      TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_Tasks_Status ON Tasks(Status);
        CREATE INDEX IF NOT EXISTS IX_Tasks_RemindAtUtc ON Tasks(RemindAtUtc);

        CREATE VIRTUAL TABLE IF NOT EXISTS DiaryFts USING fts5(
            LogicalDate UNINDEXED, Content, tokenize='trigram'
        );

        CREATE TRIGGER IF NOT EXISTS DiaryEntries_ai
            AFTER INSERT ON DiaryEntries
        BEGIN
            INSERT INTO DiaryFts(LogicalDate, Content)
            VALUES (new.LogicalDate, new.Content);
        END;

        CREATE TRIGGER IF NOT EXISTS DiaryEntries_ad
            AFTER DELETE ON DiaryEntries
        BEGIN
            INSERT INTO DiaryFts(DiaryFts, LogicalDate, Content)
            VALUES('delete', old.LogicalDate, old.Content);
        END;

        CREATE TRIGGER IF NOT EXISTS DiaryEntries_au
            AFTER UPDATE ON DiaryEntries
        BEGIN
            INSERT INTO DiaryFts(DiaryFts, LogicalDate, Content)
            VALUES('delete', old.LogicalDate, old.Content);
            INSERT INTO DiaryFts(LogicalDate, Content)
            VALUES (new.LogicalDate, new.Content);
        END;
        """;
}