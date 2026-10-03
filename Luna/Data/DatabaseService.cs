using Dapper;
using Microsoft.Data.Sqlite;

namespace Luna.Data;

public class DatabaseService
{
    private const string CreateTableSql = """
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
        """;

    private const string CreateFtsSql = """
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

    public string ConnectionString { get; }

    public DatabaseService(string connectionString)
    {
        ConnectionString = connectionString;
    }

    public SqliteConnection GetConnection()
    {
        return new SqliteConnection(ConnectionString);
    }

    public void Init()
    {
        using var conn = GetConnection();
        conn.Open();

        using var tx = conn.BeginTransaction();

        conn.Execute(CreateTableSql, transaction: tx);
        conn.Execute(CreateFtsSql, transaction: tx);

        tx.Commit();
    }

    public void QuickCheck()
    {
        using var conn = GetConnection();
        conn.Open();
        conn.Execute("PRAGMA quick_check");
    }

    /// <summary>清空所有表中的数据，保留表结构（含索引/触发器）。</summary>
    public void ClearAllData()
    {
        using var conn = GetConnection();
        conn.Open();

        using var tx = conn.BeginTransaction();

        // 先删 DiaryEntries 以触发 FTS 同步触发器，再清空其余数据表
        conn.Execute("DELETE FROM DiaryEntries;", transaction: tx);
        conn.Execute("DELETE FROM DiaryFts;", transaction: tx);
        conn.Execute("DELETE FROM DiaryDays;", transaction: tx);
        conn.Execute("DELETE FROM Attachments;", transaction: tx);
        conn.Execute("DELETE FROM Messages;", transaction: tx);
        conn.Execute("DELETE FROM TurnSummaries;", transaction: tx);
        conn.Execute("DELETE FROM Sessions;", transaction: tx);
        conn.Execute("DELETE FROM sqlite_sequence WHERE name = 'Messages';", transaction: tx);

        tx.Commit();
    }

    private static void ExecuteRaw(SqliteConnection conn, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (tx is not null)
            cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }
}