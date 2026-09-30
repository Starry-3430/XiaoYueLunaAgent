using System.IO;
using System.Net.Http;
using System.Threading;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Luna.Data;
using Luna.Models;
using Luna.Services;
using Luna.ViewModels;
using Microsoft.EntityFrameworkCore;
using System.NativeTray;
using System.Windows.Interop;
using System.Windows.Media;

namespace Luna;

public partial class App : Application
{
    private readonly IHost _host;
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showWindowEvent;
    private Win32Icon? _trayIconImage;
    private TrayIconHost? _trayIcon;
    private HomeWindow? _homeWindow;
    private CancellationTokenSource? _trayCts;
    private const string MutexName = @"Global\Luna_SingleInstance_Mutex";
    private const string ShowEventName = @"Global\Luna_ShowWindow_Event";

    public App()
    {
        // 强制使用软件渲染，避免硬件加速导致的兼容性问题
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        
        // 日志
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
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        
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
        
        // 托盘图标
        var iconStream = File.OpenRead(@"C:\Users\Star_Clara\RiderProjects\Luna\Luna\iconStream\icon.ico"); // 替换为你的图标路径
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
        
        // 实现AI服务和依赖注入 + 数据库初始化
        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton<MainWindow>();
                services.AddSingleton<MainViewModel>();
                services.AddTransient<HomeWindow>();
                services.AddSingleton<HomeViewModel>();
                services.AddSingleton(new AiSettings
                {
                    ApiKey = Environment.GetEnvironmentVariable("LUNA_API_KEY") ?? "",
                    BaseUrl = "https://api.deepseek.com/v1",
                    Model = "deepseek-flash",
                });
                services.AddSingleton<HttpClient>();
                services.AddSingleton<IAiService, OpenAiService>();

                var dbDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Luna", "database");
                Directory.CreateDirectory(dbDir);
                var dbPath = Path.Combine(dbDir, "LunaData.db");
                services.AddDbContext<LunaDbContext>(options =>
                    options.UseSqlite($"Data Source={dbPath}"));
            })
            .Build();
    }

    private async void OnTrayClick()
    {
        if (_homeWindow is { IsLoaded: true })
        {
            ShowHomeWindow();
            return;
        }

        if (_trayCts is { IsCancellationRequested: false })
        {
            _trayCts.Cancel();
            ShowHomeWindow();
            return;
        }

        _trayCts = new CancellationTokenSource();
        var cts = _trayCts;

        try
        {
            await Task.Delay(180, cts.Token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
        mainWindow.ShowInternal();
        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
    }

    private void ShowHomeWindow()
    {
        if (_homeWindow is { IsLoaded: true })
        {
            _homeWindow.WindowState = WindowState.Normal;
            _homeWindow.Show();
            _homeWindow.Activate();
            return;
        }

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

    private void StartShowWindowListener()
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                _showWindowEvent!.WaitOne();

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

        // 3. 启动 Host
        _host.Start();

        using (var scope = _host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LunaDbContext>();
            db.Database.EnsureCreated();
            db.InitializeFts();

            var integrity = db.Database.SqlQueryRaw<string>("PRAGMA quick_check").FirstOrDefault();
            if (integrity != "ok")
                Log.Warning("数据库完整性检查异常: {Integrity}", integrity);
            else
                Log.Information("数据库完整性检查通过");
        }

        Log.Information("Luna 已启动");

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
        mainWindow.ShowInternal();

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Luna 正在退出");

        _showWindowEvent?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();

        _host.StopAsync().GetAwaiter().GetResult();
        _host.Dispose();

        Log.CloseAndFlush();

        base.OnExit(e);
    }
    
}
