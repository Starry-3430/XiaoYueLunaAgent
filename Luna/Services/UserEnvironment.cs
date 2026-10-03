using System.IO;

namespace Luna.Services;

/// <summary>当前用户的环境路径，以及常见目录别名的解析。用于告知 AI 用户目录并支撑搜索定向。</summary>
public static class UserEnvironment
{
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public static string Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public static string Pictures => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    public static string Music => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
    public static string Videos => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    public static string Downloads => string.IsNullOrEmpty(Home) ? string.Empty : Path.Combine(Home, "Downloads");

    /// <summary>常用目录，按“最可能被搜索”的顺序排列。</summary>
    public static IEnumerable<string> CommonFolders
    {
        get
        {
            foreach (var dir in new[] { Desktop, Documents, Downloads, Pictures, Music, Videos, Home })
            {
                if (!string.IsNullOrWhiteSpace(dir))
                    yield return dir;
            }
        }
    }

    /// <summary>
    /// 解析目录：支持 desktop/documents/downloads/pictures/music/videos/home 等别名（含中文），
    /// 或返回原样的绝对路径。
    /// </summary>
    public static bool TryResolveFolder(string? input, out string folder)
    {
        folder = string.Empty;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var key = input.Trim().Trim('"').ToLowerInvariant();

        string? mapped = key switch
        {
            "desktop" or "桌面" => Desktop,
            "documents" or "document" or "文档" or "我的文档" => Documents,
            "downloads" or "download" or "下载" or "下载文件夹" => Downloads,
            "pictures" or "picture" or "图片" or "图片文件夹" => Pictures,
            "music" or "音乐" => Music,
            "videos" or "video" or "视频" => Videos,
            "home" or "user" or "profile" or "主目录" or "用户目录" or "用户主目录" => Home,
            _ => null,
        };

        if (!string.IsNullOrEmpty(mapped))
        {
            folder = mapped;
            return true;
        }

        if (Path.IsPathRooted(input))
        {
            folder = input;
            return true;
        }

        return false;
    }

    /// <summary>生成注入系统提示词的用户目录说明，让 AI 知道用户名与常用路径。</summary>
    public static string BuildPromptSection()
    {
        var lines = new List<string>
        {
            "## 当前用户环境",
            $"- 用户名/主目录：{Home}",
        };

        Add(lines, "桌面", Desktop);
        Add(lines, "文档", Documents);
        Add(lines, "下载", Downloads);
        Add(lines, "图片", Pictures);
        Add(lines, "音乐", Music);
        Add(lines, "视频", Videos);

        lines.Add("搜索或读取文件时，可直接使用上述绝对路径，或使用别名 desktop/documents/downloads/pictures/music/videos/home。");
        return string.Join('\n', lines);

        static void Add(List<string> list, string name, string path)
        {
            if (!string.IsNullOrWhiteSpace(path))
                list.Add($"- {name}：{path}");
        }
    }
}
