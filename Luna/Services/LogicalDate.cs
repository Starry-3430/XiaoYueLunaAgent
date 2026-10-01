namespace Luna.Services;

public static class LogicalDate
{
    // DateTime.UtcNow 传入，转本地，减 4 小时，取日期
    private const int ShiftHours = 4;

    public static string FromUtc(DateTime utcNow)
    {
        var local = utcNow.ToLocalTime().AddHours(-ShiftHours);
        return local.ToString("yyyy-MM-dd");
    }

    public static string Now() => FromUtc(DateTime.UtcNow);
}