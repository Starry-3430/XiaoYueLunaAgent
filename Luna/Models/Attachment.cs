namespace Luna.Models;

/// <summary>
/// 附件：用户上传文件在数据库中的元数据记录。
/// 文件本体以内容寻址方式存放在 %LocalAppData%\Luna\blobs\{Sha256}，
/// <see cref="StoredPath"/> 指向该文件，<see cref="ConvertedMarkdown"/> 保存转换结果。
/// </summary>
public class Attachment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; set; } = string.Empty;
    public string TurnId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string? FileExtension { get; set; }
    public long FileSize { get; set; }
    public string? Sha256 { get; set; }
    public string? StoredPath { get; set; }
    public string? ConvertedMarkdown { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>转换内容的 Token 估算值（仅界面展示使用，不入库）。</summary>
    public int TokenEstimate { get; set; }

    /// <summary>本次注入使用的 Markdown（截断后）。为 null 时使用 <see cref="ConvertedMarkdown"/>（仅运行期使用，不入库）。</summary>
    public string? InjectedMarkdown { get; set; }
}
