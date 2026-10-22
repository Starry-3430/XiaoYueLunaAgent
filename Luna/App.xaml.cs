using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using CommunityToolkit.Mvvm.Input;
using Luna.Controls;
using Luna.Localization;
using Microsoft.Extensions.DependencyInjection;
using WpfMarkdownViewer;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Luna.Data;
using Luna.Models;
using Luna.Services;
using Luna.Services.Data;
using Luna.Services.Jobs;
using Luna.Services.Tools;
using Luna.ViewModels;
using MarkItDown.DependencyInjection;
using System.NativeTray;
using System.Windows.Interop;
using System.Windows.Media;

namespace Luna;

/// <summary>
/// 应用程序入口：负责单实例控制、托盘图标、依赖注入、日志、数据库初始化及窗口管理。
/// </summary>
public partial class App : Application
{
    private readonly IHost _host = null!;              // 依赖注入与后台服务宿主
    private readonly bool _isStartupHelper;             // 是否为“管理员自启动”提权辅助进程
    private Mutex? _singleInstanceMutex;                // 单实例互斥体
    private EventWaitHandle? _showWindowEvent;          // 显示窗口事件
    private Win32Icon? _trayIconImage;                  // 托盘图标图像
    private TrayIconHost? _trayIcon;                    // 托盘图标宿主
    private TrayMenuItem? _trayOpenItem;                // 托盘菜单：主界面
    private TrayMenuItem? _trayCloseItem;               // 托盘菜单：关闭
    private HomeWindow? _homeWindow;                    // 主窗口实例
    private WelcomeWindow? _welcomeWindow;              // 首次运行欢迎向导
    private bool _backgroundServicesStarted;            // 后台调度器/日记服务是否已启动
    private bool _restarting;                           // 是否正在重启进程
    private bool _appReady;                             // 启动流程是否完成，可处理通知跳转
    private string? _pendingNotificationSessionId;      // 启动早期收到的通知会话 ID
    private CancellationTokenSource? _trayCts;          // 托盘点击延迟取消令牌，用于区分单击/双击
    private DateTime _lastTrayClickTime;                // 上次托盘点击时间，用于判断双击间隔
    private const int DoubleClickThresholdMs = 500;     // 双击判定阈值（毫秒）
    private const string MutexName = @"Global\Luna_SingleInstance_Mutex";
    private const string ShowEventName = @"Global\Luna_ShowWindow_Event";

    public App()
    {
        // 全局强制软件（CPU）渲染：覆盖所有窗口和弹窗，避免触发 NVIDIA 浮窗/覆盖层。
        // 注意：软件渲染下系统原生最小化/过渡动画会退化为普通效果。
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        // 管理员提权辅助进程：仅执行“管理员自启动”设置，不初始化日志/托盘/宿主与界面。
        _isStartupHelper = Environment.GetCommandLineArgs().Any(arg =>
            string.Equals(arg, StartupService.AdminHelperArgument, StringComparison.OrdinalIgnoreCase));
        if (_isStartupHelper) return;

        // 托盘菜单等早期 UI 在设置加载前即创建，先用默认语言初始化；启动时会按用户设置重新应用。
        LocalizationService.Instance.SetLanguage(AiSettings.DefaultUiLanguage);

        // ===== 日志配置 =====
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Luna", "logs");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "luna-.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Debug()
            .WriteTo.File(
                logPath,
                rollingInterval: RollingInterval.Day, // 按天滚动
                retainedFileCountLimit: 7,            // 最多保留 7 个文件
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        
        // ===== 全局异常处理 =====
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Fatal(args.ExceptionObject as Exception, "AppDomain 未处理异常");
        };

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Dispatcher 未处理异常");
            args.Handled = true; // 视情况决定是否吞掉
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Task 未观察异常");
            args.SetObserved();
        };
        
        // ===== 托盘图标初始化 =====
        var iconUri = new Uri("pack://application:,,,/iconStream/Luna.ico");
        var streamInfo = Application.GetResourceStream(iconUri);
        using var iconStream = streamInfo.Stream;
        _trayIconImage = new Win32Icon(iconStream)
        {
            ShowAsMonochrome = false, // 显示彩色图标
            ThemeMode = TrayThemeMode.System // 跟随系统
        };

        _trayOpenItem = new TrayMenuItem
        {
            Command = new TrayCommand(_ => Dispatcher.Invoke(ShowHomeWindow))
        };
        _trayCloseItem = new TrayMenuItem
        {
            Command = new TrayCommand(_ => Dispatcher.Invoke(Shutdown))
        };
        _trayIcon = new TrayIconHost
        {
            IconSource = _trayIconImage,
            ToolTipText = "Luna",
            // 右击托盘图标显示菜单
            Menu = new TrayMenu
            {
                _trayOpenItem,
                _trayCloseItem
            }
        };
        RefreshTrayText();
        // 订阅托盘单击事件
        _trayIcon.Click += (sender, e) => Dispatcher.Invoke(OnTrayClick);
        
        // ===== 依赖注入与数据库配置 =====
        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices((context, services) =>
            {
                // 窗口与视图模型注册
                services.AddSingleton<MainWindow>();
                services.AddSingleton<MainViewModel>();
                services.AddTransient<HomeWindow>();
                services.AddTransient<WelcomeWindow>();
                services.AddTransient<WelcomeViewModel>();
                services.AddSingleton<HomeViewModel>();
                services.AddSingleton<AiConnectionViewModel>();
                services.AddSingleton<GeneralSettingsViewModel>();
                services.AddSingleton<DiaryViewModel>();
                services.AddSingleton<AttachmentManagerViewModel>();
                
                // AI 服务配置（从 settings.json 加载）
                services.AddSingleton<SettingsService>();
                services.AddSingleton(sp => sp.GetRequiredService<SettingsService>().Load());
                services.AddSingleton<ToolSettingsService>();
                services.AddSingleton<ToolPermissionService>();
                services.AddSingleton<WindowsSearchService>();
                services.AddSingleton<ClipboardHistoryService>();
                services.AddSingleton<SettingsProxy>();
                services.AddSingleton(sp => new HttpClient(new SocketsHttpHandler
                {
                    UseProxy = true,
                    Proxy = sp.GetRequiredService<SettingsProxy>(),
                })
                {
                    // 不使用 HttpClient 的总超时：它会把持续有数据进来的流式回答也整体掐断。
                    // 具体超时由各调用方按“总超时 / 响应头超时 / 空闲超时”分别控制。
                    Timeout = Timeout.InfiniteTimeSpan,
                });
                services.AddSingleton<HotkeyService>();
                services.AddSingleton<StartupService>();
                services.AddSingleton<SystemPromptBuilder>();
                services.AddSingleton<IAiService, OpenAiService>();

                // 文档转换（MarkItDown）：注册 IMarkItDownClient，并把转换工作区定向到 Luna 缓存目录，
                // 避免转换过程中的临时文件污染用户目录。
                var converterCacheDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Luna", "cache", "markitdown");
                Directory.CreateDirectory(converterCacheDir);
                services.AddMarkItDown().UseRootPath(converterCacheDir);
                services.AddSingleton<IDocumentConverterService, MarkItDownConverterService>();

                services.AddSingleton(sp => new NotificationService(
                    _trayIcon, sp.GetRequiredService<ILogger<NotificationService>>()));
                services.AddSingleton<ITool, ClipboardReadTool>();
                services.AddSingleton<ITool, WebSearchTool>();
                services.AddSingleton<ITool, FetchUrlTool>();
                services.AddSingleton<ITool, ReadFileTool>();
                services.AddSingleton<ITool, FileTreeTool>();
                services.AddSingleton<ITool, WriteFileTool>();
                services.AddSingleton<ITool, GitTool>();
                services.AddSingleton<ITool, PowerShellTool>();
                services.AddSingleton<ITool, CodeExecuteTool>();
                services.AddSingleton<ITool, SendNotificationTool>();
                services.AddSingleton<ITool, SearchFilesTool>();
                services.AddSingleton<ITool, FileOpenTool>();
                services.AddSingleton<ITool, ReadScreenTool>();
                services.AddSingleton<ITool, NewsCalendarTool>();
                services.AddSingleton<ITool, WindowManagerTool>();
                services.AddSingleton<ITool, VolumeBrightnessTool>();
                services.AddSingleton<ITool, ClipboardHistoryTool>();
                services.AddSingleton<ITool, AddTaskTool>();
                services.AddSingleton<ITool, ListTasksTool>();
                services.AddSingleton<ITool, DiarySearchTool>();
                services.AddSingleton<ITool, TurnSearchTool>();
                services.AddSingleton<ITool, RememberFactTool>();
                services.AddSingleton<ITool, ForgetFactTool>();
                services.AddSingleton<ITool, ListFactsTool>();
                services.AddSingleton<ITool, CompleteTaskTool>();
                services.AddSingleton<ITool, DeleteTaskTool>();
                services.AddSingleton<ITool, AskUserTool>();
                services.AddSingleton<ITool, EnableToolGroupTool>();
                services.AddSingleton<ToolRegistry>();

                // SQLite 数据库路径与 DatabaseService 注册
                var dbDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Luna", "database");
                Directory.CreateDirectory(dbDir);
                var dbPath = Path.Combine(dbDir, "LunaData.db");
                services.AddSingleton(new DatabaseService($"Data Source={dbPath}"));
                services.AddSingleton<DatabaseInitializer>();
                services.AddSingleton<SessionRepository>();
                services.AddSingleton<MessageRepository>();
                services.AddSingleton<SearchRepository>();
                services.AddSingleton<TaskRepository>();
                services.AddSingleton<FactRepository>();
                services.AddSingleton<ToolPermissionRepository>();
                services.AddSingleton<AttachmentRepository>();
                services.AddSingleton<FileStorageService>();
                services.AddSingleton<DataExportService>();
                services.AddSingleton<DataImportService>();
                services.AddSingleton<TurnSummaryService>();
                services.AddSingleton<SessionTitleService>();
                services.AddSingleton<ChatGenerationService>();
                services.AddSingleton<ReminderService>();
                services.AddSingleton<DiaryService>();

                // 后台调度器：在数据库与主窗口就绪后手动启动（见 OnStartup）。
                services.AddSingleton<ReminderPollJob>();
                services.AddSingleton<IBackgroundJob>(sp => sp.GetRequiredService<ReminderPollJob>());
                services.AddSingleton<TaskFollowUpJob>();
                services.AddSingleton<IBackgroundJob>(sp => sp.GetRequiredService<TaskFollowUpJob>());
                services.AddSingleton<DailyBriefingJob>();
                services.AddSingleton<IBackgroundJob>(sp => sp.GetRequiredService<DailyBriefingJob>());
                services.AddSingleton<AutoBackupJob>();
                services.AddSingleton<IBackgroundJob>(sp => sp.GetRequiredService<AutoBackupJob>());
                services.AddSingleton<BackgroundSchedulerService>();
            })
            .Build();

        // 尽早订阅，确保应用由通知点击启动时也能收到激活事件（事件会缓冲到启动完成后再处理）。
        _host.Services.GetRequiredService<NotificationService>().SessionActivated += OnSessionNotificationActivated;
    }

    /// <summary>
    /// 托盘图标点击事件处理：通过时间间隔区分单击与双击。
    /// 两次点击间隔 &lt; 500ms 视为双击，打开 HomeWindow；
    /// 单击则等待 500ms 确认无第二次点击后显示 MainWindow。
    /// </summary>
    private async void OnTrayClick()
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastTrayClickTime).TotalMilliseconds;
        _lastTrayClickTime = now;

        // 欢迎向导打开时，托盘点击只激活向导，不打开其它窗口
        if (TryActivateWelcome()) return;

        // HomeWindow 已打开时，任何点击都激活它
        if (_homeWindow is { IsLoaded: true })
        {
            ShowHomeWindow();
            return;
        }

        // 间隔小于阈值 → 双击：打开 HomeWindow
        if (elapsed < DoubleClickThresholdMs)
        {
            _trayCts?.Cancel();
            ShowHomeWindow();
            return;
        }

        // 单击：启动阈值延迟，等待可能的第二次点击
        _trayCts?.Cancel();
        _trayCts = new CancellationTokenSource();
        var cts = _trayCts;

        try
        {
            await Task.Delay(DoubleClickThresholdMs, cts.Token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        // 延迟结束未被取消 → 确认是单击，显示 MainWindow
        if (cts.IsCancellationRequested) return;
        if (TryActivateWelcome()) return;
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
        mainWindow.ShowInternal();
        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
    }

    /// <summary>欢迎向导打开时将其激活并返回 true，阻止打开 MainWindow / HomeWindow。</summary>
    private bool TryActivateWelcome()
    {
        if (_welcomeWindow is not { IsLoaded: true }) return false;

        _welcomeWindow.WindowState = WindowState.Normal;
        _welcomeWindow.Activate();
        return true;
    }

    /// <summary>
    /// 显示或激活主界面窗口（HomeWindow）。
    /// </summary>
    private void ShowHomeWindow()
    {
        // 欢迎向导期间不允许打开主界面
        if (TryActivateWelcome()) return;

        // 打开主界面时收起胶囊，但不打断正在进行的生成。
        _host.Services.GetRequiredService<MainWindow>().HideCapsule();

        // 如果已存在且已加载，则激活
        if (_homeWindow is { IsLoaded: true })
        {
            _homeWindow.WindowState = WindowState.Normal;
            _homeWindow.Show();
            _homeWindow.Activate();
            return;
        }

        // 否则从 DI 容器获取新实例，并挂接 Closed 事件以清理引用
        var window = _host.Services.GetRequiredService<HomeWindow>();
        window.Closed += (_, _) =>
        {
            if (_homeWindow == window) _homeWindow = null;
        };
        _homeWindow = window;
        _homeWindow.WindowState = WindowState.Normal;
        _homeWindow.Show();
        _homeWindow.Activate();
    }

    /// <summary>处理系统通知点击：启动完成后显示主界面并打开目标会话。</summary>
    private void OnSessionNotificationActivated(string sessionId)
    {
        if (!_appReady)
        {
            _pendingNotificationSessionId = sessionId;
            return;
        }

        OpenSessionFromNotification(sessionId);
    }

    private void OpenSessionFromNotification(string sessionId)
    {
        // 欢迎向导期间 ShowHomeWindow 会直接返回，此时不强行打开会话。
        ShowHomeWindow();
        _ = OpenSessionFromNotificationAsync(sessionId);
    }

    private async Task OpenSessionFromNotificationAsync(string sessionId)
    {
        try
        {
            if (_homeWindow is { } window)
                await window.OpenSessionAsync(sessionId);
            else
                await _host.Services.GetRequiredService<HomeViewModel>().OpenSessionAsync(sessionId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "打开通知会话失败：{SessionId}", sessionId);
        }
    }

    /// <summary>
    /// 启动后台线程监听第二个实例发来的“显示窗口”事件。
    /// </summary>
    private void StartShowWindowListener()
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                _showWindowEvent!.WaitOne();

                // 在 UI 线程上显示 MainWindow
                Dispatcher.Invoke(() =>
                {
                    // 欢迎向导期间不允许打开主界面
                    if (TryActivateWelcome()) return;

                    var mainWindow = _host.Services.GetRequiredService<MainWindow>();
                    mainWindow.Show();
                    mainWindow.ShowInternal();
                    mainWindow.WindowState = WindowState.Normal;
                    mainWindow.Activate();
                });
            }
        })
        {
            IsBackground = true,
            Name = "Luna.ShowWindowListener"
        };
        thread.Start();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 提权辅助进程：执行“管理员自启动”设置后立即退出，不进入正常启动流程。
        if (_isStartupHelper)
        {
            Shutdown(StartupService.RunAdminHelper());
            return;
        }

        // 开机自启动：带 --startup 参数时静默启动，不显示主窗口
        var launchedAtStartup = e.Args.Any(arg =>
            string.Equals(arg, StartupService.StartupArgument, StringComparison.OrdinalIgnoreCase));

        // 首次运行信号：只认 .installed 标记。标记不存在就是新手，
        // 不参考 settings.json / 会话表，避免引导中途关闭后直接进入主界面。
        var isFirstRun = !InstallMarker.Exists;

        // 1. 单实例检查
        _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);

        if (!createdNew)
        {
            // 已有实例在运行，通知它显示窗口，然后退出
            try
            {
                _showWindowEvent = EventWaitHandle.OpenExisting(ShowEventName);
                _showWindowEvent.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // 已有实例还没创建事件，忽略
            }

            Shutdown();
            return;
        }

        // 2. 创建显示窗口事件，供第二个实例通知
        _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        StartShowWindowListener();

        // 3. 注册 WpfMarkdownViewer 插件（语法高亮、数学、SVG、Mermaid）
        DefaultCapabilities.RegisterAll();

        // 3.5 应用待恢复的备份：必须在 Host 启动与数据库初始化之前，避免运行中覆盖 SQLite。
        // 注意：这里运行在 UI 线程，若直接阻塞等待 async 方法，会因续体调度回 UI 线程而死锁。
        try
        {
            Task.Run(() => _host.Services.GetRequiredService<DataImportService>()
                .ApplyPendingRestoreAsync()).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "应用待恢复备份失败");
        }

        // 4. 启动 Host
        _host.Start();

        _host.Services.GetRequiredService<ClipboardHistoryService>().Start();

        // 4. 数据库初始化与完整性检查
        _host.Services.GetRequiredService<DatabaseInitializer>().Initialize();

        Log.Information("Luna 已启动");

        // 4.1 全局启用输入框自定义右键菜单（TextBox / PasswordBox 在加载时自动挂载）
        EditorContextMenu.EnableAutoAttach();

        // 5. 应用字体设置
        var settings = _host.Services.GetRequiredService<AiSettings>();
        ApplyCulture(settings.UiLanguage);
        RefreshTrayText();
        LocalizationService.Instance.PropertyChanged += (_, _) => RefreshTrayText();
        ThemeService.ApplyFont(settings.FontFamily);

        // 6. 初始化主窗口（MainWindow：紧凑悬浮输入窗）；开机自启动时不显示
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        var hotkey = _host.Services.GetRequiredService<HotkeyService>();
        hotkey.Attach(mainWindow);
        hotkey.Update(settings.Hotkey);
        hotkey.Pressed += () => Dispatcher.Invoke(() =>
        {
            // 欢迎向导期间不允许打开主界面
            if (TryActivateWelcome()) return;

            mainWindow.Show();
            mainWindow.ShowInternal();
            mainWindow.WindowState = WindowState.Normal;
            mainWindow.Activate();
        });

        if (!launchedAtStartup && !isFirstRun)
        {
            mainWindow.Show();
            mainWindow.ShowInternal();
        }

        // 7. 后台服务在欢迎向导结束后再启动，避免首次启动时后台任务提前创建会话。
        if (!isFirstRun)
            StartBackgroundServices();

        // 8. 配置文件异常时提示用户：恢复、重置或暂时不可用（首次运行时不再提示）
        var settingsService = _host.Services.GetRequiredService<SettingsService>();
        if (!isFirstRun && settingsService.LastLoadStatus != SettingsLoadStatus.Loaded)
            ShowSettingsLoadIssueDialog(mainWindow, settingsService.LastLoadStatus);

        // 9. 首次运行：显示欢迎向导；向导关闭（非完成）时直接结束进程。
        if (isFirstRun)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Dispatcher.BeginInvoke(new Action(ShowWelcomeWizard));
        }

        // 启动流程已完成：处理启动早期（含由通知点击启动）收到的会话跳转。
        _appReady = true;
        if (_pendingNotificationSessionId is { } pendingSessionId)
        {
            _pendingNotificationSessionId = null;
            OpenSessionFromNotification(pendingSessionId);
        }

        base.OnStartup(e);
    }

    /// <summary>
    /// 显示首次运行欢迎向导；向导不可用时回退到主界面，
    /// 避免首次运行标记缺失导致应用永久卡在欢迎向导而无法启动。
    /// </summary>
    private void ShowWelcomeWizard()
    {
        WelcomeWindow welcome;
        try
        {
            welcome = _host.Services.GetRequiredService<WelcomeWindow>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "创建欢迎向导失败，回退到主界面");
            CompleteOnboarding();
            return;
        }

        _welcomeWindow = welcome;

        var wizardFailed = false;
        try
        {
            welcome.ShowDialog();
        }
        catch (Exception ex)
        {
            wizardFailed = true;
            Log.Error(ex, "显示欢迎向导失败，回退到主界面");

            try { welcome.Close(); }
            catch { /* 忽略关闭失败，继续回退到主界面 */ }
        }
        finally
        {
            _welcomeWindow = null;
        }

        // 用户主动关闭向导：保留首次运行状态，下次启动可以继续引导。
        if (!wizardFailed && !welcome.Completed)
        {
            Shutdown();
            return;
        }

        if (wizardFailed)
            Log.Warning("欢迎向导不可用，已跳过首次引导并继续启动应用");

        CompleteOnboarding();
    }

    /// <summary>
    /// 首次运行收尾：打开主界面、写入完成标记并启动后台服务。
    /// 即使欢迎向导失败也必须继续启动链，避免配置、早报和提醒全部失效。
    /// </summary>
    private void CompleteOnboarding()
    {
        ShutdownMode = ShutdownMode.OnLastWindowClose;

        try
        {
            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.Show();
            mainWindow.ShowInternal();
            mainWindow.WindowState = WindowState.Normal;
            mainWindow.Activate();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "首次运行结束后显示主界面失败");
        }

        // 即使主界面显示失败也写入标记：否则每次启动都会重复失败的欢迎向导。
        InstallMarker.MarkInstalled();
        StartBackgroundServices();
    }

    /// <summary>启动后台调度器与日记服务（首次运行时推迟到欢迎向导完成之后）。</summary>
    private void StartBackgroundServices()
    {
        if (_backgroundServicesStarted) return;
        _backgroundServicesStarted = true;

        // 提醒轮询等；此时数据库与主窗口均已就绪
        _host.Services.GetRequiredService<BackgroundSchedulerService>()
            .StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        // 启动日记服务（启动即检查一次 + 每天 4:00 后轮询）
        _host.Services.GetRequiredService<DiaryService>().Start();

        // 预加载日记本内容，避免首次打开日记页时才加载导致的卡顿
        _ = _host.Services.GetRequiredService<DiaryViewModel>().LoadAsync();
    }

    /// <summary>应用界面语言：切换本地化服务、UI Culture 与 WPF 的 XmlLanguage（影响 XAML 内的格式化）。</summary>
    private static void ApplyCulture(string? uiLanguage)
    {
        var culture = LocalizationService.ResolveCulture(uiLanguage);
        LocalizationService.Instance.SetLanguage(culture);

        Thread.CurrentThread.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));
    }

    /// <summary>刷新托盘菜单的本地化文本。</summary>
    private void RefreshTrayText()
    {
        var loc = LocalizationService.Instance;
        if (_trayOpenItem is not null) _trayOpenItem.Header = loc["App_Tray_OpenMain"];
        if (_trayCloseItem is not null) _trayCloseItem.Header = loc["App_Tray_Close"];
    }

    /// <summary>配置文件恢复、重置或暂时不可用时弹出的提示。</summary>
    private static void ShowSettingsLoadIssueDialog(Window owner, SettingsLoadStatus status)
    {
        var loc = LocalizationService.Instance;
        var (titleKey, bodyKey) = status switch
        {
            SettingsLoadStatus.RecoveredFromBackup =>
                ("Dialog_SettingsRecovered_Title", "Dialog_SettingsRecovered_Body"),
            SettingsLoadStatus.ResetToDefaults =>
                ("Dialog_SettingsReset_Title", "Dialog_SettingsReset_Body"),
            _ => ("Dialog_SettingsUnavailable_Title", "Dialog_SettingsUnavailable_Body"),
        };

        var dialog = new LunaDialog
        {
            Owner = owner,
            DialogTitle = loc[titleKey],
            DialogContent = new TextBlock
            {
                Text = loc[bodyKey],
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Left,
            },
        };

        dialog.Buttons = new ObservableCollection<DialogButton>
        {
            new()
            {
                Text = loc["Dialog_Button_OK"],
                StyleKey = "StyleBeige",
                Command = new RelayCommand(dialog.Close),
            },
        };

        dialog.ShowDialog();
    }

    /// <summary>重启当前程序：释放单实例锁并启动新的进程。</summary>
    public void Restart()
    {
        _restarting = true;

        var exe = Environment.ProcessPath;
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch
        {
            // 忽略未持有的情况
        }
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;

        if (!string.IsNullOrEmpty(exe))
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 提权辅助进程未初始化日志、宿主与单实例资源，直接退出。
        if (_isStartupHelper)
        {
            base.OnExit(e);
            return;
        }

        Log.Information("Luna 正在退出");

        // 释放单实例相关资源
        _showWindowEvent?.Dispose();
        if (!_restarting)
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }

        // 停止后台调度器（手动启动，Host 不会自动管理；欢迎向导未完成时可能尚未启动）
        if (_backgroundServicesStarted)
        {
            try
            {
                using var schedulerStopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                _host.Services.GetRequiredService<BackgroundSchedulerService>()
                    .StopAsync(schedulerStopCts.Token).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "停止后台调度器失败");
            }
        }

        // 停止并释放 Host
        _host.StopAsync().GetAwaiter().GetResult();
        _host.Dispose();

        // 关闭日志并刷新缓冲区
        Log.CloseAndFlush();

        base.OnExit(e);
    }
    
}
