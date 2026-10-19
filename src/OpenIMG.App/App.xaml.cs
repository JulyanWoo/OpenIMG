#if OPENIMG_ENGINEERING
using System.IO;
#endif
using System.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenIMG.App.Browser;
using OpenIMG.App.Composition;
using OpenIMG.App.ViewModels;
using OpenIMG.Application.Abstractions.AI;
using OpenIMG.Application.Abstractions.Orchestration;
using OpenIMG.Application.Abstractions.Persistence;
using OpenIMG.Application.Abstractions.Storage;
using OpenIMG.Application.Abstractions.Validation;
#if OPENIMG_ENGINEERING
using OpenIMG.Application.Calibration;
#endif
using OpenIMG.Application.Configuration;
using OpenIMG.Application.Orchestration;
using OpenIMG.Application.Validation;
using OpenIMG.Infrastructure.Configuration;
using OpenIMG.Infrastructure.ImageProcessing;
using OpenIMG.Infrastructure.Logging;
using OpenIMG.Infrastructure.Persistence;
using OpenIMG.Infrastructure.Storage;
using Serilog;

using OpenIMG.Application.Abstractions.Analysis;
using OpenIMG.Application.Analysis;
using OpenIMG.Infrastructure.Analysis;

namespace OpenIMG.App;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = SettingsService.LoadOrCreateSettings();
#if !OPENIMG_ENGINEERING
        // A stale local setting must not re-enable private engineering hooks
        // in the public product build.
        options.EnableCalibrationFeatures = false;
        var requestedEngineeringCommand = e.Args.FirstOrDefault(IsEngineeringCommand);
        if (requestedEngineeringCommand != null)
        {
            MessageBox.Show(
                "This command is available only in an engineering build of OpenIMG.",
                "OpenIMG",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Environment.ExitCode = 2;
            Shutdown();
            return;
        }
#endif
        SerilogConfiguration.ConfigureLogging(options.LogsDirectory, options.LogLevel);

#if OPENIMG_ENGINEERING
        var isHeadlessCommand = e.Args.Contains("--phase4c2-historical-revalidation");
        var requestedEngineeringCommand = e.Args.FirstOrDefault(argument =>
            argument.StartsWith("--phase4", StringComparison.OrdinalIgnoreCase) ||
            argument.StartsWith("--gpt-", StringComparison.OrdinalIgnoreCase));
        var unsupportedProductCommand = !options.EnableCalibrationFeatures
            ? requestedEngineeringCommand
            : e.Args.FirstOrDefault(argument => string.Equals(argument, "--phase4e2-live-gemini", StringComparison.OrdinalIgnoreCase));
        if (unsupportedProductCommand != null)
        {
            Log.Warning("Calibration/provider spike command {Command} is disabled in the GPT-only product runtime.", unsupportedProductCommand);
            MessageBox.Show(
                "Engineering calibration commands are disabled. To enable historical engineering tools, set EnableCalibrationFeatures=true in the OpenIMG settings. The production queue remains GPT-only.",
                "OpenIMG",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Environment.ExitCode = 2;
            Shutdown();
            return;
        }

        var isGptActivationSpike = e.Args.Contains("--gpt-web-activation-spike");
#else
        const bool isHeadlessCommand = false;
#endif
        if (!isHeadlessCommand)
        {
            WebView2ProcessHelper.EnsureSingleInstance();
            WebView2ProcessHelper.TerminateLingeringWebViewProcesses(options.GptWebView2ProfilePath);
        }

        _host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddSerilog(Log.Logger, dispose: true);
            })
            .ConfigureServices((_, services) => services.AddOpenImgDesktopServices(options))
            .Build();

        await _host.StartAsync();

#if OPENIMG_ENGINEERING
        var dispatcherRoot = options.EnableCalibrationFeatures ? FindRepoRoot() : AppContext.BaseDirectory;
        var dispatcher = new AppCommandDispatcher(_host, dispatcherRoot, ShutdownFromCommandAsync);
        if (await dispatcher.TryRunHeadlessAsync(e.Args))
        {
            await _host.StopAsync();
            Log.CloseAndFlush();
            Shutdown();
            return;
        }
#endif

        // Safe database initialization
        try
        {
            var dbFactory = _host.Services.GetRequiredService<IDbContextFactory<OpenImgDbContext>>();
            await using var dbContext = await dbFactory.CreateDbContextAsync();
            await DatabaseInitializer.InitializeAndMigrateAsync(dbContext);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Failed to initialize SQLite database");
            MessageBox.Show(
                $"OpenIMG could not initialize its local database. Check that this user can write to the OpenIMG data folder and restart.\n\nDetails: {ex.Message}",
                "OpenIMG startup failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Environment.ExitCode = 1;
            await _host.StopAsync();
            Log.CloseAndFlush();
            Shutdown();
            return;
        }

#if OPENIMG_ENGINEERING
        if (isGptActivationSpike)
        {
            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.PrepareForGptActivationSpike();
            mainWindow.Show();
        }
        else
        {
            _host.Services.GetRequiredService<MainWindow>().Show();
        }
#else
        _host.Services.GetRequiredService<MainWindow>().Show();
#endif

#if OPENIMG_ENGINEERING
        if (dispatcher.TryScheduleInteractive(e.Args))
        {
            return;
        }
#endif
    }

#if OPENIMG_ENGINEERING
    private static string FindRepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "OpenIMG.slnx")))
                return current;
            var parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }

        current = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "OpenIMG.slnx")))
                return current;
            var parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }

        return Directory.GetCurrentDirectory();
    }

    private static async Task ShutdownFromCommandAsync()
    {
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            System.Windows.Application.Current.Shutdown());
    }
#endif

    private static bool IsEngineeringCommand(string argument) =>
        argument.StartsWith("--phase", StringComparison.OrdinalIgnoreCase) ||
        argument.StartsWith("--gpt-", StringComparison.OrdinalIgnoreCase);

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host != null)
        {
            try
            {
                // 1. Stop processing queue
                var queue = _host.Services.GetService<IProcessingQueue>();
                if (queue != null)
                {
                    await queue.StopAsync();
                }

                // 2. Cancel outstanding browser operations & detach
                var gptWebClient = _host.Services.GetService<GptWebClient>();
                gptWebClient?.DetachAndCancel();

                // 5. Stop Generic Host & release application resources
                await _host.StopAsync();
                _host.Dispose();
                _host = null;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error during graceful application shutdown");
            }
        }

        // Forced process termination is a last-resort recovery mechanism, only for proven OpenIMG processes
        try
        {
            var appOptions = SettingsService.LoadOrCreateSettings();
            WebView2ProcessHelper.TerminateLingeringWebViewProcesses(appOptions.GptWebView2ProfilePath);
        }
        catch { }

        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
