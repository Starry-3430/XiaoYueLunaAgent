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
/// 搴旂敤绋嬪簭鍏ュ彛锛氳礋璐ｅ崟瀹炰緥鎺у埗銆佹墭鐩樺浘鏍囥€佷緷璧栨敞鍏ャ€佹棩蹇椼€佹暟鎹簱鍒濆鍖栧強绐楀彛绠＄悊銆?/// </summary>
public partial class App : Application
{
    private readonly IHost _host = null!;              // 渚濊禆娉ㄥ叆涓庡悗鍙版湇鍔″涓?    private readonly bool _isStartupHelper;             // 鏄惁涓衡€滅鐞嗗憳鑷惎鍔ㄢ€濇彁鏉冭緟鍔╄繘绋?    private Mutex? _singleInstanceMutex;                // 鍗曞疄渚嬩簰鏂ヤ綋
    private EventWaitHandle? _showWindowEvent;          // 鏄剧ず绐楀彛浜嬩欢
    private Win32Icon? _trayIconImage;                  // 鎵樼洏鍥炬爣鍥惧儚
    private TrayIconHost? _trayIcon;                    // 鎵樼洏鍥炬爣瀹夸富
    private TrayMenuItem? _trayOpenItem;                // 鎵樼洏鑿滃崟锛氫富鐣岄潰
    private TrayMenuItem? _trayCloseItem;               // 鎵樼洏鑿滃崟锛氬叧闂?    private HomeWindow? _homeWindow;                    // 涓荤獥鍙ｅ疄渚?    private bool _restarting;                           // 鏄惁姝ｅ湪閲嶅惎杩涚▼
    private CancellationTokenSource? _trayCts;          // 鎵樼洏鐐瑰嚮寤惰繜鍙栨秷浠ょ墝锛岀敤浜庡尯鍒嗗崟鍑?鍙屽嚮
    private DateTime _lastTrayClickTime;                // 涓婃鎵樼洏鐐瑰嚮鏃堕棿锛岀敤浜庡垽鏂弻鍑婚棿闅?    private const int DoubleClickThresholdMs = 500;     // 鍙屽嚮鍒ゅ畾闃堝€硷紙姣锛?    private const string MutexName = @"Global\Luna_SingleInstance_Mutex";
    private const string ShowEventName = @"Global\Luna_ShowWindow_Event";

    public App()
    {
        // 寮哄埗浣跨敤杞欢娓叉煋锛岄伩鍏嶇‖浠跺姞閫熷鑷寸殑鍏煎鎬ч棶棰?        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        // 绠＄悊鍛樻彁鏉冭緟鍔╄繘绋嬶細浠呮墽琛屸€滅鐞嗗憳鑷惎鍔ㄢ€濊缃紝涓嶅垵濮嬪寲鏃ュ織/鎵樼洏/瀹夸富涓庣晫闈€?        _isStartupHelper = Environment.GetCommandLineArgs().Any(arg =>
            string.Equals(arg, StartupService.AdminHelperArgument, StringComparison.OrdinalIgnoreCase));
        if (_isStartupHelper) return;

        // 鎵樼洏鑿滃崟绛夋棭鏈?UI 鍦ㄨ缃姞杞藉墠鍗冲垱寤猴紝鍏堢敤榛樿璇█鍒濆鍖栵紱鍚姩鏃朵細鎸夌敤鎴疯缃噸鏂板簲鐢ㄣ€?        LocalizationService.Instance.SetLanguage(AiSettings.DefaultUiLanguage);

        // ===== 鏃ュ織閰嶇疆 =====
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
                rollingInterval: RollingInterval.Day, // 鎸夊ぉ婊氬姩
                retainedFileCountLimit: 7,            // 鏈€澶氫繚鐣?7 涓枃浠?                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        
        // ===== 鍏ㄥ眬寮傚父澶勭悊 =====
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Fatal(args.ExceptionObject as Exception, "AppDomain 鏈鐞嗗紓甯?);
        };

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Dispatcher 鏈鐞嗗紓甯?);
            args.Handled = true; // 瑙嗘儏鍐靛喅瀹氭槸鍚﹀悶鎺?        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Task 鏈瀵熷紓甯?);
            args.SetObserved();
        };
        
        // ===== 鎵樼洏鍥炬爣鍒濆鍖?=====
        var iconUri = new Uri("pack://application:,,,/iconStream/Luna.ico");
        var streamInfo = Application.GetResourceStream(iconUri);
        using var iconStream = streamInfo.Stream;
        _trayIconImage = new Win32Icon(iconStream)
        {
            ShowAsMonochrome = false, // 鏄剧ず褰╄壊鍥炬爣
            ThemeMode = TrayThemeMode.System // 璺熼殢绯荤粺
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
            // 鍙冲嚮鎵樼洏鍥炬爣鏄剧ず鑿滃崟
            Menu = new TrayMenu
            {
                _trayOpenItem,
                _trayCloseItem
            }
        };
        RefreshTrayText();
        // 璁㈤槄鎵樼洏鍗曞嚮浜嬩欢
        _trayIcon.Click += (sender, e) => Dispatcher.Invoke(OnTrayClick);
        
        // ===== 渚濊禆娉ㄥ叆涓庢暟鎹簱閰嶇疆 =====
        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices((context, services) =>
            {
                // 绐楀彛涓庤鍥炬ā鍨嬫敞鍐?                services.AddSingleton<MainWindow>();
                services.AddSingleton<MainViewModel>();
                services.AddTransient<HomeWindow>();
                services.AddSingleton<HomeViewModel>();
                services.AddSingleton<AiConnectionViewModel>();
                services.AddSingleton<GeneralSettingsViewModel>();
                services.AddSingleton<DiaryViewModel>();
                services.AddSingleton<AttachmentManagerViewModel>();
                
                // AI 鏈嶅姟閰嶇疆锛堜粠 settings.json 鍔犺浇锛?                services.AddSingleton<SettingsService>();
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
                    // 涓嶄娇鐢?HttpClient 鐨勬€昏秴鏃讹細瀹冧細鎶婃寔缁湁鏁版嵁杩涙潵鐨勬祦寮忓洖绛斾篃鏁翠綋鎺愭柇銆?                    // 鍏蜂綋瓒呮椂鐢卞悇璋冪敤鏂规寜鈥滄€昏秴鏃?/ 鍝嶅簲澶磋秴鏃?/ 绌洪棽瓒呮椂鈥濆垎鍒帶鍒躲€?                    Timeout = Timeout.InfiniteTimeSpan,
                });
                services.AddSingleton<HotkeyService>();
                services.AddSingleton<StartupService>();
                services.AddSingleton<SystemPromptBuilder>();
                services.AddSingleton<IAiService, OpenAiService>();

                // 鏂囨。杞崲锛圡arkItDown锛夛細娉ㄥ唽 IMarkItDownClient锛屽苟鎶婅浆鎹㈠伐浣滃尯瀹氬悜鍒?Luna 缂撳瓨鐩綍锛?                // 閬垮厤杞崲杩囩▼涓殑涓存椂鏂囦欢姹℃煋鐢ㄦ埛鐩綍銆?                var converterCacheDir = Path.Combine(
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

                // SQLite 鏁版嵁搴撹矾寰勪笌 DatabaseService 娉ㄥ唽
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
                services.AddSingleton<TurnSummaryService>();
                services.AddSingleton<SessionTitleService>();
                services.AddSingleton<ChatGenerationService>();
                services.AddSingleton<ReminderService>();
                services.AddSingleton<DiaryService>();

                // 鍚庡彴璋冨害鍣細鍦ㄦ暟鎹簱涓庝富绐楀彛灏辩华鍚庢墜鍔ㄥ惎鍔紙瑙?OnStartup锛夈€?                services.AddSingleton<ReminderPollJob>();
                services.AddSingleton<IBackgroundJob>(sp => sp.GetRequiredService<ReminderPollJob>());
                services.AddSingleton<TaskFollowUpJob>();
                services.AddSingleton<IBackgroundJob>(sp => sp.GetRequiredService<TaskFollowUpJob>());
                services.AddSingleton<DailyBriefingJob>();
                services.AddSingleton<IBackgroundJob>(sp => sp.GetRequiredService<DailyBriefingJob>());
                services.AddSingleton<BackgroundSchedulerService>();
            })
            .Build();
    }

    /// <summary>
    /// 鎵樼洏鍥炬爣鐐瑰嚮浜嬩欢澶勭悊锛氶€氳繃鏃堕棿闂撮殧鍖哄垎鍗曞嚮涓庡弻鍑汇€?    /// 涓ゆ鐐瑰嚮闂撮殧 &lt; 500ms 瑙嗕负鍙屽嚮锛屾墦寮€ HomeWindow锛?    /// 鍗曞嚮鍒欑瓑寰?500ms 纭鏃犵浜屾鐐瑰嚮鍚庢樉绀?MainWindow銆?    /// </summary>
    private async void OnTrayClick()
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastTrayClickTime).TotalMilliseconds;
        _lastTrayClickTime = now;

        // HomeWindow 宸叉墦寮€鏃讹紝浠讳綍鐐瑰嚮閮芥縺娲诲畠
        if (_homeWindow is { IsLoaded: true })
        {
            ShowHomeWindow();
            return;
        }

        // 闂撮殧灏忎簬闃堝€?鈫?鍙屽嚮锛氭墦寮€ HomeWindow
        if (elapsed < DoubleClickThresholdMs)
        {
            _trayCts?.Cancel();
            ShowHomeWindow();
            return;
        }

        // 鍗曞嚮锛氬惎鍔ㄩ槇鍊煎欢杩燂紝绛夊緟鍙兘鐨勭浜屾鐐瑰嚮
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

        // 寤惰繜缁撴潫鏈鍙栨秷 鈫?纭鏄崟鍑伙紝鏄剧ず MainWindow
        if (cts.IsCancellationRequested) return;
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
        mainWindow.ShowInternal();
        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
    }

    /// <summary>
    /// 鏄剧ず鎴栨縺娲讳富鐣岄潰绐楀彛锛圚omeWindow锛夈€?    /// </summary>
    private void ShowHomeWindow()
    {
        // 鎵撳紑涓荤晫闈㈡椂鏀惰捣鑳跺泭锛屼絾涓嶆墦鏂鍦ㄨ繘琛岀殑鐢熸垚銆?        _host.Services.GetRequiredService<MainWindow>().HideCapsule();

        // 濡傛灉宸插瓨鍦ㄤ笖宸插姞杞斤紝鍒欐縺娲?        if (_homeWindow is { IsLoaded: true })
        {
            _homeWindow.WindowState = WindowState.Normal;
            _homeWindow.Show();
            _homeWindow.Activate();
            return;
        }

        // 鍚﹀垯浠?DI 瀹瑰櫒鑾峰彇鏂板疄渚嬶紝骞舵寕鎺?Closed 浜嬩欢浠ユ竻鐞嗗紩鐢?        var window = _host.Services.GetRequiredService<HomeWindow>();
        window.Closed += (_, _) =>
        {
            if (_homeWindow == window) _homeWindow = null;
        };
        _homeWindow = window;
        _homeWindow.WindowState = WindowState.Normal;
        _homeWindow.Show();
        _homeWindow.Activate();
    }

    /// <summary>
    /// 鍚姩鍚庡彴绾跨▼鐩戝惉绗簩涓疄渚嬪彂鏉ョ殑鈥滄樉绀虹獥鍙ｂ€濅簨浠躲€?    /// </summary>
    private void StartShowWindowListener()
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                _showWindowEvent!.WaitOne();

                // 鍦?UI 绾跨▼涓婃樉绀?MainWindow
                Dispatcher.Invoke(() =>
                {
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
        // 鎻愭潈杈呭姪杩涚▼锛氭墽琛屸€滅鐞嗗憳鑷惎鍔ㄢ€濊缃悗绔嬪嵆閫€鍑猴紝涓嶈繘鍏ユ甯稿惎鍔ㄦ祦绋嬨€?        if (_isStartupHelper)
        {
            Shutdown(StartupService.RunAdminHelper());
            return;
        }

        // 寮€鏈鸿嚜鍚姩锛氬甫 --startup 鍙傛暟鏃堕潤榛樺惎鍔紝涓嶆樉绀轰富绐楀彛
        var launchedAtStartup = e.Args.Any(arg =>
            string.Equals(arg, StartupService.StartupArgument, StringComparison.OrdinalIgnoreCase));

        // 1. 鍗曞疄渚嬫鏌?        _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);

        if (!createdNew)
        {
            // 宸叉湁瀹炰緥鍦ㄨ繍琛岋紝閫氱煡瀹冩樉绀虹獥鍙ｏ紝鐒跺悗閫€鍑?            try
            {
                _showWindowEvent = EventWaitHandle.OpenExisting(ShowEventName);
                _showWindowEvent.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // 宸叉湁瀹炰緥杩樻病鍒涘缓浜嬩欢锛屽拷鐣?            }

            Shutdown();
            return;
        }

        // 2. 鍒涘缓鏄剧ず绐楀彛浜嬩欢锛屼緵绗簩涓疄渚嬮€氱煡
        _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        StartShowWindowListener();

        // 3. 娉ㄥ唽 WpfMarkdownViewer 鎻掍欢锛堣娉曢珮浜€佹暟瀛︺€丼VG銆丮ermaid锛?        DefaultCapabilities.RegisterAll();

        // 4. 鍚姩 Host
        _host.Start();

        _host.Services.GetRequiredService<ClipboardHistoryService>().Start();

        // 4. 鏁版嵁搴撳垵濮嬪寲涓庡畬鏁存€ф鏌?        _host.Services.GetRequiredService<DatabaseInitializer>().Initialize();

        Log.Information("Luna 宸插惎鍔?);

        // 4.1 鍏ㄥ眬鍚敤杈撳叆妗嗚嚜瀹氫箟鍙抽敭鑿滃崟锛圱extBox / PasswordBox 鍦ㄥ姞杞芥椂鑷姩鎸傝浇锛?        EditorContextMenu.EnableAutoAttach();

        // 5. 搴旂敤瀛椾綋璁剧疆
        var settings = _host.Services.GetRequiredService<AiSettings>();
        ApplyCulture(settings.UiLanguage);
        RefreshTrayText();
        LocalizationService.Instance.PropertyChanged += (_, _) => RefreshTrayText();
        ThemeService.ApplyFont(settings.FontFamily);

        // 6. 鍒濆鍖栦富绐楀彛锛圡ainWindow锛氱揣鍑戞偓娴緭鍏ョ獥锛夛紱寮€鏈鸿嚜鍚姩鏃朵笉鏄剧ず
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        var hotkey = _host.Services.GetRequiredService<HotkeyService>();
        hotkey.Attach(mainWindow);
        hotkey.Update(settings.Hotkey);
        hotkey.Pressed += () => Dispatcher.Invoke(() =>
        {
            mainWindow.Show();
            mainWindow.ShowInternal();
            mainWindow.WindowState = WindowState.Normal;
            mainWindow.Activate();
        });

        if (!launchedAtStartup)
        {
            mainWindow.Show();
            mainWindow.ShowInternal();
        }

        // 7. 鍚姩鍚庡彴璋冨害鍣紙鎻愰啋杞绛夛紱姝ゆ椂鏁版嵁搴撲笌涓荤獥鍙ｅ潎宸插氨缁級
        _host.Services.GetRequiredService<BackgroundSchedulerService>()
            .StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        // 7.1 鍚姩鏃ヨ鏈嶅姟锛堝惎鍔ㄥ嵆妫€鏌ヤ竴娆?+ 姣忓ぉ 4:00 鍚庤疆璇級
        _host.Services.GetRequiredService<DiaryService>().Start();

        // 7.2 棰勫姞杞芥棩璁版湰鍐呭锛岄伩鍏嶉娆℃墦寮€鏃ヨ椤垫椂鎵嶅姞杞藉鑷寸殑鍗￠】
        _ = _host.Services.GetRequiredService<DiaryViewModel>().LoadAsync();

        // 8. 閰嶇疆鏂囦欢涓㈠け鎴栨崯鍧忔椂锛屽凡鑷姩閲嶇疆锛屾彁绀虹敤鎴?        if (_host.Services.GetRequiredService<SettingsService>().LastLoadWasReset)
            ShowSettingsResetDialog(mainWindow);

        base.OnStartup(e);
    }

    /// <summary>搴旂敤鐣岄潰璇█锛氬垏鎹㈡湰鍦板寲鏈嶅姟銆乁I Culture 涓?WPF 鐨?XmlLanguage锛堝奖鍝?XAML 鍐呯殑鏍煎紡鍖栵級銆?/summary>
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

    /// <summary>鍒锋柊鎵樼洏鑿滃崟鐨勬湰鍦板寲鏂囨湰銆?/summary>
    private void RefreshTrayText()
    {
        var loc = LocalizationService.Instance;
        if (_trayOpenItem is not null) _trayOpenItem.Header = loc["App_Tray_OpenMain"];
        if (_trayCloseItem is not null) _trayCloseItem.Header = loc["App_Tray_Close"];
    }

    /// <summary>閰嶇疆鏂囦欢涓㈠け鎴栫粨鏋勬崯鍧忓苟宸查噸缃椂寮瑰嚭鐨勬彁绀恒€?/summary>
    private static void ShowSettingsResetDialog(Window owner)
    {
        var loc = LocalizationService.Instance;
        var dialog = new LunaDialog
        {
            Owner = owner,
            DialogTitle = loc["Dialog_SettingsReset_Title"],
            DialogContent = new TextBlock
            {
                Text = loc["Dialog_SettingsReset_Body"],
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

    /// <summary>閲嶅惎褰撳墠绋嬪簭锛氶噴鏀惧崟瀹炰緥閿佸苟鍚姩鏂扮殑杩涚▼銆?/summary>
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
            // 蹇界暐鏈寔鏈夌殑鎯呭喌
        }
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;

        if (!string.IsNullOrEmpty(exe))
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 鎻愭潈杈呭姪杩涚▼鏈垵濮嬪寲鏃ュ織銆佸涓讳笌鍗曞疄渚嬭祫婧愶紝鐩存帴閫€鍑恒€?        if (_isStartupHelper)
        {
            base.OnExit(e);
            return;
        }

        Log.Information("Luna 姝ｅ湪閫€鍑?);

        // 閲婃斁鍗曞疄渚嬬浉鍏宠祫婧?        _showWindowEvent?.Dispose();
        if (!_restarting)
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }

        // 鍋滄鍚庡彴璋冨害鍣紙鎵嬪姩鍚姩锛孒ost 涓嶄細鑷姩绠＄悊锛?        try
        {
            using var schedulerStopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            _host.Services.GetRequiredService<BackgroundSchedulerService>()
                .StopAsync(schedulerStopCts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "鍋滄鍚庡彴璋冨害鍣ㄥけ璐?);
        }

        // 鍋滄骞堕噴鏀?Host
        _host.StopAsync().GetAwaiter().GetResult();
        _host.Dispose();

        // 鍏抽棴鏃ュ織骞跺埛鏂扮紦鍐插尯
        Log.CloseAndFlush();

        base.OnExit(e);
    }
    
}
