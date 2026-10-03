using Dapper;
using Luna.Models;
using Microsoft.Data.Sqlite;

namespace Luna.Services;

/// <summary>待办事项的数据库访问（Tasks 表）。</summary>
public class TaskRepository
{
    private readonly DatabaseInitializer _db;
    public TaskRepository(DatabaseInitializer db) => _db = db;

    public async Task AddAsync(TaskItem t)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        await conn.ExecuteAsync(
            @"INSERT INTO Tasks
              (Id, Title, Notes, DueAtUtc, RemindAtUtc, Status, Priority, ReminderCount, LastRemindedAtUtc, CreatedAtUtc, UpdatedAtUtc)
              VALUES
              (@Id, @Title, @Notes, @DueAtUtc, @RemindAtUtc, @Status, @Priority, @ReminderCount, @LastRemindedAtUtc, @CreatedAtUtc, @UpdatedAtUtc)", t);
    }

    public async Task<TaskItem?> GetAsync(string id)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        return await conn.QueryFirstOrDefaultAsync<TaskItem>(
            "SELECT * FROM Tasks WHERE Id = @Id", new { Id = id });
    }

    public async Task<List<TaskItem>> ListAsync(string? status = null)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);

        if (string.IsNullOrWhiteSpace(status) || status.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var all = await conn.QueryAsync<TaskItem>(
                @"SELECT * FROM Tasks
                  ORDER BY (Status = 'pending') DESC, COALESCE(DueAtUtc, RemindAtUtc, '9999') ASC, CreatedAtUtc DESC");
            return all.ToList();
        }

        var rows = await conn.QueryAsync<TaskItem>(
            @"SELECT * FROM Tasks WHERE Status = @Status
              ORDER BY COALESCE(DueAtUtc, RemindAtUtc, '9999') ASC, CreatedAtUtc DESC",
            new { Status = status });
        return rows.ToList();
    }

    public async Task<int> CompleteAsync(string id)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        return await conn.ExecuteAsync(
            "UPDATE Tasks SET Status = 'done', UpdatedAtUtc = @Now WHERE Id = @Id",
            new { Id = id, Now = DateTime.UtcNow });
    }

    public async Task<int> DeleteAsync(string id)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        return await conn.ExecuteAsync("DELETE FROM Tasks WHERE Id = @Id", new { Id = id });
    }

    /// <summary>到点需要提醒的待办（pending 且 RemindAtUtc &lt;= now）。</summary>
    public async Task<List<TaskItem>> GetDueRemindersAsync(DateTime nowUtc)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        var rows = await conn.QueryAsync<TaskItem>(
            @"SELECT * FROM Tasks
              WHERE Status = 'pending'
                AND RemindAtUtc IS NOT NULL
                AND RemindAtUtc <= @Now
              ORDER BY RemindAtUtc ASC",
            new { Now = nowUtc });
        return rows.ToList();
    }

    public async Task MarkRemindedAsync(string id, DateTime nowUtc)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        await conn.ExecuteAsync(
            @"UPDATE Tasks
              SET ReminderCount = ReminderCount + 1, LastRemindedAtUtc = @Now, UpdatedAtUtc = @Now
              WHERE Id = @Id", new { Id = id, Now = nowUtc });
    }

    /// <summary>稍后提醒：修改 RemindAtUtc 并清除 LastRemindedAtUtc。</summary>
    public async Task SnoozeAsync(string id, DateTime remindAtUtc)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        await conn.ExecuteAsync(
            @"UPDATE Tasks
              SET RemindAtUtc = @RemindAtUtc, LastRemindedAtUtc = NULL, UpdatedAtUtc = @Now
              WHERE Id = @Id", new { Id = id, RemindAtUtc = remindAtUtc, Now = DateTime.UtcNow });
    }
}
