namespace Luna.Models;

/// <summary>工具执行审计记录。</summary>
public class ToolAuditLog
{
    public long Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public string? ArgumentsSummary { get; set; }
    public string PermissionResult { get; set; } = string.Empty;
    public string ExecutionResult { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
