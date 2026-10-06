using System.IO;
using Luna.Models;
using Microsoft.Win32;

namespace Luna.Services;

/// <summary>附件选择辅助：弹出文件对话框 / 接收拖拽路径，并按规则生成待发送附件。</summary>
public static class AttachmentHelper
{
    public const string DialogFilter =
        "文档文件|*.docx;*.pdf;*.xlsx;*.pptx;*.txt;*.md;*.html|所有文件|*.*";

    /// <summary>单条消息可附带的最大附件数量。</summary>
    public const int MaxCount = 10;

    /// <summary>单个附件的最大字节数（100 MB）。</summary>
    public const long MaxBytes = 100L * 1024 * 1024;

    /// <summary>
    /// 弹出多选文件对话框。选中的文件以原始路径暂存在 <see cref="Attachment.StoredPath"/> / <see cref="Attachment.SourcePath"/>，
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
