using System.IO;

namespace Luna.Services;

/// <summary>
/// 首次运行标记：在 %LocalAppData%\Luna 下维护一个隐藏的 .installed 文件。
/// 只要该文件存在，就说明用户已经完成过首次启动，即使 settings.json 被误删也不再弹出欢迎向导。
/// </summary>
public static class InstallMarker
{
    public static string MarkerPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Luna", ".installed");

    public static bool Exists => File.Exists(MarkerPath);

    /// <summary>写入隐藏标记文件；失败时静默忽略，不影响正常启动。</summary>
    public static void MarkInstalled()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            if (!File.Exists(MarkerPath))
                File.WriteAllText(MarkerPath, DateTimeOffset.UtcNow.ToString("O"));
            File.SetAttributes(MarkerPath, FileAttributes.Hidden);
        }
        catch
        {
            // 标记失败不应阻止用户使用应用。
        }
    }
}
