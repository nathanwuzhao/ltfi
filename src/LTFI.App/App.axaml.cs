using System;
using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using LTFI.Infrastructure;
using LTFI.Infrastructure.Persistence;
using LTFI.ViewModels;
using LTFI.Views;

namespace LTFI;

public partial class App : Application
{
    private ServiceProvider? _services;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        ConfigureLogging();

        try
        {
            _services = BuildServiceProvider();
            _services.MigrateLtfiDatabase();
            Log.Information("LTFI started; database ready at {Path}", DbPaths.DatabaseFilePath);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed");
            throw;
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = _services.GetRequiredService<MainWindowViewModel>()
            };

            desktop.ShutdownRequested += (_, _) =>
            {
                _services?.Dispose();
                Log.CloseAndFlush();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ConfigureLogging()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(DbPaths.LogDirectory, "ltfi-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .CreateLogger();
    }

    private static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();

        services.AddLogging(builder => builder.AddSerilog(dispose: false));
        services.AddLtfiInfrastructure();

        services.AddSingleton<TodayViewModel>();
        services.AddSingleton<ProjectsViewModel>();
        services.AddSingleton<TasksViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }
}
