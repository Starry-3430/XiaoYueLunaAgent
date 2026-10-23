using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
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

    /// <summary>附件存储最多保留的记录数；新附件上传成功后自动删除最旧的多余记录。</summary>
    public const int MaxStoredCount = 30;

    /// <summary>单个附件的最大字节数（100 MB）。</summary>
    public const long MaxBytes = 100L * 1024 * 1024;

    /// <summary>单张图片发送时的最大字节数（10 MB，限制 Data URL / 请求体大小）。</summary>
    public const long MaxImageBytes = 10L * 1024 * 1024;

    /// <summary>允许进入压缩流程的原始图片上限（50 MB），避免超大文件解码耗尽内存。</summary>
    public const long MaxImageInputBytes = 50L * 1024 * 1024;

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
    /// 把图片附件压缩后读取为 data URL（供多模态识图）。优先使用内容寻址后的
    /// <see cref="Attachment.StoredPath"/>，回退到原始 <see cref="Attachment.SourcePath"/>。
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
            if (info.Length > MaxImageInputBytes)
            {
                error = LocalizationService.Instance.Format(
                    "Common_Attachment_ImageTooLarge", FormatSize(info.Length), FormatSize(MaxImageInputBytes));
                return false;
            }

            var compressed = ImageCompressionHelper.TryCompressImage(path, out var formatName, out var compressionError);
            var extension = !string.IsNullOrEmpty(a.FileExtension) ? a.FileExtension : Path.GetExtension(path);
            string mime;

            if (compressed is not null)
            {
                mime = formatName == "PNG" ? "image/png" : "image/jpeg";
                Serilog.Log.Information(
                    "图片已优化：{File} | 格式：{Format} | 原始：{OriginalKb} KB → 压缩后：{NewKb} KB",
                    a.FileName, formatName, info.Length / 1024, compressed.Length / 1024);
            }
            else if (string.Equals(extension, ".webp", StringComparison.OrdinalIgnoreCase)
                     && info.Length <= MaxImageBytes)
            {
                // WPF 没有内置 WebP 解码器：保留原图，避免把原本可用的附件直接判失败。
                compressed = File.ReadAllBytes(path);
                mime = "image/webp";
                Serilog.Log.Information("图片压缩不可用，使用原图：{File}", a.FileName);
            }
            else
            {
                error = LocalizationService.Instance.Format(
                    "Common_Attachment_ImageCompressFailed", compressionError ?? "unknown");
                return false;
            }

            var dataUrlValue = $"data:{mime};base64,{Convert.ToBase64String(compressed)}";
            if (dataUrlValue.Length > MaxImageBytes)
            {
                error = LocalizationService.Instance.Format(
                    "Common_Attachment_ImageTooLarge", FormatSize(dataUrlValue.Length), FormatSize(MaxImageBytes));
                return false;
            }

            dataUrl = dataUrlValue;
            return true;
        }
        catch (Exception ex)
        {
            error = LocalizationService.Instance.Format("Common_Attachment_ImageCompressFailed", ex.Message);
            return false;
        }
    }

    /// <summary>在后台线程执行图片压缩，避免大图解码阻塞 UI。</summary>
    public static Task<(bool Success, string DataUrl, string Error)> TryBuildImageDataUrlAsync(Attachment a)
        => Task.Run(() =>
        {
            var success = TryBuildImageDataUrl(a, out var dataUrl, out var error);
            return (success, dataUrl, error);
        });

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

    /// <summary>剪贴板中是否含有可直接导入的图片或文件。</summary>
    public static bool ClipboardHasAttachments()
    {
        try
        {
            return Clipboard.ContainsFileDropList() || Clipboard.ContainsImage();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 读取剪贴板中的文件（优先）或图片并生成待发送附件。
    /// 文件沿用原始文件名；剪贴板图片没有文件名，保存为 <c>clipboard_yyyyMMdd_HHmmss.png</c>。
    /// </summary>
    public static List<Attachment> GetClipboardAttachments()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var paths = Clipboard.GetFileDropList().Cast<string>();
                return FromPaths(paths);
            }

            if (Clipboard.ContainsImage())
            {
                var image = Clipboard.GetImage();
                if (image is null) return new List<Attachment>();

                var path = SaveClipboardImage(image);
                return path is null ? new List<Attachment>() : FromPaths(new[] { path });
            }
        }
        catch
        {
            // 剪贴板可能被其它进程占用：忽略，退回普通文本粘贴。
        }

        return new List<Attachment>();
    }

    private static string? SaveClipboardImage(BitmapSource image)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Luna", "cache", "clipboard");
            Directory.CreateDirectory(dir);

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var path = Path.Combine(dir, $"clipboard_{stamp}.png");
            var index = 1;
            while (File.Exists(path))
                path = Path.Combine(dir, $"clipboard_{stamp}_{index++}.png");

            using var stream = File.Create(path);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            encoder.Save(stream);
            return path;
        }
        catch
        {
            return null;
        }
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
