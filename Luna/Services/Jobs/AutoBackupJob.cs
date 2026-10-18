using System.IO;
using Luna.Services;
using Microsoft.Extensions.Logging;

namespace Luna.Services.Jobs;

/// <summary>
/// 每天凌晨 03:00 自动导出一次完整备份，并只保留最近 7 份自动备份。
/// 应用启动时若当天尚未备份，会补跑一次。
/// </summary>
public sealed class AutoBackupJob : IBackgroundJob
{
    private static readonly TimeSpan TargetTime = new(3, 0, 0);
    private const int RetentionCount = 7;

    private readonly DataExportService _exportService;
    private readonly ILogger<AutoBackupJob> _logger;

    public AutoBackupJob(DataExportService exportService, ILogger<AutoBackupJob> logger)
    {
        _exportService = exportService;
        _logger = logger;
    }

    public string Name => "AutoBackupJob";

    public bool RunOnStartup => true;

    public DateTime GetNextRunTime(DateTime lastRunTime)
    {
        var todayTarget = lastRunTime.Date + TargetTime;
        return lastRunTime < todayTarget ? todayTarget : todayTarget.AddDays(1);
    }

    public async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backupDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Luna", "backups");

        try
        {
            Directory.CreateDirectory(backupDir);

            var todayFile = $"Luna_AutoBackup_{DateTime.Now:yyyyMMdd}.zip";
            var targetPath = Path.Combine(backupDir, todayFile);
            if (File.Exists(targetPath))
            {
                _logger.LogInformation("今日自动备份已存在，跳过：{File}", todayFile);
                return;
            }

            // 先写临时文件，成功后再改名，避免失败后留下“看起来已备份”的半成品。
            var tempPath = targetPath + ".tmp";
            try
            {
                await _exportService.ExportAllAsync(tempPath, stoppingToken);
                stoppingToken.ThrowIfCancellationRequested();
                File.Move(tempPath, targetPath, overwrite: true);
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }

            _logger.LogInformation("每日自动备份完成：{Path}", targetPath);
            CleanupOldBackups(backupDir, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("每日自动备份在应用退出时取消");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "每日自动备份失败");
        }
    }

    private void CleanupOldBackups(string backupDir, CancellationToken ct)
    {
        var oldBackups = new DirectoryInfo(backupDir)
            .GetFiles("Luna_AutoBackup_*.zip")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Skip(RetentionCount)
            .ToList();

        foreach (var file in oldBackups)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                file.Delete();
                _logger.LogInformation("已清理过期自动备份：{File}", file.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "清理过期自动备份失败：{File}", file.Name);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
