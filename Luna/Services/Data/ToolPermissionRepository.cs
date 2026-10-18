using Dapper;
using Luna.Models;
using Microsoft.Data.Sqlite;

namespace Luna.Services.Data;

/// <summary>工具权限持久化与工具执行审计日志。</summary>
public class ToolPermissionRepository
{
    private readonly DatabaseInitializer _db;

    public ToolPermissionRepository(DatabaseInitializer db) => _db = db;

    // ===== 权限持久化 =====

    public async Task<HashSet<string>> GetPersistedPermissionsAsync()
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        var tools = await conn.QueryAsync<string>("SELECT ToolName FROM ToolPermissions");
        return new HashSet<string>(tools, StringComparer.OrdinalIgnoreCase);
    }

    public async Task AddPersistentPermissionAsync(string toolName)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        await conn.ExecuteAsync(
            "INSERT INTO ToolPermissions (ToolName, AllowedAtUtc) VALUES (@ToolName, @Now) ON CONFLICT(ToolName) DO NOTHING",
            new { ToolName = toolName, Now = DateTime.UtcNow.ToString("O") });
    }

    public async Task RemovePersistentPermissionAsync(string toolName)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        await conn.ExecuteAsync(
            "DELETE FROM ToolPermissions WHERE ToolName = @ToolName",
            new { ToolName = toolName });
    }

    public async Task ClearAllPersistentPermissionsAsync()
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        await conn.ExecuteAsync("DELETE FROM ToolPermissions");
    }

    // ===== 审计日志 =====

    public async Task AddAuditLogAsync(string sessionId, string toolName, string? arguments,
        string permissionResult, string executionResult, string? errorMessage = null)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        await conn.ExecuteAsync(
            @"INSERT INTO ToolAuditLogs
              (SessionId, ToolName, ArgumentsSummary, PermissionResult, ExecutionResult, ErrorMessage, CreatedAtUtc)
              VALUES (@SessionId, @ToolName, @Args, @Perm, @Exec, @Err, @Now)",
            new
            {
                SessionId = sessionId,
                ToolName = toolName,
                Args = Truncate(arguments, 500),
                Perm = permissionResult,
                Exec = executionResult,
                Err = Truncate(errorMessage, 1000),
                Now = DateTime.UtcNow.ToString("O"),
            });
    }

    /// <summary>最近的审计记录（供审计页面使用）。</summary>
    public async Task<List<ToolAuditLog>> GetRecentAuditLogsAsync(int limit = 200)
    {
        if (limit <= 0) return [];

        using var conn = new SqliteConnection(_db.ConnectionString);
        var rows = await conn.QueryAsync<ToolAuditLog>(
            "SELECT * FROM ToolAuditLogs ORDER BY Id DESC LIMIT @Limit",
            new { Limit = limit });
        return rows.ToList();
    }

    private static string? Truncate(string? text, int max)
        => string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..max] + "...";
}
