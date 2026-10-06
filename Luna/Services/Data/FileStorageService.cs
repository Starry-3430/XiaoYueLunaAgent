using System.IO;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Luna.Services.Data;

/// <summary>
/// 附件文件的内容寻址存储：把用户选择的文件复制到 %LocalAppData%\Luna\blobs\{sha256}。
/// 相同内容只存一份（幂等），数据库中保留原始文件名并让 StoredPath 指向该 blob。
/// </summary>
public class FileStorageService
{
    private readonly ILogger<FileStorageService> _logger;

    /// <summary>blobs 根目录：%LocalAppData%\Luna\blobs。</summary>
    public string BlobsRoot { get; }

    public FileStorageService(ILogger<FileStorageService> logger)
    {
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
