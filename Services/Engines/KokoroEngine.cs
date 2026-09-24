using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Herald.Services.Engines;

/// <summary>
/// Local neural voices via kokoro-onnx, run by a Python server that Herald starts.
/// Nothing is installed until the user clicks Install: that creates a private Python
/// environment and downloads the model files into Herald's own app-data folder.
/// </summary>
public class KokoroEngine : ITtsEngine, IDisposable
{
    private const int Port = 8767;
    private const string ModelFileName = "kokoro-v1.0.onnx";
    private const string VoicesFileName = "voices-v1.0.bin";
    private const string ReleaseBaseUrl = "https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/";

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
        private set
        {
            _isInstalling = value;
            OnPropertyChanged(nameof(IsInstalling));
        }
    }

    public IReadOnlyList<VoiceInfo> Voices { get; } = BuildVoiceList();
    public string DefaultVoiceId => "am_michael";

    public KokoroEngine(string appDataDir)
    {
        _engineDir = Path.Combine(appDataDir, "engines", "kokoro");
        _venvDir = Path.Combine(_engineDir, "venv");
        _modelDir = Path.Combine(_engineDir, "models");
        _serverScript = Path.Combine(AppContext.BaseDirectory, "Engines", "kokoro_server.py");
        Refresh();
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
        if (!File.Exists(_serverScript)) missing.Add("Herald's kokoro_server.py (reinstall Herald)");

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
                RestartServer();
                return false;
            }
            if (DateTime.UtcNow > deadline || ct.IsCancellationRequested) return false;
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
            return response != null && response.Contains("\"status\":\"ok\"") ? SynthResult.Ok : SynthResult.Failed;
        }
        catch (SocketException)
        {
            return SynthResult.NotListening;
        }
        catch
        {
            return SynthResult.Failed;
        }
    }

    private void EnsureServerStarted()
    {
        lock (_serverLock)
        {
            if (_server is { HasExited: false }) return;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = File.Exists(VenvPythonW) ? VenvPythonW : VenvPython,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add(_serverScript);
                psi.ArgumentList.Add("--model");
                psi.ArgumentList.Add(ModelPath);
                psi.ArgumentList.Add("--voices");
                psi.ArgumentList.Add(VoicesPath);
                psi.ArgumentList.Add("--port");
                psi.ArgumentList.Add(Port.ToString());
                _server = Process.Start(psi);
            }
            catch
            {
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
                if (_server is { HasExited: false }) _server.Kill();
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

            log.Report("Installing kokoro-onnx and soundfile (this can take a few minutes) ...");
            if (await RunAsync(VenvPython, ["-m", "pip", "install", "--disable-pip-version-check", "kokoro-onnx", "soundfile"], log, ct) != 0)
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

        var url = ReleaseBaseUrl + fileName;
        var partial = target + ".part";
        log.Report($"Downloading {fileName} from {url} ...");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            log.Report($"Download failed: HTTP {(int)response.StatusCode}.");
            return false;
        }

        var total = response.Content.Headers.ContentLength;
        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = File.Create(partial))
        {
            var buffer = new byte[1 << 16];
            long done = 0;
            var lastReported = -1;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0)
                {
                    var percent = (int)(done * 100 / total.Value);
                    if (percent / 10 != lastReported / 10)
                    {
                        lastReported = percent;
                        log.Report($"  {fileName}: {percent}%");
                    }
                }
            }
        }

        File.Move(partial, target, overwrite: true);
        return true;
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

        return ids.Select(id =>
        {
            var (code, name) = Languages[id[0]];
            var gender = id[1] == 'f' ? "female" : "male";
            var display = char.ToUpper(id[3]) + id[4..];
            return new VoiceInfo(id, $"{display} ({name}, {gender})", code);
        }).ToList();
    }

    public void Dispose() => StopServer();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
