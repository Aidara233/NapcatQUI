using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NapcatQUI.Core.Host;

namespace NapcatQUI.Client;

sealed class Program
{
    private static string? _appDataDir;

    [STAThread]
    public static void Main(string[] args)
    {
        _appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NapcatQUI");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            WriteCrashLog("AppDomain unhandled", ex);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        try
        {
            var services = new ServiceCollection();

            services.AddLogging(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Information);
            });

            services.AddNapcatQUICore(_appDataDir);

            services.AddSingleton<ViewModels.MainViewModel>();

            var provider = services.BuildServiceProvider();

            App.ServiceProvider = provider;

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            WriteCrashLog("Main exception", ex);
            throw;
        }
    }

    public static void WriteCrashLog(string context, Exception? ex)
    {
        try
        {
            if (_appDataDir == null) return;
            var logPath = Path.Combine(_appDataDir, "crash.log");
            var sb = new StringBuilder();
            sb.AppendLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            sb.AppendLine($"Context: {context}");
            if (ex != null)
            {
                sb.AppendLine($"Exception: {ex.GetType().FullName}");
                sb.AppendLine($"Message: {ex.Message}");
                sb.AppendLine($"StackTrace: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    sb.AppendLine($"Inner: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}");
                    sb.AppendLine($"Inner StackTrace: {ex.InnerException.StackTrace}");
                }
            }
            sb.AppendLine();
            File.AppendAllText(logPath, sb.ToString());
        }
        catch { }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
