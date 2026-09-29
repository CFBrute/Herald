using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Herald.Services.Engines.Kokoro;

/// <summary>
/// espeak-ng, used only to turn text into IPA phonemes, called the way the Python
/// phonemizer package calls it (which Kokoro was built around). espeak-ng keeps global
/// state, so everything goes through one lock.
/// </summary>
public sealed class EspeakNg
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitializeFn(int output, int bufferLength, IntPtr path, int options);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ListVoicesFn(IntPtr voiceSpec);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetVoiceByNameFn(IntPtr name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr TextToPhonemesFn(ref IntPtr text, int textMode, int phonemeMode);

    // AUDIO_OUTPUT_SYNCHRONOUS: nothing is played, espeak only translates.
    private const int OutputSynchronous = 0x02;
    private const int TextModeUtf8 = 1;
    // IPA, with '_' between the phonemes of a word (removed again in post-processing).
    private const int PhonemeModeIpa = '_' << 8 | 0x02;

    // espeak-ng's state belongs to the whole process: one lock for everything, and each
    // espeak-ng.dll started only once (starting it again while in use crashes).
    private static readonly Lock EspeakLock = new();
    private static readonly Dictionary<string, EspeakNg> Opened = new(StringComparer.OrdinalIgnoreCase);

    private readonly SetVoiceByNameFn _setVoiceByName;
    private readonly TextToPhonemesFn _textToPhonemes;
    private readonly Dictionary<string, string> _voiceByLanguage;
    private string? _currentLanguage;

    /// <summary>espeak-ng from this DLL, started the first time it's asked for.</summary>
    /// <param name="libraryPath">espeak-ng.dll</param>
    /// <param name="dataPath">Its espeak-ng-data folder.</param>
    public static EspeakNg Open(string libraryPath, string dataPath)
    {
        var key = Path.GetFullPath(libraryPath);
        lock (EspeakLock)
        {
            if (!Opened.TryGetValue(key, out var espeak))
            {
                espeak = new EspeakNg(key, dataPath);
                Opened[key] = espeak;
            }
            return espeak;
        }
    }

    private EspeakNg(string libraryPath, string dataPath)
    {
        var library = NativeLibrary.Load(libraryPath);
        T Export<T>(string name) => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

        var initialize = Export<InitializeFn>("espeak_Initialize");
        var listVoices = Export<ListVoicesFn>("espeak_ListVoices");
        _setVoiceByName = Export<SetVoiceByNameFn>("espeak_SetVoiceByName");
        _textToPhonemes = Export<TextToPhonemesFn>("espeak_TextToPhonemes");

        var path = Marshal.StringToHGlobalAnsi(dataPath);
        try
        {
            if (initialize(OutputSynchronous, 0, path, 0) <= 0) throw new InvalidOperationException($"espeak-ng couldn't start with the data in {dataPath}");
        }
        finally
        {
            Marshal.FreeHGlobal(path);
        }

        _voiceByLanguage = VoicesByLanguage(listVoices(IntPtr.Zero));
    }

    /// <summary>The espeak-ng files in a folder, if both are there.</summary>
    public static bool IsInstalledIn(string folder) =>
        File.Exists(Path.Combine(folder, "espeak-ng.dll")) && Directory.Exists(Path.Combine(folder, "espeak-ng-data"));

    /// <summary>
    /// espeak's voice for each language code: the first one listed (they're sorted by
    /// relevance), skipping mbrola voices, which need a separate program.
    /// </summary>
    private static Dictionary<string, string> VoicesByLanguage(IntPtr voices)
    {
        var byLanguage = new Dictionary<string, string>();
        for (var i = 0; ; i++)
        {
            var voice = Marshal.ReadIntPtr(voices, i * IntPtr.Size);
            if (voice == IntPtr.Zero) break;

            // espeak_VOICE: name, languages, identifier, ... "languages" starts with a priority byte.
            var languages = Marshal.ReadIntPtr(voice, IntPtr.Size);
            var identifier = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(voice, 2 * IntPtr.Size)) ?? "";
            var language = Marshal.PtrToStringUTF8(languages + 1) ?? "";

            if (identifier.Replace('\\', '/').StartsWith("mb/", StringComparison.Ordinal)) continue;
            byLanguage.TryAdd(language, identifier);
        }
        return byLanguage;
    }

    /// <summary>
    /// The IPA for one line of text, clause by clause as espeak hands it out, joined with
    /// spaces: phonemizer's EspeakWrapper.text_to_phonemes.
    /// </summary>
    public string TextToPhonemes(string text, string language)
    {
        lock (EspeakLock)
        {
            SetLanguage(language);

            var bytes = Encoding.UTF8.GetBytes(text + "\0");
            var buffer = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, buffer, bytes.Length);
                var position = buffer;
                var clauses = new List<string>();
                // espeak moves the pointer on after each clause and sets it to null at the end.
                while (position != IntPtr.Zero)
                {
                    var phonemes = Marshal.PtrToStringUTF8(_textToPhonemes(ref position, TextModeUtf8, PhonemeModeIpa));
                    if (!string.IsNullOrEmpty(phonemes)) clauses.Add(phonemes);
                }
                return string.Join(" ", clauses);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private void SetLanguage(string language)
    {
        if (language == _currentLanguage) return;
        if (!_voiceByLanguage.TryGetValue(language, out var identifier))
        {
            throw new ArgumentException($"espeak-ng has no voice for language \"{language}\"", nameof(language));
        }

        var name = Marshal.StringToCoTaskMemUTF8(identifier);
        try
        {
            if (_setVoiceByName(name) != 0) throw new InvalidOperationException($"espeak-ng couldn't load the voice for \"{language}\"");
        }
        finally
        {
            Marshal.FreeCoTaskMem(name);
        }
        _currentLanguage = language;
    }
}
