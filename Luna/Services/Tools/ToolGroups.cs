namespace Luna.Services.Tools;

/// <summary>
/// 工具分组与渐进披露规则：核心工具始终下发，其余工具按关键词或
/// <c>enable_tool_group</c> 元工具按需解锁。
/// </summary>
public static class ToolGroups
{
    public const string Info = "info";
    public const string FileSystem = "filesystem";
    public const string SystemDev = "system_dev";
    public const string MemoryOrganize = "memory_organize";

    /// <summary>始终下发的核心工具。</summary>
    public static readonly HashSet<string> Core = new(StringComparer.OrdinalIgnoreCase)
    {
        "ask_user", "enable_tool_group", "read_file", "read_clipboard",
        "list_tasks", "list_facts", "diary_search", "turn_search", "send_notification",
    };

    /// <summary>按需解锁的工具组。</summary>
    public static readonly IReadOnlyDictionary<string, HashSet<string>> Groups =
        new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [FileSystem] = new(StringComparer.OrdinalIgnoreCase)
            {
                "write_file", "search_files", "file_tree", "file_open", "read_screen",
            },
            [Info] = new(StringComparer.OrdinalIgnoreCase)
            {
                "web_search", "fetch_url",
            },
            [SystemDev] = new(StringComparer.OrdinalIgnoreCase)
            {
                "powershell", "code_execute", "git",
            },
            [MemoryOrganize] = new(StringComparer.OrdinalIgnoreCase)
            {
                "remember_fact", "forget_fact", "add_task", "complete_task", "delete_task",
            },
        };

    /// <summary>根据用户输入自动激活相关工具组的关键词。</summary>
    private static readonly IReadOnlyDictionary<string, string[]> Keywords =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [FileSystem] =
            [
                "文件", "目录", "文件夹", "截图", "屏幕", "保存", "写入", "读取", "打开",
                "file", "files", "folder", "folders", "directory", "directories",
                "screenshot", "screenshots", "save file", "write file", "read file", "open file",
                "path", "disk",
            ],
            [Info] =
            [
                "搜索", "搜一下", "搜一搜", "查找", "查一下", "查查", "帮我查", "帮我找",
                "联网", "上网", "天气", "新闻", "网址", "网页", "链接",
                "search", "searching", "web", "website", "websites", "url", "http",
                "news", "weather", "look up", "google", "bing", "browse", "fetch", "download",
            ],
            [SystemDev] =
            [
                "运行", "执行", "命令", "脚本", "代码", "终端",
                "cmd", "powershell", "shell", "script", "scripts", "terminal",
                "execute", "executing", "run ", "running", "git", "commit", "branch", "bash", "console",
            ],
            [MemoryOrganize] =
            [
                "记住", "忘掉", "忘记", "待办", "提醒", "任务",
                "remember", "forget", "todo", "task", "tasks", "remind", "reminders", "fact", "facts",
            ],
        };

    /// <summary>从用户输入中匹配需要自动激活的工具组。</summary>
    public static HashSet<string> MatchGroups(string? text)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
            return result;

        var lower = text.ToLowerInvariant();
        foreach (var (group, words) in Keywords)
        {
            if (words.Any(word => Matches(lower, word)))
                result.Add(group);
        }

        return result;
    }

    private static bool Matches(string text, string keyword)
    {
        // 中文关键词直接包含匹配；单个英文单词要求词边界，避免 "file" 命中 "profile"。
        var asciiWord = keyword.All(c => c < 128)
                        && keyword.All(c => char.IsLetterOrDigit(c) || c == ' ');
        if (!asciiWord)
            return text.Contains(keyword, StringComparison.Ordinal);

        if (keyword.Contains(' '))
            return text.Contains(keyword, StringComparison.Ordinal);

        var index = 0;
        while ((index = text.IndexOf(keyword, index, StringComparison.Ordinal)) >= 0)
        {
            var beforeOk = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            var afterIndex = index + keyword.Length;
            var afterOk = afterIndex >= text.Length || !char.IsLetterOrDigit(text[afterIndex]);
            if (beforeOk && afterOk)
                return true;

            index++;
        }

        return false;
    }

    public static bool IsValidGroup(string? group)
        => !string.IsNullOrWhiteSpace(group) && Groups.ContainsKey(group);

    /// <summary>返回组内工具名（用于元工具结果提示）。</summary>
    public static IReadOnlyCollection<string> GetGroupToolNames(string group)
        => Groups.TryGetValue(group, out var tools) ? tools : [];

    /// <summary>工具所属的按需组；核心工具或未分组工具返回 null。</summary>
    public static string? GetGroup(string toolName)
    {
        foreach (var (group, tools) in Groups)
        {
            if (tools.Contains(toolName))
                return group;
        }

        return null;
    }

    /// <summary>
    /// 是否应该把该工具下发给模型。未分组的工具默认放行，避免以后新增工具被意外隐藏。
    /// </summary>
    public static bool ShouldInclude(string toolName, IReadOnlySet<string> activeGroups)
    {
        if (Core.Contains(toolName))
            return true;

        var group = GetGroup(toolName);
        return group is null || activeGroups.Contains(group);
    }
}
