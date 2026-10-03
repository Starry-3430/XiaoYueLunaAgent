using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Luna.Services.Tools;

/// <summary>获取当前日期与时间（本地时间 + UTC），无参数、无副作用。</summary>
public class GetCurrentTimeTool : ITool
{
    private readonly ILogger<GetCurrentTimeTool> _logger;

    public string Name => "get_current_time";
    public string DisplayName => "当前时间";
    public string Description => "获取当前日期和时间，返回本地时间与 UTC 时间";
    public string ParametersSchema => """{"type":"object","properties":{},"required":[]}""";

    public GetCurrentTimeTool(ILogger<GetCurrentTimeTool> logger)
    {
        _logger = logger;
    }

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var now = DateTimeOffset.Now;
        var local = now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
        var utc = now.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var week = CultureInfo.GetCultureInfo("zh-CN").DateTimeFormat.GetDayName(now.DayOfWeek);

        _logger.LogInformation("获取当前时间: {Local}", local);

        return Task.FromResult(
            $"当前时间：{local}（{week}）\n" +
            $"UTC 时间：{utc}\n" +
            $"Unix 时间戳：{now.ToUnixTimeSeconds()}");
    }
}
