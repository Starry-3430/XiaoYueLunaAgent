using System.Text;
using System.Text.Json;
using Luna.Services;
using Microsoft.Extensions.Logging;

namespace Luna.Services.Tools;

/// <summary>查询、复制或清空 Luna 运行期间采集的文本剪贴板历史。</summary>
public sealed class ClipboardHistoryTool : ITool
{
    private static readonly JsonElement Schema = ToolSchema.Parse(
        """{"type":"object","properties":{"action":{"type":"string","enum":["list","copy","clear"]},"id":{"type":"integer","description":"历史条目 ID，copy 操作必填"},"limit":{"type":"integer","description":"返回条目数，默认 10，最大 50"}},"required":[]}""");
    private readonly ClipboardHistoryService _history;
    private readonly ILogger<ClipboardHistoryTool> _logger;

    public ClipboardHistoryTool(ClipboardHistoryService history, ILogger<ClipboardHistoryTool> logger)
    { _history = history; _logger = logger; }

    public string Name => "clipboard_history";
    public string DisplayName => ToolLocalization.DisplayName(Name);
    public string Description => ToolLocalization.Description(Name);
    public JsonElement ParametersSchema => Schema;
    public ToolRiskLevel Risk => ToolRiskLevel.Low;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var action = GetString(args, "action").ToLowerInvariant();
        if (string.IsNullOrEmpty(action)) action = "list";
        if (action == "clear")
        {
            _history.Clear();
            return ToolResult.Ok(ToolLocalization.Text("Tool_ClipboardHistory_Msg_Cleared"));
        }
        if (action == "copy")
        {
            if (!GetInt(args, "id", out var id)) return ToolResult.Error(ToolLocalization.Text("Tool_Msg_MissingParam", "id"));
            return await _history.CopyAsync(id)
                ? ToolResult.Ok(ToolLocalization.Text("Tool_ClipboardHistory_Msg_Copied", id))
                : ToolResult.Error(ToolLocalization.Text("Tool_ClipboardHistory_Msg_NotFound", id));
        }
        if (action != "list") return ToolResult.Error(ToolLocalization.Text("Tool_ClipboardHistory_Msg_InvalidAction"));

        var limit = GetInt(args, "limit", out var n) ? Math.Clamp(n, 1, 50) : 10;
        var entries = _history.GetEntries(limit);
        if (entries.Count == 0) return ToolResult.Ok(ToolLocalization.Text("Tool_ClipboardHistory_Msg_Empty"));
        var sb = new StringBuilder(ToolLocalization.Text("Tool_ClipboardHistory_Msg_List") + "\n");
        foreach (var entry in entries)
        {
            var preview = entry.Text.Replace("\r", " ").Replace("\n", " ");
            if (preview.Length > 200) preview = preview[..200] + "…";
            sb.Append('#').Append(entry.Id).Append(" [").Append(entry.CapturedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"))
                .Append("] ").AppendLine(preview);
        }
        _logger.LogInformation("查询剪贴板历史：{Count} 条", entries.Count);
        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static string GetString(JsonElement args, string name) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() ?? string.Empty : string.Empty;
    private static bool GetInt(JsonElement args, string name, out int value)
    {
        value = 0;
        return args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.TryGetInt32(out value);
    }
}
