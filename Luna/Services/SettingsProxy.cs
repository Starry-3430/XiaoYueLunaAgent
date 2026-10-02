using System.Net;
using Luna.Models;

namespace Luna.Services;

/// <summary>
/// 动态代理：从 <see cref="AiSettings"/> 读取当前的代理配置，
/// 因此设置变更后无需重建 HttpClient 即可生效。
/// </summary>
public sealed class SettingsProxy : IWebProxy
{
    private readonly AiSettings _settings;

    public SettingsProxy(AiSettings settings)
    {
        _settings = settings;
    }

    public ICredentials? Credentials { get; set; }

    public Uri? GetProxy(Uri destination)
    {
        if (!TryBuildProxyUri(out var uri)) return null;
        return uri;
    }

    public bool IsBypassed(Uri host) => !TryBuildProxyUri(out _);

    /// <summary>根据当前设置构造代理地址；未配置代理时返回 false。</summary>
    public bool TryBuildProxyUri(out Uri? uri)
    {
        uri = null;

        var type = _settings.ProxyType?.ToLowerInvariant() ?? "none";
        if (type is "none" or "" || string.IsNullOrWhiteSpace(_settings.ProxyServer))
            return false;

        var scheme = type switch
        {
            "socks4" => "socks4",
            "socks5" => "socks5",
            _ => "http",
        };

        var host = _settings.ProxyServer.Trim();
        var authority = _settings.ProxyPort > 0 ? $"{host}:{_settings.ProxyPort}" : host;

        if (!Uri.TryCreate($"{scheme}://{authority}", UriKind.Absolute, out var built))
            return false;

        uri = built;

        if (!string.IsNullOrEmpty(_settings.ProxyUsername))
        {
            Credentials = new NetworkCredential(
                _settings.ProxyUsername,
                _settings.ProxyPassword);
        }

        return true;
    }
}
