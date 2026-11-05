using System.Threading;
using System.Windows;

namespace NPlayerServer;

public partial class App : System.Windows.Application
{
    private static Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, @"Local\NPlayerServer_SingleInstance", out var isFirstInstance);

        if (!isFirstInstance)
        {
            System.Windows.MessageBox.Show(
                "程序已经在运行了，请从系统托盘打开主界面。",
                "nPlayer 视频服务",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            System.Windows.MessageBox.Show(args.Exception.Message, "发生未处理的错误",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _instanceMutex?.ReleaseMutex();
        }
        catch
        {
            // 未持有互斥体时释放会抛异常，忽略
        }

        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
