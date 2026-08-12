using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using NapcatQUI.Client.ViewModels;
using NapcatQUI.Client.Views;
using NapcatQUI.Core.Host;
using NapcatQUI.Core.Services;

namespace NapcatQUI.Client;

public partial class App : Application
{
    public static IServiceProvider? ServiceProvider { get; set; }
    private bool _shuttingDown;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ServiceProvider == null)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        try
        {
            var bgService = ServiceProvider.GetRequiredService<NapcatQUIBackgroundService>();
            _ = bgService.StartAsync(CancellationToken.None).ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                    Program.WriteCrashLog("BackgroundService.StartAsync", t.Exception);
            }, TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex)
        {
            Program.WriteCrashLog("BackgroundService resolution", ex);
        }

        try
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var viewModel = ServiceProvider.GetRequiredService<MainViewModel>();
                desktop.MainWindow = new MainWindow
                {
                    DataContext = viewModel
                };

                // 退出前停掉后台服务，干净关闭 WebSocket / SQLite
                desktop.ShutdownRequested += async (_, e) =>
                {
                    if (_shuttingDown) return;
                    _shuttingDown = true;
                    e.Cancel = true;
                    await StopCoreAsync();
                    desktop.Shutdown();
                };
            }
        }
        catch (Exception ex)
        {
            Program.WriteCrashLog("MainViewModel resolution", ex);
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = new MainWindow
                {
                    DataContext = new MainViewModel()
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task StopCoreAsync()
    {
        try
        {
            var bg = ServiceProvider?.GetService<NapcatQUIBackgroundService>();
            if (bg is not null)
                await bg.StopAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Program.WriteCrashLog("StopCoreAsync", ex);
        }
    }
}
