using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LocalTranslate.UI;

namespace LocalTranslate;

public partial class App : Application
{
    Mutex? singleInstance;
    Controller? controller;
    Tray? tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var settings = Settings.Load();
        Loc.Apply(settings.Language);

        singleInstance = new Mutex(true, "LocalTranslate.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show(Loc.T("app.running"), "LocalTranslate");
            Shutdown();
            return;
        }

        // A tray app must not die on an unexpected error in one lookup.
        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            args.Handled = true;
        };

        controller = new Controller(settings);
        tray = new Tray(controller);
        controller.Problem += tray.Notify;
        controller.Problem += (title, message) => Log(new Exception($"{title}: {message}"));
        Background(controller.EnsureServerAsync());
        if (e.Args.Contains("--settings")) controller.ShowSettings();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        controller?.Dispose();
        tray?.Dispose();
        singleInstance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Runs a task in the background; if it fails, the exception is logged instead of vanishing.</summary>
    internal static void Background(Task task) =>
        task.ContinueWith(t => Log(t.Exception!.GetBaseException()), TaskContinuationOptions.OnlyOnFaulted);

    internal static void Log(Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LocalTranslate");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }
}
