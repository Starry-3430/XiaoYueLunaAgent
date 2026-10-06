using System.IO;
using Luna.Models;
using Microsoft.Win32;

namespace Luna.Services;

/// <summary>附件选择辅助：弹出文件对话框并按过滤规则生成待发送附件。</summary>
public static class AttachmentHelper
{
    public const string DialogFilter =
        "文档文件|*.docx;*.pdf;*.xlsx;*.pptx;*.txt;*.md;*.html|所有文件|*.*";

    /// <summary>
    /// 弹出多选文件对话框。选中的文件以原始路径暂存在 <see cref="Attachment.StoredPath"/>，
    /// 发送时再由 <see cref="Data.FileStorageService"/> 复制到 blobs 并替换为内容寻址路径。取消时返回空列表。
    /// </summary>
    public static List<Attachment> PickFiles()
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = DialogFilter,
        };

        if (dialog.ShowDialog() != true) return new List<Attachment>();

        var result = new List<Attachment>(dialog.FileNames.Length);
        foreach (var path in dialog.FileNames)
        {
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
