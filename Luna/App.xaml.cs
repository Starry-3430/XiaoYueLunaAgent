using System.IO;
using System.Net.Http;
using System.Threading;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Luna.Models;
using Luna.Services;
using Luna.ViewModels;
using System.NativeTray;

namespace Luna;

public partial class App : Application
{
    private readonly IHost _host;
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showWindowEvent;
    private Win32Icon? _trayIconImage;
    private TrayIconHost? _trayIcon;
    private const string MutexName = @"Global\Luna_SingleInstance_Mutex";
    private const string ShowEventName = @"Global\Luna_ShowWindow_Event";

    public App()
    {
        // 日志位置
        
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
                    Header = "显示窗口",
                    Command = new TrayCommand(_ => Dispatcher.Invoke(() =>
                    {
                        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
                        mainWindow.Show();
                        mainWindow.WindowState = WindowState.Normal;
                        mainWindow.Activate();
                    }))
                },
                new TrayMenuItem
                {
                    Header = "关闭",
                    Command = new TrayCommand(_ => Dispatcher.Invoke(Shutdown))
                }
            }
        };
        // 订阅左键单击事件
        _trayIcon.Click += (sender, e) =>
        {
            Dispatcher.Invoke(() =>
            { var mainWindow = _host.Services.GetRequiredService<MainWindow>();
                    mainWindow.Show();
                    mainWindow.WindowState = WindowState.Normal;
                    mainWindow.Activate();
                });
            
        };

        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton<MainWindow>();
                services.AddSingleton<MainViewModel>();
                services.AddSingleton(new AiSettings
                {
                    ApiKey = Environment.GetEnvironmentVariable("LUNA_API_KEY") ?? "",
                    BaseUrl = "https://api.deepseek.com/v1",
                    Model = "deepseek-chat",
                });
                services.AddSingleton<HttpClient>();
                services.AddSingleton<IAiService, OpenAiService>();
            })
            .Build();
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
        Log.Information("Luna 已启动");

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();

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
