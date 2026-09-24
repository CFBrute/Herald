using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Herald.Services.Engines;

public record PiperCatalogVoice(string Key, string Display, string LanguageName, long SizeBytes, string ModelUrlPath)
{
    public override string ToString() => Display;
}

/// <summary>
/// Piper: fast local voices in many languages (including Swedish). Herald downloads the
/// Piper program and each voice only when the user asks, into its own app-data folder.
/// </summary>
public class PiperEngine : ITtsEngine
{
    private const string ProgramUrl = "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip";
    private const string VoicesBaseUrl = "https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/";

    private readonly string _engineDir;
    private readonly string _voicesDir;
    private readonly string _catalogPath;

    public string Id => "piper";
    public string DisplayName => "Piper (local, many languages)";
    public string Description =>
        "Fast voices that run on this PC, in 42 languages including Swedish. Needs the Piper program (about 20 MB) plus each voice you want (usually 20-120 MB each), downloaded one at a time.";

    private string PiperExe => Path.Combine(_engineDir, "piper", "piper.exe");

    public IReadOnlyList<VoiceInfo> Voices { get; private set; } = [];
    public string DefaultVoiceId => Voices.FirstOrDefault()?.Id ?? string.Empty;

    private IReadOnlyList<string> _missingParts = [];
    public IReadOnlyList<string> MissingParts => _missingParts;
    public bool IsReady => _missingParts.Count == 0;
    public bool IsProgramInstalled => File.Exists(PiperExe);

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            _isBusy = value;
            OnPropertyChanged(nameof(IsBusy));
        }
    }

    public PiperEngine(string appDataDir)
    {
        _engineDir = Path.Combine(appDataDir, "engines", "piper");
        _voicesDir = Path.Combine(_engineDir, "voices");
        _catalogPath = Path.Combine(_engineDir, "voices.json");
        Refresh();
    }

    public void Refresh()
    {
        var catalog = ReadCachedCatalog();
        Voices = Directory.Exists(_voicesDir)
            ? Directory.GetFiles(_voicesDir, "*.onnx")
                .Select(path => Path.GetFileNameWithoutExtension(path))
                .Where(key => File.Exists(Path.Combine(_voicesDir, key + ".onnx.json")))
                .Select(key => ToVoiceInfo(key, catalog))
                .OrderBy(v => v.DisplayName)
                .ToList()
            : [];

        var missing = new List<string>();
        if (!IsProgramInstalled) missing.Add("Piper program (about 20 MB)");
        if (Voices.Count == 0) missing.Add("at least one voice");
        _missingParts = missing;

        OnPropertyChanged(nameof(Voices));
        OnPropertyChanged(nameof(MissingParts));
        OnPropertyChanged(nameof(IsReady));
    }

    public async Task<bool> SynthesizeAsync(string text, string voiceId, double speed, string outPath, CancellationToken ct)
    {
        if (!IsProgramInstalled) return false;
        var model = Path.Combine(_voicesDir, voiceId + ".onnx");
        if (!File.Exists(model)) return false;

        try
        {
            var psi = new ProcessStartInfo(PiperExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                WorkingDirectory = Path.GetDirectoryName(PiperExe)!
            };
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(model);
            psi.ArgumentList.Add("--output_file");
            psi.ArgumentList.Add(outPath);
            // Piper's length_scale is duration: smaller is faster.
            psi.ArgumentList.Add("--length_scale");
            psi.ArgumentList.Add((1.0 / Math.Clamp(speed, 0.5, 3.0)).ToString("0.###", CultureInfo.InvariantCulture));

            using var process = Process.Start(psi);
            if (process == null) return false;

            // Drain output so a chatty process can't block on a full pipe.
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);

            // Piper reads one utterance per line.
            await process.StandardInput.WriteLineAsync(text.Replace('\r', ' ').Replace('\n', ' '));
            process.StandardInput.Close();

            await process.WaitForExitAsync(ct);
            await Task.WhenAll(stdout, stderr);
            return process.ExitCode == 0 && File.Exists(outPath);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Downloads the Piper program. Only runs when the user asks.</summary>
    public async Task<bool> InstallProgramAsync(IProgress<string> log, CancellationToken ct)
    {
        if (IsProgramInstalled)
        {
            log.Report("Piper program already installed.");
            return true;
        }

        Directory.CreateDirectory(_engineDir);
        var zipPath = Path.Combine(_engineDir, "piper_windows_amd64.zip");
        if (!await DownloadAsync(ProgramUrl, zipPath, "Piper program", log, ct)) return false;

        log.Report("Unpacking ...");
        var programDir = Path.Combine(_engineDir, "piper");
        if (Directory.Exists(programDir)) Directory.Delete(programDir, recursive: true);
        ZipFile.ExtractToDirectory(zipPath, _engineDir);
        File.Delete(zipPath);

        Refresh();
        if (!IsProgramInstalled)
        {
            log.Report("piper.exe was not found after unpacking.");
            return false;
        }

        log.Report("Piper program installed.");
        return true;
    }

    /// <summary>
    /// Returns the list of downloadable voices, fetching it from Hugging Face the first
    /// time (about 200 KB) and caching it in Herald's folder after that.
    /// </summary>
    public async Task<IReadOnlyList<PiperCatalogVoice>> GetCatalogAsync(IProgress<string> log, CancellationToken ct)
    {
        if (!File.Exists(_catalogPath))
        {
            Directory.CreateDirectory(_engineDir);
            if (!await DownloadAsync(VoicesBaseUrl + "voices.json", _catalogPath, "voice list", log, ct)) return [];
        }

        var catalog = ReadCachedCatalog();
        Refresh();
        return catalog.Values.OrderBy(v => v.LanguageName).ThenBy(v => v.Key).ToList();
    }

    /// <summary>Downloads one voice (model + config). Installs the program first if needed.</summary>
    public async Task<bool> InstallVoiceAsync(PiperCatalogVoice voice, IProgress<string> log, CancellationToken ct)
    {
        if (IsBusy) return false;
        IsBusy = true;
        try
        {
            if (!await InstallProgramAsync(log, ct)) return false;

            Directory.CreateDirectory(_voicesDir);
            var model = Path.Combine(_voicesDir, voice.Key + ".onnx");
            var config = model + ".json";

            if (!await DownloadAsync(VoicesBaseUrl + voice.ModelUrlPath + ".json", config, voice.Key + " config", log, ct)) return false;
            if (!File.Exists(model) && !await DownloadAsync(VoicesBaseUrl + voice.ModelUrlPath, model, voice.Key, log, ct)) return false;

            Refresh();
            log.Report($"Voice {voice.Key} is ready.");
            return true;
        }
        catch (OperationCanceledException)
        {
            log.Report("Cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            log.Report("Failed: " + ex.Message);
            return false;
        }
        finally
        {
            Refresh();
            IsBusy = false;
        }
    }

    public void RemoveVoice(string key)
    {
        foreach (var file in new[] { key + ".onnx", key + ".onnx.json" })
        {
            try { File.Delete(Path.Combine(_voicesDir, file)); } catch { }
        }
        Refresh();
    }

    private Dictionary<string, PiperCatalogVoice> ReadCachedCatalog()
    {
        var result = new Dictionary<string, PiperCatalogVoice>();
        if (!File.Exists(_catalogPath)) return result;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_catalogPath));
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                var v = entry.Value;
                var lang = v.GetProperty("language");
                var languageName = $"{lang.GetProperty("name_english").GetString()} ({lang.GetProperty("country_english").GetString()})";
                var native = lang.GetProperty("name_native").GetString();
                var name = v.GetProperty("name").GetString();
                var quality = v.GetProperty("quality").GetString();

                var modelFile = v.GetProperty("files").EnumerateObject()
                    .FirstOrDefault(f => f.Name.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase));
                if (modelFile.Value.ValueKind == JsonValueKind.Undefined) continue;
                var size = modelFile.Value.GetProperty("size_bytes").GetInt64();

                var display = $"{languageName} / {native} - {name}, {quality} ({size / 1_000_000} MB)";
                result[entry.Name] = new PiperCatalogVoice(entry.Name, display, languageName, size, modelFile.Name);
            }
        }
        catch
        {
            // unreadable cache - behave as if there's no catalog yet
        }

        return result;
    }

    private static VoiceInfo ToVoiceInfo(string key, Dictionary<string, PiperCatalogVoice> catalog)
    {
        // Keys look like "sv_SE-nst-medium": language, name, quality.
        var language = key.Split('-')[0].Replace('_', '-');
        var display = catalog.TryGetValue(key, out var entry)
            ? entry.Display[..entry.Display.LastIndexOf(" (", StringComparison.Ordinal)]
            : key;
        return new VoiceInfo(key, display, language);
    }

    private static async Task<bool> DownloadAsync(string url, string target, string label, IProgress<string> log, CancellationToken ct)
    {
        log.Report($"Downloading {label} ...");
        var partial = target + ".part";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                log.Report($"Download failed: HTTP {(int)response.StatusCode} for {url}");
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
                    if (total > 1_000_000)
                    {
                        var percent = (int)(done * 100 / total.Value);
                        if (percent / 20 != lastReported / 20)
                        {
                            lastReported = percent;
                            log.Report($"  {label}: {percent}%");
                        }
                    }
                }
            }

            File.Move(partial, target, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Report($"Download failed: {ex.Message}");
            try { File.Delete(partial); } catch { }
            return false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
