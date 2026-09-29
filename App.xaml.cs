using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using Herald.Services;
using Herald.Services.Engines;

namespace Herald;

public partial class App : Application
{
    private AppServices? _services;

    // Held for the app's lifetime; its existence is how a second start knows Herald already runs.
    private static Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, @"Local\Herald.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            ShowRunningInstance();
            Shutdown();
            return;
        }

        StartupRegistration.RepairPath();

        var paths = AppPaths.ForCurrentUser();

        // WPF apps die silently on an unhandled exception; leave a trace to diagnose from.
        DispatcherUnhandledException += (_, args) => LogCrash(paths.CrashLog, args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogCrash(paths.CrashLog, args.ExceptionObject as Exception);

        var log = new AppLog(paths.HeraldLog, paths.HeraldOldLog);
        log.Write("herald", $"Started {BuildInfo.Label} (built {BuildInfo.BuildTime})");

        var settings = new AppSettings(paths.AppSettingsFile);
        var engines = new EngineRegistry(paths.EnginesDir, log);
        var senders = new SenderSettingsStore(paths.SenderSettingsFile);
        var languages = new LanguageProfileStore(paths.LanguageProfilesFile);
        var speech = new SpeechEngine(paths, settings, engines, senders, languages, log: log);
        var hookServer = new HookServer(speech, settings, log: log, endpointFile: paths.EndpointFile);
        _services = new AppServices(log, paths, settings, engines, senders, languages, new HotkeySettingsStore(paths.HotkeysFile),
                                    speech, hookServer, new ClaudeCodeIntegration(paths.ClaudeHookScript));

        // Loading the Kokoro model takes a few seconds; start now so the first message doesn't wait.
        engines.Kokoro.WarmUp();
        hookServer.Start();

        // Herald keeps running in the tray when its window is closed; only Exit in the
        // tray menu (or Windows signing out) ends it.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Before any window is created: the windows' styles are based on the theme.
        ThemeManager.Apply(settings.Theme);

        var window = new MainWindow(_services);
        MainWindow = window;

        // Started with Windows: stay in the tray until the user opens it.
        if (!e.Args.Contains(StartupRegistration.MinimizedArgument)) window.Show();
    }

    /// <summary>Asks the Herald that's already running to bring its window forward.</summary>
    private static void ShowRunningInstance()
    {
        try
        {
            // This process was just started by the user, so it may hand foreground rights on.
            AllowSetForegroundWindow(-1);

            // The running Herald's port, from where it says it listens.
            var port = HookServer.ReadEndpointPort(AppPaths.ForCurrentUser().EndpointFile) ?? HookServer.DefaultPort;
            using var client = new TcpClient();
            if (!client.ConnectAsync(IPAddress.Loopback, port).Wait(1000)) return;
            var bytes = Encoding.UTF8.GetBytes("{\"type\":\"show\"}\n");
            client.GetStream().Write(bytes, 0, bytes.Length);
            client.GetStream().ReadTimeout = 2000;
            client.GetStream().ReadByte();
        }
        catch
        {
            // the other instance isn't answering - nothing more to do
        }
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    private static void LogCrash(string path, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.HookServer.Stop();
        _services?.Speech.Dispose();
        _services?.Engines.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
