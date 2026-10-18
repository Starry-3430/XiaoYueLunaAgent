using System.IO;
using System.IO.Compression;
using Dapper;
using Luna.Services.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Luna.Services;

/// <summary>
/// 导入备份：先把 ZIP 解压到待恢复目录并校验，真正覆盖数据库则在下一次启动、
/// 数据库初始化之前执行，避免运行中覆盖 SQLite 文件。
/// </summary>
public sealed class DataImportService
{
    private const string PendingFolderName = "pending_restore";
    private const string DatabaseFileName = "LunaData.db";
    private const string BlobsFolderName = "Blobs";

    private readonly DatabaseInitializer _db;
    private readonly FileStorageService _fileStorage;
    private readonly ILogger<DataImportService> _logger;

    public DataImportService(
        DatabaseInitializer db, FileStorageService fileStorage, ILogger<DataImportService> logger)
    {
        _db = db;
        _fileStorage = fileStorage;
        _logger = logger;
    }

    private string DatabasePath =>
        new SqliteConnectionStringBuilder(_db.ConnectionString).DataSource
        ?? throw new InvalidOperationException("数据库路径无效。");

    private string DatabaseDirectory => Path.GetDirectoryName(DatabasePath)!;

    private string StagingDirectory => Path.Combine(DatabaseDirectory, PendingFolderName);

    /// <summary>导入备份到待恢复目录；重启后由 <see cref="ApplyPendingRestoreAsync"/> 应用。</summary>
    public async Task StageAsync(string zipPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(zipPath))
            throw new ArgumentException("备份路径为空。", nameof(zipPath));
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("备份文件不存在。", zipPath);
        if (!string.Equals(Path.GetExtension(zipPath), ".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择 ZIP 格式的备份文件。");

        Directory.CreateDirectory(DatabaseDirectory);

        // 先解压到临时目录，校验通过后再替换旧的待恢复内容，避免失败时破坏上一次导入。
        var tempRoot = Path.Combine(DatabaseDirectory, PendingFolderName + "." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            await ExtractZipAsync(zipPath, tempRoot, ct);

            var stagedDb = Path.Combine(tempRoot, DatabaseFileName);
            if (!File.Exists(stagedDb))
                throw new InvalidDataException("备份中缺少 LunaData.db，可能不是当前版本导出的备份。");

            await ValidateDatabaseAsync(stagedDb, ct);

            if (Directory.Exists(StagingDirectory))
                Directory.Delete(StagingDirectory, recursive: true);

            Directory.Move(tempRoot, StagingDirectory);
            _logger.LogInformation("备份已导入，等待重启应用：{Zip}", zipPath);
        }
        catch
        {
            TryDeleteDirectory(tempRoot);
            throw;
        }
    }

    /// <summary>在启动早期应用待恢复备份；返回是否实际执行了恢复。</summary>
    public async Task<bool> ApplyPendingRestoreAsync(CancellationToken ct = default)
    {
        var stagedDb = Path.Combine(StagingDirectory, DatabaseFileName);
        if (!File.Exists(stagedDb))
        {
            TryDeleteDirectory(StagingDirectory);
            return false;
        }

        await ValidateDatabaseAsync(stagedDb, ct);

        var dbPath = DatabasePath;
        Directory.CreateDirectory(DatabaseDirectory);

        // 确保没有连接池持有的旧连接，否则覆盖 SQLite 文件会失败。
        SqliteConnection.ClearAllPools();

        var backupPath = dbPath + ".before-restore-" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        if (File.Exists(dbPath))
            BackupCurrentDatabase(dbPath, backupPath);

        ct.ThrowIfCancellationRequested();

        var stagedCopy = dbPath + "." + Guid.NewGuid().ToString("N") + ".restoring";
        try
        {
            File.Copy(stagedDb, stagedCopy, overwrite: true);
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
            File.Move(stagedCopy, dbPath);
        }
        catch
        {
            TryDelete(stagedCopy);

            // 覆盖失败时尽量回滚，避免应用启动后没有数据库文件。
            if (File.Exists(backupPath))
            {
                TryDelete(dbPath);
                try { File.Copy(backupPath, dbPath, overwrite: true); }
                catch (Exception rollbackEx) { _logger.LogError(rollbackEx, "回滚原数据库失败"); }
            }

            throw;
        }

        await CopyBlobsAsync(ct);
        await UpdateAttachmentPathsAsync(dbPath, ct);

        // 备份数据库未引用的旧附件属于孤儿文件，恢复后统一清理。
        try { await _fileStorage.CleanupOrphansAsync(ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "清理恢复后多余附件失败"); }

        // 先把待恢复数据库改名，确保即使目录清理失败，下次启动也不会重复覆盖。
        try { File.Move(stagedDb, stagedDb + ".done", overwrite: true); }
        catch (Exception ex) { _logger.LogWarning(ex, "标记已应用的备份失败"); }
        TryDeleteDirectory(StagingDirectory);

        _logger.LogInformation("备份已恢复：{Database}（原数据库备份：{Backup}）", dbPath, backupPath);
        return true;
    }

    private static async Task ExtractZipAsync(string zipPath, string destinationRoot, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var root = Path.GetFullPath(destinationRoot);
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();

            // 目录条目没有文件名，交给后续文件条目按需创建。
            if (string.IsNullOrEmpty(entry.Name)) continue;

            var destination = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("备份中包含非法文件路径。");

            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            await using var input = entry.Open();
            await using var output = new FileStream(
                destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await input.CopyToAsync(output, ct);
        }
    }

    private static async Task ValidateDatabaseAsync(string path, CancellationToken ct)
    {
        using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await conn.OpenAsync(ct);

        var check = await conn.ExecuteScalarAsync<string>(
            new CommandDefinition("PRAGMA quick_check;", cancellationToken: ct));
        if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("备份数据库完整性检查失败。");

        var tables = (await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name IN ('Sessions', 'Messages', 'DiaryEntries', 'Facts')",
            cancellationToken: ct))).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!tables.Contains("Sessions") || !tables.Contains("Messages") ||
            !tables.Contains("DiaryEntries") || !tables.Contains("Facts"))
        {
            throw new InvalidDataException("备份数据库缺少必要的数据表。");
        }
    }

    private static void BackupCurrentDatabase(string sourcePath, string backupPath)
    {
        using var source = new SqliteConnection($"Data Source={sourcePath};Pooling=False");
        using var target = new SqliteConnection($"Data Source={backupPath};Pooling=False");
        source.Open();
        target.Open();
        source.BackupDatabase(target);
    }

    private async Task CopyBlobsAsync(CancellationToken ct)
    {
        var stagedBlobs = Path.Combine(StagingDirectory, BlobsFolderName);
        if (!Directory.Exists(stagedBlobs)) return;

        Directory.CreateDirectory(_fileStorage.BlobsRoot);

        foreach (var sourcePath in Directory.EnumerateFiles(stagedBlobs))
        {
            ct.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(sourcePath);
            if (string.IsNullOrEmpty(fileName)) continue;

            var destination = Path.Combine(_fileStorage.BlobsRoot, fileName);
            await using var source = File.OpenRead(sourcePath);
            await using var target = new FileStream(
                destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await source.CopyToAsync(target, ct);
        }
    }

    /// <summary>附件路径可能来自另一台机器，恢复后按当前 blobs 目录重建。</summary>
    private async Task UpdateAttachmentPathsAsync(string dbPath, CancellationToken ct)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
            await conn.OpenAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE Attachments SET StoredPath = @Root || '\\' || Sha256 WHERE Sha256 IS NOT NULL AND Sha256 <> ''",
                new { Root = _fileStorage.BlobsRoot }, cancellationToken: ct));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "更新恢复后的附件路径失败");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
