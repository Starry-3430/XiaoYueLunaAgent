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
    /// <summary>遇到可重试的瞬时 IO 错误（如文件被占用、网络共享中断）时的最大尝试次数。</summary>
    private const int MaxAttempts = 3;

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

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ConvertCoreAsync(filePath, ct);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("文档转换已取消：{FilePath}", filePath);
                throw;
            }
            catch (IOException ex) when (attempt < MaxAttempts)
            {
                // 瞬时 IO 错误（文件被占用 / 网络中断）：退避后重试
                _logger.LogWarning(ex, "文档转换 IO 失败，第 {Attempt} 次重试：{FilePath}", attempt, filePath);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct);
            }
            catch (UnsupportedFormatException ex)
            {
                _logger.LogWarning(ex, "文档格式不受支持或文件损坏：{FilePath}", filePath);
                return Friendly($"不支持或已损坏的文件格式：{Path.GetExtension(filePath)}");
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
    }

    /// <summary>在后台线程执行实际转换，避免大文件解析阻塞 UI 线程。</summary>
    private async Task<string> ConvertCoreAsync(string filePath, CancellationToken ct)
    {
        return await Task.Run(async () =>
        {
            await using var result = await _client.ConvertAsync(filePath, ct);
            _logger.LogInformation("文档转换成功：{FilePath}（输出 {Length} 字符）", filePath, result.Markdown.Length);
            return result.Markdown;
        }, ct);
    }

    private static string Friendly(string message) => $"> **文档转换失败**：{message}";
}
