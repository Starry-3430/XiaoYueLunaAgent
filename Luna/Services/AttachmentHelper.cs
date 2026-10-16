using System.IO;
using Luna.Localization;
using Luna.Models;
using Microsoft.Win32;

namespace Luna.Services;

/// <summary>附件选择辅助：弹出文件对话框 / 接收拖拽路径，并按规则生成待发送附件。</summary>
public static class AttachmentHelper
{
    /// <summary>文件对话框筛选器（按当前语言构建）。</summary>
    public static string BuildDialogFilter()
    {
        var loc = LocalizationService.Instance;
        const string images = "*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp";
        const string docs = "*.docx;*.pdf;*.xlsx;*.pptx;*.txt;*.md;*.html";
        return $"{loc["Common_Attachment_AllFiles"]}|{images};{docs}" +
               $"|{loc["Common_Attachment_Images"]}|{images}" +
               $"|{loc["Common_Attachment_Documents"]}|{docs}" +
               $"|{loc["Common_Attachment_AnyFile"]}|*.*";
    }

    /// <summary>单条消息可附带的最大附件数量。</summary>
    public const int MaxCount = 10;

    /// <summary>单个附件的最大字节数（100 MB）。</summary>
    public const long MaxBytes = 100L * 1024 * 1024;

    /// <summary>单张图片的最大字节数（10 MB，多模态输入用）。</summary>
    public const long MaxImageBytes = 10L * 1024 * 1024;

    /// <summary>可交由 AI 直接识别的图片扩展名。</summary>
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp",
    };

    /// <summary>该附件是否为图片。</summary>
    public static bool IsImage(Attachment a)
        => !string.IsNullOrEmpty(a.FileExtension) && ImageExtensions.Contains(a.FileExtension);

    /// <summary>该扩展名是否为图片。</summary>
    public static bool IsImageExtension(string? extension)
        => !string.IsNullOrEmpty(extension) && ImageExtensions.Contains(extension);

    /// <summary>
    /// 把图片附件读取为 data URL（供多模态识图）。达到上限或读取失败时返回 false。
    /// 优先使用内容寻址后的 <see cref="Attachment.StoredPath"/>，回退到原始 <see cref="Attachment.SourcePath"/>。
    /// </summary>
    public static bool TryBuildImageDataUrl(Attachment a, out string dataUrl, out string error)
    {
        dataUrl = string.Empty;
        error = string.Empty;

        var path = !string.IsNullOrEmpty(a.StoredPath) ? a.StoredPath : a.SourcePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            error = LocalizationService.Instance["Common_Attachment_ImageMissing"];
            return false;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxImageBytes)
            {
                error = LocalizationService.Instance.Format("Common_Attachment_ImageTooLarge", FormatSize(info.Length), FormatSize(MaxImageBytes));
                return false;
            }

            var mime = GetImageMime(a.FileExtension ?? Path.GetExtension(path));
            var bytes = File.ReadAllBytes(path);
            dataUrl = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string GetImageMime(string? extension) => extension?.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        _ => "image/png",
    };

    /// <summary>
    /// 弹出多选文件对话框。选中的文件以原始路径暂存在 <see cref="Attachment.StoredPath"/> / <see cref="Attachment.SourcePath"/>，
    /// 发送时再由 <see cref="Data.FileStorageService"/> 复制到 blobs 并替换为内容寻址路径。取消时返回空列表。
    /// </summary>
    public static List<Attachment> PickFiles()
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = BuildDialogFilter(),
        };

        if (dialog.ShowDialog() != true) return new List<Attachment>();
        return FromPaths(dialog.FileNames);
    }

    /// <summary>把文件绝对路径转换为待发送附件（仅元数据，不做转换）。</summary>
    public static List<Attachment> FromPaths(IEnumerable<string> paths)
    {
        var result = new List<Attachment>();
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;

            long size = 0;
            try { size = new FileInfo(path).Length; } catch { /* 忽略无法读取的大小 */ }

            result.Add(new Attachment
            {
                FileName = Path.GetFileName(path),
                FileExtension = Path.GetExtension(path),
                FileSize = size,
                StoredPath = path,
                SourcePath = path,
            });
        }
        return result;
    }

    /// <summary>把字节数格式化为人类可读的大小（B / KB / MB / GB）。</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:0.#} KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return $"{mb:0.#} MB";
        return $"{mb / 1024.0:0.##} GB";
    }
}
