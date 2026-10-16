using System.Text;
using Luna.Localization;

namespace Luna.Services.Tools;

/// <summary>
/// 按工具名（snake_case）解析本地化的显示名与工具说明。
/// 约定 Key：显示名 <c>Tool_&lt;Name&gt;_DisplayName</c>，说明 <c>Tool_&lt;Name&gt;_AiDescription</c>。
/// </summary>
public static class ToolLocalization
{
    /// <summary>工具卡 / 权限确认框使用的显示名。</summary>
    public static string DisplayName(string toolName) =>
        LocalizationService.Instance[$"Tool_{Key(toolName)}_DisplayName"];

    /// <summary>发送给 AI 的工具说明。</summary>
    public static string Description(string toolName) =>
        LocalizationService.Instance[$"Tool_{Key(toolName)}_AiDescription"];

    /// <summary>按 Key 取工具文本（结果消息 / 参数说明）。</summary>
    public static string Text(string key) => LocalizationService.Instance[key];

    /// <summary>按 Key 取工具文本并按当前区域格式化（如 <c>{0}</c> 占位符）。</summary>
    public static string Text(string key, params object?[] args) => LocalizationService.Instance.Format(key, args);

    /// <summary>按 Key 取参数说明，并做 JSON 字符串转义（注入 schema 模板用，避免引号/换行破坏 JSON）。</summary>
    public static string Param(string key) => JsonEscape(LocalizationService.Instance[key]);

    private static string JsonEscape(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>snake_case → PascalCase（web_search → WebSearch）。</summary>
    private static string Key(string name)
    {
        var sb = new StringBuilder(name.Length);
        var upper = true;
        foreach (var c in name)
        {
            if (c == '_')
            {
                upper = true;
                continue;
            }

            sb.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }

        return sb.ToString();
    }
}
