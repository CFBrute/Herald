using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Herald.Models;

namespace Herald.Services.Engines;

/// <summary>
/// Local neural voices via kokoro-onnx, run by a Python server that Herald starts.
/// Nothing is installed until the user clicks Install: that creates a private Python
/// environment and downloads the model files into Herald's own app-data folder.
/// </summary>
public class KokoroEngine : ObservableObject, ITtsEngine, IDisposable
{
    private const int Port = 8767;
    private const string ModelFileName = "kokoro-v1.0.onnx";
    private const string VoicesFileName = "voices-v1.0.bin";
    // Pinned to the versions kokoro_server.py was tested with: kokoro-onnx changes behaviour
    // between versions (speed limits, pauses). Their own dependencies stay unpinned, since
    // fixed versions of those may have no download for a newer Python.
    public const string KokoroOnnxPackage = "kokoro-onnx==0.6.1";
    public const string SoundfilePackage = "soundfile==0.14.0";
    private const string ReleaseBaseUrl ="https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/";

    private readonly string _engineDir;
    private readonly string _venvDir;
    private readonly string _modelDir;
    private readonly string _serverScript;
    private readonly object _serverLock = new();
    private Process? _server;

    public string Id => "kokoro";
    public string DisplayName => "Kokoro (local neural voices)";
    public string Description =>
        "Natural-sounding voices that run on this PC. Needs Python 3.10+, a private Python environment (about 150 MB) and model files (about 350 MB). English, Spanish, French, Italian, Portuguese, Hindi, Japanese and Chinese; no Swedish.";

    private string VenvPython => Path.Combine(_venvDir, "Scripts", "python.exe");
    private string VenvPythonW => Path.Combine(_venvDir, "Scripts", "pythonw.exe");
    private string ModelPath => Path.Combine(_modelDir, ModelFileName);
    private string VoicesPath => Path.Combine(_modelDir, VoicesFileName);

    private IReadOnlyList<string> _missingParts = [];
    public IReadOnlyList<string> MissingParts => _missingParts;
    public bool IsReady => _missingParts.Count == 0;

    private bool _isInstalling;
    public bool IsInstalling
    {
        get => _isInstalling;
        private set => SetField(ref _isInstalling, value);
    }

    public IReadOnlyList<VoiceInfo> Voices { get; } = BuildVoiceList();
    public string DefaultVoiceId => "am_michael";

    private readonly AppLog? _log;

    public KokoroEngine(string enginesDir, AppLog? log = null)
    {
        _log = log;
        _engineDir = Path.Combine(enginesDir, "kokoro");
        _venvDir = Path.Combine(_engineDir, "venv");
        _modelDir = Path.Combine(_engineDir, "models");
        _serverScript = Path.Combine(_engineDir, "kokoro_server.py");
        Refresh();
    }

    /// <summary>
    /// Writes the server script (embedded in Herald.exe) into the engine folder, replacing
    /// any older copy so it always matches the running Herald.
    /// </summary>
    private bool WriteServerScript()
    {
        try
        {
            using var stream = typeof(KokoroEngine).Assembly.GetManifestResourceStream("kokoro_server.py");
            if (stream == null) return false;
            Directory.CreateDirectory(_engineDir);
            using var file = File.Create(_serverScript);
            stream.CopyTo(file);
            return true;
        }
        catch (Exception ex)
        {
            _log?.Write("kokoro", $"Couldn't write the server script to {_serverScript}", ex);
            return false;
        }
    }

    public void Refresh()
    {
        var missing = new List<string>();
        if (!File.Exists(VenvPython) || !Directory.Exists(Path.Combine(_venvDir, "Lib", "site-packages", "kokoro_onnx")))
        {
            missing.Add("Python environment with kokoro-onnx");
        }
        if (!File.Exists(ModelPath)) missing.Add($"Model file {ModelFileName} (about 325 MB)");
        if (!File.Exists(VoicesPath)) missing.Add($"Voice file {VoicesFileName} (about 28 MB)");

        _missingParts = missing;
        OnPropertyChanged(nameof(MissingParts));
        OnPropertyChanged(nameof(IsReady));
    }

    public void StartServerIfReady()
    {
        if (IsReady) EnsureServerStarted();
    }

    public async Task<bool> SynthesizeAsync(string text, string voiceId, double speed, string outPath, CancellationToken ct)
    {
        if (!IsReady) return false;

        EnsureServerStarted();

        // The model takes a few seconds to load after the server starts.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            var result = await TrySynthesizeAsync(text, voiceId, speed, outPath, ct);
            if (result == SynthResult.Ok) return true;
            if (result == SynthResult.Failed) return false;
            if (result == SynthResult.TimedOut)
            {
                _log?.Write("kokoro", $"No answer within 30 s for {AppLog.Excerpt(text)}; restarting the server");
                RestartServer();
                return false;
            }
            if (ct.IsCancellationRequested) return false;
            if (DateTime.UtcNow > deadline)
            {
                _log?.Write("kokoro", $"The server didn't start listening on port {Port} within 20 s; {AppLog.Excerpt(text)} not spoken");
                return false;
            }
            await Task.Delay(250, ct);
        }
    }

    private enum SynthResult { Ok, Failed, NotListening, TimedOut }

    private async Task<SynthResult> TrySynthesizeAsync(string text, string voiceId, double speed, string outPath, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync("127.0.0.1", Port, ct).AsTask();
            if (await Task.WhenAny(connectTask, Task.Delay(300, ct)) != connectTask || !client.Connected)
            {
                return SynthResult.NotListening;
            }

            using var stream = client.GetStream();
            var request = JsonSerializer.Serialize(new
            {
                text,
                voice = voiceId,
                speed,
                lang = LanguageCodeFor(voiceId),
                @out = outPath
            });
            await stream.WriteAsync(Encoding.UTF8.GetBytes(request + "\n"), ct);

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var readTask = reader.ReadLineAsync(ct).AsTask();
            if (await Task.WhenAny(readTask, Task.Delay(30000, ct)) != readTask)
            {
                return SynthResult.TimedOut;
            }

            var response = readTask.Result;
            if (response != null && response.Contains("\"status\":\"ok\"")) return SynthResult.Ok;

            // The server's answer says why, e.g. {"status":"error","message":"..."}.
            _log?.Write("kokoro", $"Couldn't speak {AppLog.Excerpt(text)} with voice {voiceId}: {response ?? "no answer"}");
            return SynthResult.Failed;
        }
        catch (SocketException)
        {
            return SynthResult.NotListening;
        }
        catch (OperationCanceledException)
        {
            return SynthResult.Failed;
        }
        catch (Exception ex)
        {
            _log?.Write("kokoro", $"Couldn't speak {AppLog.Excerpt(text)} with voice {voiceId}", ex);
            return SynthResult.Failed;
        }
    }

    private void EnsureServerStarted()
    {
        lock (_serverLock)
        {
            if (_server is { HasExited: false }) return;
            if (!WriteServerScript()) return;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = File.Exists(VenvPythonW) ? VenvPythonW : VenvPython,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    // The server reports its progress and any Python error here.
                    RedirectStandardError = _log != null
                };
                psi.ArgumentList.Add(_serverScript);
                psi.ArgumentList.Add("--model");
                psi.ArgumentList.Add(ModelPath);
                psi.ArgumentList.Add("--voices");
                psi.ArgumentList.Add(VoicesPath);
                psi.ArgumentList.Add("--port");
                psi.ArgumentList.Add(Port.ToString());
                _server = Process.Start(psi);
                if (_server != null && _log != null)
                {
                    _server.ErrorDataReceived += (_, e) =>
                    {
                        if (!string.IsNullOrWhiteSpace(e.Data)) _log.Write("kokoro-server", e.Data);
                    };
                    _server.BeginErrorReadLine();
                }
            }
            catch (Exception ex)
            {
                _log?.Write("kokoro", "Couldn't start the Kokoro server", ex);
                _server = null;
            }
        }
    }

    private void RestartServer()
    {
        StopServer();
        EnsureServerStarted();
    }

    private void StopServer()
    {
        lock (_serverLock)
        {
            try
            {
                // The venv's pythonw.exe is a launcher that runs the real interpreter as a
                // child; killing only the launcher would leave that child running.
                if (_server is { HasExited: false }) _server.Kill(entireProcessTree: true);
            }
            catch { }
            _server = null;
        }
    }

    /// <summary>
    /// Installs everything Kokoro needs into Herald's app-data folder. Only ever
    /// runs when the user asks for it. Progress lines go to <paramref name="log"/>.
    /// </summary>
    public async Task<bool> InstallAsync(IProgress<string> log, CancellationToken ct)
    {
        if (IsInstalling) return false;
        IsInstalling = true;
        try
        {
            Directory.CreateDirectory(_engineDir);
            Directory.CreateDirectory(_modelDir);

            if (!File.Exists(VenvPython))
            {
                var basePython = await FindBasePythonAsync(log, ct);
                if (basePython == null)
                {
                    log.Report("Python 3.10 or newer was not found. Install it from python.org (or run: winget install Python.Python.3.12), then click Install again.");
                    return false;
                }

                log.Report($"Creating a private Python environment using {basePython} ...");
                if (await RunAsync(basePython, ["-m", "venv", _venvDir], log, ct) != 0 || !File.Exists(VenvPython))
                {
                    log.Report("Creating the Python environment failed.");
                    return false;
                }
            }

            log.Report($"Installing {KokoroOnnxPackage} and {SoundfilePackage} (this can take a few minutes) ...");
            if (await RunAsync(VenvPython, ["-m", "pip", "install", "--disable-pip-version-check", KokoroOnnxPackage, SoundfilePackage], log, ct) != 0)
            {
                log.Report("Installing the Python packages failed.");
                return false;
            }

            foreach (var fileName in new[] { ModelFileName, VoicesFileName })
            {
                if (!await EnsureModelFileAsync(fileName, log, ct)) return false;
            }

            Refresh();
            if (!IsReady)
            {
                log.Report("Still missing: " + string.Join(", ", MissingParts));
                return false;
            }

            log.Report("Kokoro is installed. Starting the voice server ...");
            RestartServer();
            log.Report("Done.");
            return true;
        }
        catch (OperationCanceledException)
        {
            log.Report("Installation cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            log.Report("Installation failed: " + ex.Message);
            return false;
        }
        finally
        {
            Refresh();
            IsInstalling = false;
        }
    }

    private async Task<bool> EnsureModelFileAsync(string fileName, IProgress<string> log, CancellationToken ct)
    {
        var target = Path.Combine(_modelDir, fileName);
        if (File.Exists(target))
        {
            log.Report($"{fileName} already present.");
            return true;
        }

        // Reuse a copy from an earlier manual Kokoro setup instead of downloading again.
        var existing = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "models", "kokoro", fileName);
        if (File.Exists(existing))
        {
            log.Report($"Copying existing {fileName} from {Path.GetDirectoryName(existing)} ...");
            File.Copy(existing, target);
            return true;
        }

        return await Downloader.DownloadAsync(ReleaseBaseUrl + fileName, target, fileName, log, ct);
    }

    private static async Task<string?> FindBasePythonAsync(IProgress<string> log, CancellationToken ct)
    {
        var candidates = new List<string>();

        var launcher = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "py.exe");
        if (File.Exists(launcher))
        {
            var fromLauncher = await CaptureAsync(launcher, ["-3", "-c", "import sys; print(sys.executable)"], ct);
            if (!string.IsNullOrWhiteSpace(fromLauncher)) candidates.Add(fromLauncher.Trim());
        }

        var programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python");
        if (Directory.Exists(programs))
        {
            candidates.AddRange(Directory.GetDirectories(programs, "Python3*")
                .OrderByDescending(d => d)
                .Select(d => Path.Combine(d, "python.exe"))
                .Where(File.Exists));
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            // The WindowsApps "python.exe" is only a shortcut that opens the Microsoft Store.
            if (dir.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
            var exe = Path.Combine(dir.Trim(), "python.exe");
            if (File.Exists(exe)) candidates.Add(exe);
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var ok = await CaptureAsync(candidate, ["-c", "import sys; print(sys.version_info >= (3, 10))"], ct);
            if (ok?.Trim() == "True") return candidate;
            log.Report($"Skipping {candidate} (needs Python 3.10 or newer).");
        }

        return null;
    }

    private static async Task<string?> CaptureAsync(string exe, string[] args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return null;
            var output = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return p.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<int> RunAsync(string exe, string[] args, IProgress<string> log, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log.Report("  " + e.Data); };
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log.Report("  " + e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync(ct);
        return p.ExitCode;
    }

    private static readonly Dictionary<char, (string Code, string Name)> Languages = new()
    {
        ['a'] = ("en-us", "American English"),
        ['b'] = ("en-gb", "British English"),
        ['e'] = ("es", "Spanish"),
        ['f'] = ("fr-fr", "French"),
        ['h'] = ("hi", "Hindi"),
        ['i'] = ("it", "Italian"),
        ['j'] = ("ja", "Japanese"),
        ['p'] = ("pt-br", "Brazilian Portuguese"),
        ['z'] = ("cmn", "Mandarin Chinese")
    };

    private static string LanguageCodeFor(string voiceId) =>
        voiceId.Length > 0 && Languages.TryGetValue(voiceId[0], out var lang) ? lang.Code : "en-us";

    private static IReadOnlyList<VoiceInfo> BuildVoiceList()
    {
        string[] ids =
        [
            "af_alloy", "af_aoede", "af_bella", "af_heart", "af_jessica", "af_kore", "af_nicole", "af_nova", "af_river", "af_sarah", "af_sky",
            "am_adam", "am_echo", "am_eric", "am_fenrir", "am_liam", "am_michael", "am_onyx", "am_puck", "am_santa",
            "bf_alice", "bf_emma", "bf_isabella", "bf_lily", "bm_daniel", "bm_fable", "bm_george", "bm_lewis",
            "ef_dora", "em_alex", "em_santa",
            "ff_siwis",
            "hf_alpha", "hf_beta", "hm_omega", "hm_psi",
            "if_sara", "im_nicola",
            "jf_alpha", "jf_gongitsune", "jf_nezumi", "jf_tebukuro", "jm_kumo",
            "pf_dora", "pm_alex", "pm_santa",
            "zf_xiaobei", "zf_xiaoni", "zf_xiaoxiao", "zf_xiaoyi", "zm_yunjian", "zm_yunxi", "zm_yunxia", "zm_yunyang"
        ];

        return
        [
            .. ids.Select(id =>
            {
                var (code, name) = Languages[id[0]];
                var gender = id[1] == 'f' ? "female" : "male";
                var display = char.ToUpper(id[3]) + id[4..];
                return new VoiceInfo(id, $"{display} ({name}, {gender})", code);
            })
        ];
    }

    public void Dispose() => StopServer();
}
