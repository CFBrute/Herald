using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Herald.Models;
using Herald.Services.Engines.Kokoro;

namespace Herald.Services.Engines;

/// <summary>
/// Local neural voices: the Kokoro model run inside Herald (ONNX Runtime), with espeak-ng
/// for pronunciation. Nothing is installed until the user clicks Install: that downloads
/// the model files and espeak-ng into Herald's own app-data folder.
/// </summary>
public class KokoroEngine : ObservableObject, ITtsEngine, IDisposable
{
    private const string ModelFileName = "kokoro-v1.0.onnx";
    private const string VoicesFileName = "voices-v1.0.bin";
    private const string ReleaseBaseUrl = "https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/";

    // espeak-ng exactly as the Python version used it: the espeakng-loader 0.2.4 package,
    // which is a plain zip holding espeak-ng.dll and its data. Checked against its hash.
    private const string EspeakPackageUrl =
        "https://files.pythonhosted.org/packages/9d/ed/a3d872fbad4f3a3f3db0e8c31768ab14e77cd77306de16b8b20b1e1df7ea/espeakng_loader-0.2.4-py3-none-win_amd64.whl";
    private const string EspeakPackageSha256 = "41f1e08ac9deda2efd1ea9de0b81dab9f5ae3c4b24284f76533d0a7b1dd7abd7";

    private readonly string _engineDir;
    private readonly string _modelDir;
    private readonly string _espeakDir;
    private readonly AppLog? _log;
    // Guards the model: loading it, counting the syntheses using it, and freeing it only
    // once none is (freeing it mid-synthesis would crash Herald on exit).
    private readonly Lock _loadLock = new();
    private KokoroSynthesizer? _synthesizer;
    private string? _loadError;
    private int _running;
    private bool _disposed;

    public string Id => "kokoro";
    public string DisplayName => "Kokoro (local neural voices)";
    public string Description =>
        "Natural-sounding voices that run on this PC. Needs model files (about 350 MB) and espeak-ng for pronunciation (about 20 MB). English, Spanish, French, Italian, Portuguese, Hindi, Japanese and Chinese; no Swedish.";

    private string ModelPath => Path.Combine(_modelDir, ModelFileName);
    private string VoicesPath => Path.Combine(_modelDir, VoicesFileName);

    /// <summary>The Python environment earlier versions of Herald ran Kokoro in; only its espeak-ng is still of use.</summary>
    public string OldPythonDir => Path.Combine(_engineDir, "venv");
    private string OldPythonEspeakDir => Path.Combine(OldPythonDir, "Lib", "site-packages", "espeakng_loader");

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

    public KokoroEngine(string enginesDir, AppLog? log = null)
    {
        _log = log;
        _engineDir = Path.Combine(enginesDir, "kokoro");
        _modelDir = Path.Combine(_engineDir, "models");
        _espeakDir = Path.Combine(_engineDir, "espeak");
        AdoptOldEspeak();
        Refresh();
    }

    /// <summary>
    /// An earlier Herald installed espeak-ng inside its Python environment: copy it over
    /// once, so Kokoro keeps working after the update without installing anything.
    /// </summary>
    private void AdoptOldEspeak()
    {
        if (EspeakNg.IsInstalledIn(_espeakDir) || !EspeakNg.IsInstalledIn(OldPythonEspeakDir)) return;
        try
        {
            CopyEspeak(OldPythonEspeakDir, _espeakDir);
            _log?.Write("kokoro", $"Took espeak-ng over from the old Python environment ({OldPythonEspeakDir})");
        }
        catch (Exception ex)
        {
            _log?.Write("kokoro", "Couldn't take espeak-ng over from the old Python environment", ex);
        }
    }

    private static void CopyEspeak(string from, string to)
    {
        var partial = to + ".part";
        if (Directory.Exists(partial)) Directory.Delete(partial, recursive: true);
        Directory.CreateDirectory(partial);
        File.Copy(Path.Combine(from, "espeak-ng.dll"), Path.Combine(partial, "espeak-ng.dll"));
        CopyFolder(Path.Combine(from, "espeak-ng-data"), Path.Combine(partial, "espeak-ng-data"));
        Directory.Move(partial, to);
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var folder in Directory.GetDirectories(from)) CopyFolder(folder, Path.Combine(to, Path.GetFileName(folder)));
    }

    /// <summary>Checks the files again; a model that failed to load gets another try.</summary>
    public void Refresh()
    {
        lock (_loadLock) _loadError = null;
        UpdateMissingParts();
    }

    private void UpdateMissingParts()
    {
        var missing = new List<string>();
        if (!File.Exists(ModelPath)) missing.Add($"Model file {ModelFileName} (about 325 MB)");
        if (!File.Exists(VoicesPath)) missing.Add($"Voice file {VoicesFileName} (about 28 MB)");
        if (!EspeakNg.IsInstalledIn(_espeakDir)) missing.Add("espeak-ng (about 20 MB)");
        // Not ready, so messages fall back to the Windows voice instead of failing one by one.
        if (_loadError is { } error) missing.Add($"A working model: it couldn't be loaded ({error}); Install again, or see the Herald log");

        _missingParts = missing;
        OnPropertyChanged(nameof(MissingParts));
        OnPropertyChanged(nameof(IsReady));
    }

    /// <summary>Loads the model in the background, so the first message doesn't wait for it.</summary>
    public void WarmUp()
    {
        if (IsReady) Task.Run(() => Synthesizer());
    }

    /// <summary>
    /// The loaded model, loading it the first time (a few seconds). Null if that fails;
    /// then Kokoro reports why and counts as not ready until <see cref="Refresh"/>.
    /// </summary>
    private KokoroSynthesizer? Synthesizer()
    {
        bool failed;
        lock (_loadLock)
        {
            if (_synthesizer != null || _disposed || _loadError != null) return _synthesizer;
            try
            {
                var espeak = EspeakNg.Open(Path.Combine(_espeakDir, "espeak-ng.dll"), Path.Combine(_espeakDir, "espeak-ng-data"));
                var phonemizer = new KokoroPhonemizer(espeak.TextToPhonemes, KokoroVocabulary.Tokens);
                _synthesizer = new KokoroSynthesizer(ModelPath, VoicesPath, phonemizer, KokoroVocabulary.Tokens);
                _log?.Write("kokoro", "Model loaded");
                return _synthesizer;
            }
            catch (Exception ex)
            {
                _loadError = ex.Message;
                _log?.Write("kokoro", "Couldn't load the Kokoro model", ex);
                failed = true;
            }
        }
        if (failed) UpdateMissingParts();
        return null;
    }

    public Task<bool> SynthesizeAsync(string text, string voiceId, double speed, string outPath, CancellationToken ct)
    {
        if (!IsReady || ct.IsCancellationRequested) return Task.FromResult(false);

        // Not given the token: a cancelled synthesis says "not spoken" instead of throwing.
        return Task.Run(() =>
        {
            if (ct.IsCancellationRequested || Synthesizer() is not { } kokoro) return false;
            lock (_loadLock)
            {
                if (_disposed) return false;
                _running++;
            }
            try
            {
                KokoroAudio.WriteWav(outPath, kokoro.Speak(text, voiceId, LanguageCodeFor(voiceId), speed));
                return true;
            }
            catch (Exception ex)
            {
                _log?.Write("kokoro", $"Couldn't speak {AppLog.Excerpt(text)} with voice {voiceId}", ex);
                return false;
            }
            finally
            {
                lock (_loadLock)
                {
                    // The last synthesis after Dispose frees the model.
                    if (--_running == 0 && _disposed) _synthesizer?.Dispose();
                }
            }
        });
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
            Directory.CreateDirectory(_modelDir);

            foreach (var fileName in new[] { ModelFileName, VoicesFileName })
            {
                if (!await EnsureModelFileAsync(fileName, log, ct)) return false;
            }
            if (!await EnsureEspeakAsync(log, ct)) return false;

            Refresh();
            if (!IsReady)
            {
                log.Report("Still missing: " + String.Join(", ", MissingParts));
                return false;
            }

            log.Report("Kokoro is installed. Loading the model ...");
            if (await Task.Run(Synthesizer, ct) == null)
            {
                log.Report("The model didn't load; the Herald log says why.");
                return false;
            }
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
            _log?.Write("kokoro", "Installation failed", ex);
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

    private async Task<bool> EnsureEspeakAsync(IProgress<string> log, CancellationToken ct)
    {
        if (EspeakNg.IsInstalledIn(_espeakDir))
        {
            log.Report("espeak-ng already present.");
            return true;
        }
        if (EspeakNg.IsInstalledIn(OldPythonEspeakDir))
        {
            log.Report("Copying espeak-ng from the old Python environment ...");
            CopyEspeak(OldPythonEspeakDir, _espeakDir);
            return true;
        }

        Directory.CreateDirectory(_engineDir);
        var package = Path.Combine(_engineDir, "espeakng_loader.zip");
        if (!await Downloader.DownloadAsync(EspeakPackageUrl, package, "espeak-ng", log, ct)) return false;
        try
        {
            string hash;
            await using (var file = File.OpenRead(package)) hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
            if (hash != EspeakPackageSha256)
            {
                log.Report("The espeak-ng download isn't the expected file (its checksum differs); not installing it.");
                _log?.Write("kokoro", $"espeak-ng download has SHA-256 {hash}, expected {EspeakPackageSha256}");
                return false;
            }

            log.Report("Unpacking espeak-ng ...");
            var unpacked = _espeakDir + ".unpacked";
            if (Directory.Exists(unpacked)) Directory.Delete(unpacked, recursive: true);
            ZipFile.ExtractToDirectory(package, unpacked);
            CopyEspeak(Path.Combine(unpacked, "espeakng_loader"), _espeakDir);
            Directory.Delete(unpacked, recursive: true);
            return true;
        }
        finally
        {
            File.Delete(package);
        }
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
                var display = Char.ToUpper(id[3]) + id[4..];
                return new VoiceInfo(id, $"{display} ({name}, {gender})", code);
            })
        ];
    }

    /// <summary>Frees the model now, or when the synthesis still using it finishes.</summary>
    public void Dispose()
    {
        lock (_loadLock)
        {
            if (_disposed) return;
            _disposed = true;
            if (_running == 0) _synthesizer?.Dispose();
        }
    }
}
