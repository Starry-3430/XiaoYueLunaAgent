using System.IO;
using System.Security.Cryptography;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Luna.Services;

namespace Luna.Services.Data;

/// <summary>
/// 附件文件的内容寻址存储：把用户选择的文件复制到 %LocalAppData%\Luna\blobs\{sha256}。
/// 相同内容只存一份（幂等），数据库中保留原始文件名并让 StoredPath 指向该 blob。
/// </summary>
public class FileStorageService
{
    private readonly ILogger<FileStorageService> _logger;
    private readonly DatabaseInitializer _db;

    /// <summary>blobs 根目录：%LocalAppData%\Luna\blobs。</summary>
    public string BlobsRoot { get; }

    public FileStorageService(DatabaseInitializer db, ILogger<FileStorageService> logger)
    {
        _db = db;
        _logger = logger;
        BlobsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Luna", "blobs");
    }

    /// <summary>
    /// 计算源文件的 SHA-256，并将内容复制到 blobs 目录。目标已存在时跳过复制（内容寻址）。
    /// </summary>
    /// <returns>内容哈希、blob 完整路径与文件大小。</returns>
    public async Task<StoredFile> StoreAsync(string sourcePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("源文件路径为空。", nameof(sourcePath));
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("待存储的文件不存在。", sourcePath);

        Directory.CreateDirectory(BlobsRoot);

        var sha256 = await ComputeSha256Async(sourcePath, ct);
        var targetPath = Path.Combine(BlobsRoot, sha256);
        var size = new FileInfo(sourcePath).Length;

        if (File.Exists(targetPath))
        {
            _logger.LogDebug("附件内容已存在，跳过复制：{Hash}", sha256);
            return new StoredFile(sha256, targetPath, size);
        }

        // 先写临时文件再原子移动，避免中断或并发留下不完整 blob。
        var tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var source = File.OpenRead(sourcePath))
            await using (var dest = File.Create(tempPath))
            {
                await source.CopyToAsync(dest, ct);
            }

            File.Move(tempPath, targetPath, overwrite: false);
            _logger.LogInformation("附件已存储：{Hash}（{Size} 字节）→ {Path}", sha256, size, targetPath);
        }
        catch (IOException) when (File.Exists(targetPath))
        {
            // 并发写入：另一个调用已先落地同一内容，丢弃临时文件即可。
            TryDelete(tempPath);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        return new StoredFile(sha256, targetPath, size);
    }

    /// <summary>返回某个内容哈希对应的 blob 路径（不检查是否存在）。</summary>
    public string? GetBlobPath(string? sha256) =>
        string.IsNullOrWhiteSpace(sha256) ? null : Path.Combine(BlobsRoot, sha256);

    /// <summary>删除内容哈希对应的 blob 文件，释放磁盘空间。返回是否实际删除。</summary>
    public bool DeleteBlob(string? sha256)
    {
        var path = GetBlobPath(sha256);
        if (path is null || !File.Exists(path)) return false;

        try
        {
            File.Delete(path);
            _logger.LogInformation("已删除附件 blob：{Path}", path);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "删除附件 blob 失败：{Path}", path);
            return false;
        }
    }

    /// <summary>
    /// 删除数据库未引用的 blob 文件（例如恢复备份后遗留的旧附件）。
    /// 读取引用失败时不做删除，避免误删仍在使用的附件。
    /// </summary>
    public async Task<int> CleanupOrphansAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(BlobsRoot)) return 0;

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var conn = new SqliteConnection($"{_db.ConnectionString};Pooling=False");
            await conn.OpenAsync(ct);
            var hashes = await conn.QueryAsync<string>(new CommandDefinition(
                "SELECT DISTINCT Sha256 FROM Attachments WHERE Sha256 IS NOT NULL AND Sha256 <> ''",
                cancellationToken: ct));
            foreach (var hash in hashes) referenced.Add(hash);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取附件引用失败，跳过孤儿文件清理");
            return 0;
        }

        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(BlobsRoot))
        {
            ct.ThrowIfCancellationRequested();
            if (referenced.Contains(Path.GetFileName(path))) continue;

            try
            {
                File.Delete(path);
                deleted++;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "删除孤儿附件失败：{Path}", path);
            }
        }

        if (deleted > 0)
            _logger.LogInformation("已清理孤儿附件文件：{Count} 个", deleted);
        return deleted;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 清理失败不影响主流程
        }
    }
}

/// <summary>内容寻址存储后的文件信息。</summary>
public readonly record struct StoredFile(string Sha256, string StoredPath, long FileSize);
