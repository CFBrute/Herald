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
    private SpeechEngine? _engine;
    private HookServer? _hookServer;
    private EngineRegistry? _engines;

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

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appDataDir = UseDataFolder(Path.Combine(localAppData, "Herald"), Path.Combine(localAppData, LegacyName));
        var historyDir = Path.Combine(appDataDir, "history");

        // WPF apps die silently on an unhandled exception; leave a trace to diagnose from.
        var crashLog = Path.Combine(appDataDir, "crash.log");
        DispatcherUnhandledException += (_, args) => LogCrash(crashLog, args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogCrash(crashLog, args.ExceptionObject as Exception);

        var senderSettings = new SenderSettingsStore(appDataDir);
        var hotkeySettings = new HotkeySettingsStore(appDataDir);
        _engines = new EngineRegistry(appDataDir);
        _engine = new SpeechEngine(_engines, historyDir, appDataDir, senderSettings, new LanguageProfileStore(appDataDir));
        _hookServer = new HookServer(_engine);
        _hookServer.Start();

        var window = new MainWindow(_engine, _hookServer, hotkeySettings, new ClaudeCodeIntegration(appDataDir));
        MainWindow = window;
        if (e.Args.Contains(StartupRegistration.MinimizedArgument)) window.WindowState = WindowState.Minimized;
        window.Show();
    }

    /// <summary>Asks the Herald that's already running to bring its window forward.</summary>
    private static void ShowRunningInstance()
    {
        try
        {
            // This process was just started by the user, so it may hand foreground rights on.
            AllowSetForegroundWindow(-1);

            using var client = new TcpClient();
            if (!client.ConnectAsync(IPAddress.Loopback, HookServer.Port).Wait(1000)) return;
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

    /// <summary>Herald's name before it was renamed; its data folder is moved over once.</summary>
    public const string LegacyName = "ClaudeSpeechService";

    /// <summary>
    /// Moves the old data folder (settings, history, installed engines) to Herald's folder
    /// the first time. If that fails, keeps using the old folder rather than starting empty.
    /// </summary>
    private static string UseDataFolder(string appDataDir, string legacyDir)
    {
        if (Directory.Exists(appDataDir) || !Directory.Exists(legacyDir)) return appDataDir;

        try
        {
            // A Kokoro server still running from the old folder would keep its files locked.
            foreach (var process in System.Diagnostics.Process.GetProcessesByName("pythonw"))
            {
                try
                {
                    if (process.MainModule?.FileName.StartsWith(legacyDir, StringComparison.OrdinalIgnoreCase) == true) process.Kill();
                }
                catch { }
            }

            Directory.Move(legacyDir, appDataDir);
            return appDataDir;
        }
        catch
        {
            return legacyDir;
        }
    }

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
        _hookServer?.Stop();
        _engine?.Dispose();
        _engines?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
