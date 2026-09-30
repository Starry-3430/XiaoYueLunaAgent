using System.IO;
using System.Net.Http;
using System.Threading;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WpfMarkdownViewer;
using Microsoft.Extensions.Hosting;
using Serilog;
using Luna.Data;
using Luna.Models;
using Luna.Services;
using Luna.ViewModels;
using System.NativeTray;
using System.Windows.Interop;
using System.Windows.Media;

namespace Luna;

/// <summary>
/// 应用程序入口：负责单实例控制、托盘图标、依赖注入、日志、数据库初始化及窗口管理。
/// </summary>
public partial class App : Application
{
    private readonly IHost _host;                       // 依赖注入与后台服务宿主
    private Mutex? _singleInstanceMutex;                // 单实例互斥体
    private EventWaitHandle? _showWindowEvent;          // 显示窗口事件
    private Win32Icon? _trayIconImage;                  // 托盘图标图像
    private TrayIconHost? _trayIcon;                    // 托盘图标宿主
    private HomeWindow? _homeWindow;                    // 主窗口实例
    private CancellationTokenSource? _trayCts;          // 托盘点击延迟取消令牌，用于区分单击/双击
    private DateTime _lastTrayClickTime;                // 上次托盘点击时间，用于判断双击间隔
    private const int DoubleClickThresholdMs = 500;     // 双击判定阈值（毫秒）
    private const string MutexName = @"Global\Luna_SingleInstance_Mutex";
    private const string ShowEventName = @"Global\Luna_ShowWindow_Event";

    public App()
    {
        // 强制使用软件渲染，避免硬件加速导致的兼容性问题
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        
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
        var iconUri = new Uri("pack://application:,,,/iconStream/icon.ico");
        var streamInfo = Application.GetResourceStream(iconUri);
        using var iconStream = streamInfo.Stream;
        _trayIconImage = new Win32Icon(iconStream)
        {
            ShowAsMonochrome = true, // 单色自适应
            ThemeMode = TrayThemeMode.System // 跟随系统
        };

        _trayIcon = new TrayIconHost
        {
            IconSource = _trayIconImage,
            ToolTipText = "Luna",
            // 右击托盘图标显示菜单
            Menu = new TrayMenu
            {
                new TrayMenuItem
                {
                    Header = "主界面",
                    Command = new TrayCommand(_ => Dispatcher.Invoke(ShowHomeWindow))
                },
                new TrayMenuItem
                {
                    Header = "关闭",
                    Command = new TrayCommand(_ => Dispatcher.Invoke(Shutdown))
}
            }
        };
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
                services.AddSingleton<HomeViewModel>();
                
                // AI 服务配置
                services.AddSingleton(new AiSettings
                {
                    ApiKey = Environment.GetEnvironmentVariable("LUNA_API_KEY") ?? "",
                    BaseUrl = "https://api.deepseek.com/v1",
                    Model = "deepseek-flash",
                });
                services.AddSingleton<HttpClient>();
                services.AddSingleton<IAiService, OpenAiService>();

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
            })
            .Build();
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
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
        mainWindow.ShowInternal();
        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
    }

    /// <summary>
    /// 显示或激活主界面窗口（HomeWindow）。
    /// </summary>
    private void ShowHomeWindow()
    {
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

        // 4. 启动 Host
        _host.Start();

        // 4. 数据库初始化与完整性检查
        _host.Services.GetRequiredService<DatabaseInitializer>().Initialize();

        Log.Information("Luna 已启动");

        // 5. 显示主窗口（MainWindow：紧凑悬浮输入窗）
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
        mainWindow.ShowInternal();

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Luna 正在退出");

        // 释放单实例相关资源
        _showWindowEvent?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();

        // 停止并释放 Host
        _host.StopAsync().GetAwaiter().GetResult();
        _host.Dispose();

        // 关闭日志并刷新缓冲区
        Log.CloseAndFlush();

        base.OnExit(e);
    }
    
}
