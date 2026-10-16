// 工具：获取当前本地时间与 UTC 时间。
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using static Luna.Services.Tools.ToolLocalization;

namespace Luna.Services.Tools;

/// <summary>获取当前日期与时间（本地时间 + UTC），无参数、无副作用。</summary>
public class GetCurrentTimeTool : ITool
{
    private static readonly JsonElement Schema = ToolSchema.Empty;

    private readonly ILogger<GetCurrentTimeTool> _logger;

    public string Name => "get_current_time";
    public string DisplayName => ToolLocalization.DisplayName(Name);
    public string Description => ToolLocalization.Description(Name);
    public JsonElement ParametersSchema => Schema;

    public GetCurrentTimeTool(ILogger<GetCurrentTimeTool> logger)
    {
        _logger = logger;
    }

    public Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var now = DateTimeOffset.Now;
        var local = now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
        var utc = now.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var week = CultureInfo.CurrentUICulture.DateTimeFormat.GetDayName(now.DayOfWeek);

        _logger.LogInformation("获取当前时间: {Local}", local);

        return Task.FromResult(ToolResult.Ok(
            Text("Tool_GetCurrentTime_Msg_Result", local, week, utc, now.ToUnixTimeSeconds())));
    }
}
