using System.IO;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Luna.Services;

public class DatabaseInitializer
{
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
        _logger.LogInformation("数据库初始化完成：{Path}", _dbPath);
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