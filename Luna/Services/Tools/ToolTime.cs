using System.Globalization;

namespace Luna.Services.Tools;

/// <summary>待办工具的时间解析与格式化：接受 ISO 8601 或常见本地时间格式，统一存 UTC。</summary>
public static class ToolTime
{
    private static readonly string[] Formats =
    [
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-dd",
        "yyyy/MM/dd HH:mm",
        "yyyy/MM/dd",
    ];

    /// <summary>解析为 UTC。无时区信息时按本地时间处理。</summary>
    public static bool TryParseUtc(string? input, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var s = input.Trim();

        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var dto))
        {
            utc = dto.UtcDateTime;
            return true;
        }

        if (DateTime.TryParseExact(s, Formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var dt))
        {
            utc = dt.ToUniversalTime();
            return true;
        }

        return false;
    }

    /// <summary>把 UTC 时间格式化为本地时间字符串。</summary>
    public static string ToLocalString(DateTime utc) =>
        utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string? ToLocalString(DateTime? utc) =>
        utc is null ? null : ToLocalString(utc.Value);
}
