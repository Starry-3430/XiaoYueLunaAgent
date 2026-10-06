namespace Luna.Services;

/// <summary>
/// 文档转换服务：把本地任意受支持的文档转换为 Markdown 文本，供附件集成使用。
/// </summary>
public interface IDocumentConverterService
{
    /// <summary>
    /// 将指定文件转换为 Markdown。
    /// </summary>
    /// <param name="filePath">待转换文件的完整路径。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>转换得到的 Markdown 文本；转换失败时返回友好的错误说明。</returns>
    Task<string> ConvertToMarkdownAsync(string filePath, CancellationToken ct = default);
}
