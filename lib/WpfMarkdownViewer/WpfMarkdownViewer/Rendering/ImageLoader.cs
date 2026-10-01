using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace WpfMarkdownViewer.Rendering;

/// <summary>
/// Fetches the raw bytes of an image from any supported source — <c>data:</c> URIs, local files,
/// <c>pack://</c>/application resources, and http(s) — and hands them to <see cref="ImageView"/>, which
/// decides bitmap vs. SVG by sniffing the content. Remote images are cached to disk and revalidated with a
/// conditional request (<c>If-None-Match</c> ETag), so a 304 reuses the cache without re-downloading.
/// </summary>
internal static class ImageLoader
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "WpfMarkdownViewer", "images");

    public static async Task<byte[]?> LoadBytesAsync(Uri uri)
    {
        try
        {
            if (uri.Scheme == "data")
                return DecodeDataUri(uri.OriginalString);
            if (uri.IsFile)
                return await File.ReadAllBytesAsync(uri.LocalPath);
            if (uri.Scheme is "http" or "https")
                return await LoadHttpAsync(uri);
            return LoadResource(uri); // pack:// or application resource
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? DecodeDataUri(string s)
    {
        int comma = s.IndexOf(',');
        if (comma < 0)
            return null;
        string meta = s[..comma];
        string data = s[(comma + 1)..];
        return meta.Contains(";base64", StringComparison.OrdinalIgnoreCase)
            ? Convert.FromBase64String(data)
            : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(data));
    }

    private static byte[]? LoadResource(Uri uri)
    {
        var info = System.Windows.Application.GetResourceStream(uri);
        if (info?.Stream is null)
            return null;
        using var stream = info.Stream;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static async Task<byte[]?> LoadHttpAsync(Uri uri)
    {
        Directory.CreateDirectory(Dir);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.ToString())));
        string dataFile = Path.Combine(Dir, key + ".img");
        string etagFile = Path.Combine(Dir, key + ".etag");

        if (File.Exists(dataFile))
        {
            // No validator stored ⇒ trust the cache (image URLs are effectively immutable) and skip the network.
            if (!File.Exists(etagFile))
                return await File.ReadAllBytesAsync(dataFile);

            // Have an ETag ⇒ revalidate; a 304 reuses the cache, otherwise fall through to the fresh body.
            using var conditional = new HttpRequestMessage(HttpMethod.Get, uri);
            conditional.Headers.TryAddWithoutValidation("If-None-Match", await File.ReadAllTextAsync(etagFile));
            using var revalidate = await Http.SendAsync(conditional);
            if (revalidate.StatusCode == HttpStatusCode.NotModified)
                return await File.ReadAllBytesAsync(dataFile);
            return await StoreAsync(revalidate, dataFile, etagFile);
        }

        using var response = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri));
        return await StoreAsync(response, dataFile, etagFile);
    }

    private static async Task<byte[]?> StoreAsync(HttpResponseMessage response, string dataFile, string etagFile)
    {
        response.EnsureSuccessStatusCode();
        byte[] bytes = await response.Content.ReadAsByteArrayAsync();

        await File.WriteAllBytesAsync(dataFile, bytes);
        if (response.Headers.ETag is { } etag)
            await File.WriteAllTextAsync(etagFile, etag.ToString());
        else if (File.Exists(etagFile))
            File.Delete(etagFile);

        return bytes;
    }
}
