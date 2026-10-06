using System.IO;
using MarkItDown;
using Microsoft.Extensions.Logging;

namespace Luna.Services;

/// <summary>
/// 基于 ManagedCode.MarkItDown 的文档转换实现。
/// 依赖注入的 <see cref="IMarkItDownClient"/> 在启动时通过 <c>MarkItDownOptions.RootPath</c>
/// 把转换工作区定向到 Luna 缓存目录，避免污染用户目录。
/// </summary>
public class MarkItDownConverterService : IDocumentConverterService
{
    private readonly IMarkItDownClient _client;
    private readonly ILogger<MarkItDownConverterService> _logger;

    public MarkItDownConverterService(IMarkItDownClient client, ILogger<MarkItDownConverterService> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string> ConvertToMarkdownAsync(string filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return Friendly("未提供文件路径。");

        if (!File.Exists(filePath))
        {
            _logger.LogWarning("文档转换失败：文件不存在 {FilePath}", filePath);
            return Friendly($"文件不存在：{filePath}");
        }

        try
        {
            await using var result = await _client.ConvertAsync(filePath, ct);
            _logger.LogInformation("文档转换成功：{FilePath}（输出 {Length} 字符）", filePath, result.Markdown.Length);
            return result.Markdown;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("文档转换已取消：{FilePath}", filePath);
            throw;
        }
        catch (UnsupportedFormatException ex)
        {
            _logger.LogWarning(ex, "文档格式不受支持：{FilePath}", filePath);
            return Friendly($"不支持的文件格式：{Path.GetExtension(filePath)}");
        }
        catch (MissingDependencyException ex)
        {
            _logger.LogError(ex, "文档转换缺少依赖：{FilePath}", filePath);
            return Friendly("转换该文档所需的组件缺失：" + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "文档转换无访问权限：{FilePath}", filePath);
            return Friendly("没有访问该文件的权限：" + ex.Message);
        }
        catch (MarkItDownException ex)
        {
            _logger.LogError(ex, "文档转换失败：{FilePath}", filePath);
            return Friendly("文档转换失败：" + ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "文档转换出现未预期错误：{FilePath}", filePath);
            return Friendly("文档转换出现未预期错误：" + ex.Message);
        }
    }

    private static string Friendly(string message) => $"> **文档转换失败**：{message}";
}
