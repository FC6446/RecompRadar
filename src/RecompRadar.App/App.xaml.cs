using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RecompRadar.App.Services;
using RecompRadar.App.ViewModels;
using RecompRadar.Core;
using RecompRadar.Infrastructure;
using RecompRadar.Portability;
using RecompRadar.Portability.Export;

namespace RecompRadar.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private ILogger<App>? _logger;
    private AppPaths? _paths;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Make XAML StringFormat bindings use the user's regional settings instead of en-US.
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(System.Windows.Markup.XmlLanguage.GetLanguage(System.Globalization.CultureInfo.CurrentCulture.IetfLanguageTag)));

        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger?.LogError(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        try
        {
            _paths = AppPaths.Default();
            _paths.EnsureCreated();
            _services = BuildServices(_paths);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"RecompRadar could not prepare its data folder and cannot start.\n\n{ex.Message}\n\nSet the environment variable {AppPaths.DataDirectoryVariable} to a folder you can write to and try again.",
                "RecompRadar", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _logger = _services.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("RecompRadar {Version} started on {Os}", ToolInfo.Version, Environment.OSVersion);

        var main = _services.GetRequiredService<MainViewModel>();
        var window = new MainWindow(main, _services.GetRequiredService<SettingsService>());
        MainWindow = window;
        window.Show();

        HandleCommandLine(e.Args, main);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("RecompRadar exiting");
        _services?.Dispose();
        base.OnExit(e);
    }

    private static ServiceProvider BuildServices(AppPaths paths)
    {
        var services = new ServiceCollection();
        services.AddSingleton(paths);
        services.AddSingleton(LoggingSetup.CreateLoggerFactory(paths));
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        services.AddSingleton<ISettingsStore>(sp => new JsonSettingsStore(paths.Settings, sp.GetRequiredService<ILogger<JsonSettingsStore>>()));
        services.AddSingleton<SettingsService>();
        services.AddSingleton<IProjectStore>(sp => new SqliteProjectStore(paths.Database, sp.GetRequiredService<ILogger<SqliteProjectStore>>()));
        services.AddSingleton<IAnalysisPipeline, AnalysisPipeline>();
        services.AddSingleton<ReportExportService>();

        services.AddSingleton<IFileDialogService, WpfFileDialogService>();
        services.AddSingleton<IDialogService, WpfDialogService>();
        services.AddSingleton<IShellService, ShellService>();
        services.AddSingleton<NotificationService>();
        services.AddSingleton<AnalysisSession>();

        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<BinaryAnalyzerViewModel>();
        services.AddSingleton<ArchitectureViewModel>();
        services.AddSingleton<DependenciesViewModel>();
        services.AddSingleton<ReportViewModel>();
        services.AddSingleton<ProjectsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainViewModel>();
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Optional arguments: a file path to analyze right away, and --page=&lt;key&gt; to start on a page
    /// (dashboard, analyzer, architecture, dependencies, report, projects, settings).
    /// The file is only analyzed statically; it is never started.
    /// </summary>
    private void HandleCommandLine(string[] args, MainViewModel main)
    {
        string? file = null;
        string? page = null;
        foreach (var arg in args)
        {
            if (arg.StartsWith("--page=", StringComparison.OrdinalIgnoreCase))
            {
                page = arg["--page=".Length..];
            }
            else if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                file = arg;
            }
        }

        if (file is not null)
        {
            var path = Path.GetFullPath(file);
            _ = Dispatcher.InvokeAsync(async () =>
            {
                await main.Session.AnalyzeAsync(path);
                if (page is not null)
                {
                    main.Navigate(page);
                }
            });
        }
        else if (page is not null)
        {
            main.Navigate(page);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unhandled UI exception");
        e.Handled = true;
        var logs = _paths?.Logs ?? "the log folder";
        MessageBox.Show(
            "RecompRadar ran into an unexpected problem and could not finish that action.\n\n" +
            "Your analyzed files were not modified. You can keep working; if the problem repeats, please report it together with the log files in:\n" +
            logs + "\n\nTechnical detail: " + e.Exception.Message,
            "RecompRadar", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
