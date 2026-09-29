using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Herald.Services.Engines.Kokoro;

/// <summary>
/// The voices file (voices-v1.0.bin): a NumPy .npz archive with one float32 array of
/// shape (510, 1, 256) per voice - a style vector for each possible number of tokens.
/// </summary>
public sealed class KokoroVoices
{
    public const int StyleLength = 256;

    private readonly Dictionary<string, float[]> _voices;

    private KokoroVoices(Dictionary<string, float[]> voices) => _voices = voices;

    public IReadOnlyCollection<string> Names => _voices.Keys;

    public static KokoroVoices Load(string path)
    {
        var voices = new Dictionary<string, float[]>();
        using var archive = ZipFile.OpenRead(path);
        foreach (var entry in archive.Entries)
        {
            if (!entry.Name.EndsWith(".npy", StringComparison.Ordinal)) continue;
            using var stream = entry.Open();
            voices[entry.Name[..^4]] = ReadNpyFloats(stream);
        }
        return new KokoroVoices(voices);
    }

    /// <summary>
    /// The style for a batch of this many tokens: row n - 1, or the last row for longer
    /// batches (kokoro-onnx's _style_for).
    /// </summary>
    public float[] StyleFor(string voice, int tokenCount)
    {
        if (!_voices.TryGetValue(voice, out var rows)) throw new ArgumentException($"There's no Kokoro voice \"{voice}\"", nameof(voice));
        var rowCount = rows.Length / StyleLength;
        var row = Math.Min(tokenCount, rowCount) - 1;
        return rows.AsSpan(row * StyleLength, StyleLength).ToArray();
    }

    private static readonly Regex Descr = new(@"'descr':\s*'([^']*)'", RegexOptions.Compiled);

    /// <summary>A little-endian float32 .npy array, flattened.</summary>
    public static float[] ReadNpyFloats(Stream stream)
    {
        using var reader = new BinaryReader(stream);
        var magic = reader.ReadBytes(6);
        if (magic.Length != 6 || magic[0] != 0x93 || Encoding.ASCII.GetString(magic, 1, 5) != "NUMPY") throw new InvalidDataException("Not a .npy array");

        var major = reader.ReadByte();
        reader.ReadByte();
        var headerLength = major == 1 ? reader.ReadUInt16() : (int)reader.ReadUInt32();
        var header = Encoding.Latin1.GetString(reader.ReadBytes(headerLength));

        var type = Descr.Match(header).Groups[1].Value;
        if (type != "<f4") throw new InvalidDataException($"Expected float32 data, found \"{type}\"");
        if (header.Contains("'fortran_order': True", StringComparison.Ordinal)) throw new InvalidDataException("Fortran-ordered arrays aren't supported");

        var bytes = new MemoryStream();
        reader.BaseStream.CopyTo(bytes);
        var data = bytes.ToArray();
        var values = new float[data.Length / sizeof(float)];
        Buffer.BlockCopy(data, 0, values, 0, values.Length * sizeof(float));
        return values;
    }
}
