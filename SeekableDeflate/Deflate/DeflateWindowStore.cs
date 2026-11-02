using System.IO.Compression;

namespace SeekableDeflate;

/// <summary>
/// The sparse windows of a part's checkpoints, stored together: windows that need no bytes (history-free
/// boundaries) take no room, the others are concatenated in groups of <see cref="DefaultGroupSize"/> and each
/// group compressed as one stream — windows of one document share most of their content (the XML
/// vocabulary), so one stream over several windows is ~25 % smaller than one stream per window
/// (measured 35.7 KB vs 47.7 KB for 44 windows of a 91 MB part). A group is decoded on first use and
/// the last used group is kept (~1 ms per group).
///
/// Codec: Brotli where the runtime has it (.NET Core / 5+), Deflate on .NET Framework, which has no
/// Brotli in the BCL; the codec byte is stored. A store written with Brotli cannot be read on .NET
/// Framework (the index is rebuilt there and written with Deflate, which every runtime reads).
/// </summary>
public sealed class DeflateWindowStore
{
    public const int DefaultGroupSize = 8;
    private const byte CodecDeflate = 0;
    private const byte CodecBrotli = 1;

    private readonly byte _codec;
    private readonly int _groupSize;
    private readonly int[] _windowLengths;      // per checkpoint (0 = not stored: empty or history-free)
    private readonly byte[][] _groups;          // compressed group blobs
    private int _cachedGroup = -1;
    private byte[]? _cachedBytes;

    /// <summary>How many windows the store describes (one per checkpoint, stored or empty).</summary>
    public int Count => _windowLengths.Length;

    /// <summary>Compressed bytes of every group together.</summary>
    public long CompressedBytes
    {
        get { long total = 0; foreach (byte[] group in _groups) total += group.Length; return total; }
    }

    private DeflateWindowStore(byte codec, int groupSize, int[] windowLengths, byte[][] groups)
    {
        _codec = codec;
        _groupSize = groupSize;
        _windowLengths = windowLengths;
        _groups = groups;
    }

    /// <summary>True when window <paramref name="index"/> holds bytes (false = all zero: a history-free checkpoint).</summary>
    public bool HasBytes(int index) => _windowLengths[index] > 0;

    /// <summary>Builds the store from checkpoints in order: window i is checkpoints[i]'s (a history-free one is dropped).</summary>
    public static DeflateWindowStore Build(IReadOnlyList<DeflateCheckpoint> checkpoints, int groupSize = DefaultGroupSize)
    {
        if (groupSize <= 0) throw new ArgumentOutOfRangeException(nameof(groupSize));
        var lengths = new int[checkpoints.Count];
        var stored = new List<byte[]>();
        for (int i = 0; i < checkpoints.Count; i++)
        {
            DeflateCheckpoint checkpoint = checkpoints[i];
            if (checkpoint.WindowLength == 0 || checkpoint.UsedWindowBytes == 0) continue;
            byte[] window = checkpoint.Window;
            if (checkpoint.UsedWindowBytes < 0 && IsAllZero(window)) continue;
            lengths[i] = window.Length;
            stored.Add(window);
        }
        var groups = new List<byte[]>();
        for (int first = 0; first < stored.Count; first += groupSize)
        {
            var raw = new MemoryStream();
            for (int i = first; i < Math.Min(stored.Count, first + groupSize); i++) raw.Write(stored[i], 0, stored[i].Length);
            groups.Add(Compress(raw.ToArray()));
        }
        return new DeflateWindowStore(PreferredCodec, groupSize, lengths, groups.ToArray());
    }

    private static bool IsAllZero(byte[] bytes)
    {
        foreach (byte b in bytes) if (b != 0) return false;
        return true;
    }

    /// <summary>Copies window <paramref name="index"/> (zeros when it is not stored) into <paramref name="destination"/>[0..length).</summary>
    public void CopyWindow(int index, byte[] destination, int length)
    {
        if (_windowLengths[index] == 0) { Array.Clear(destination, 0, length); return; }
        if (_windowLengths[index] != length) throw new InvalidDataException("Window store: window length mismatch.");
        int stored = 0;
        for (int i = 0; i < index; i++) if (_windowLengths[i] > 0) stored++;
        int group = stored / _groupSize;
        int offset = 0;
        for (int i = 0, seen = 0; i < index; i++)
            if (_windowLengths[i] > 0) { if (seen >= group * _groupSize) offset += _windowLengths[i]; seen++; }
        if (_cachedGroup != group)
        {
            _cachedBytes = Decompress(_groups[group]);
            _cachedGroup = group;
        }
        if (offset + length > _cachedBytes!.Length) throw new InvalidDataException("Window store: group shorter than its windows.");
        Buffer.BlockCopy(_cachedBytes, offset, destination, 0, length);
    }

    public void Write(BinaryWriter writer)
    {
        writer.Write(_codec);
        writer.Write(_groupSize);
        writer.Write(_windowLengths.Length);
        foreach (int length in _windowLengths) WriteVarint(writer, (ulong)length);
        writer.Write(_groups.Length);
        foreach (byte[] group in _groups) { WriteVarint(writer, (ulong)group.Length); writer.Write(group); }
    }

    public static DeflateWindowStore Read(BinaryReader reader)
    {
        byte codec = reader.ReadByte();
        if (codec != CodecDeflate && codec != CodecBrotli) throw new InvalidDataException("Window store: unknown codec.");
        if (codec == CodecBrotli && !HasBrotli) throw new InvalidDataException("Window store: Brotli windows cannot be read on this runtime.");
        int groupSize = reader.ReadInt32();
        int count = reader.ReadInt32();
        if (groupSize <= 0 || count < 0) throw new InvalidDataException("Window store: bad header.");
        var lengths = new int[count];
        for (int i = 0; i < count; i++)
        {
            lengths[i] = (int)ReadVarint(reader);
            if (lengths[i] > DeflateCheckpoint.WindowSize) throw new InvalidDataException("Window store: window too long.");
        }
        var groups = new byte[reader.ReadInt32()][];
        for (int i = 0; i < groups.Length; i++)
        {
            int length = (int)ReadVarint(reader);
            groups[i] = reader.ReadBytes(length);
            if (groups[i].Length != length) throw new EndOfStreamException("Window store truncated.");
        }
        return new DeflateWindowStore(codec, groupSize, lengths, groups);
    }

    internal static void WriteVarint(BinaryWriter writer, ulong value)
    {
        while (value >= 0x80) { writer.Write((byte)(value | 0x80)); value >>= 7; }
        writer.Write((byte)value);
    }

    internal static ulong ReadVarint(BinaryReader reader)
    {
        ulong value = 0;
        int shift = 0;
        while (true)
        {
            byte b = reader.ReadByte();
            value |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80) return value;
            shift += 7;
            if (shift > 63) throw new InvalidDataException("Varint too long.");
        }
    }

#if NETFRAMEWORK
    private const bool HasBrotli = false;
    private const byte PreferredCodec = CodecDeflate;
#else
    private const bool HasBrotli = true;
    private const byte PreferredCodec = CodecBrotli;
#endif

    private static byte[] Compress(byte[] raw)
    {
        var buffer = new MemoryStream();
#if NETFRAMEWORK
        using (var deflate = new DeflateStream(buffer, CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(raw, 0, raw.Length);
#else
        using (var brotli = new BrotliStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true)) brotli.Write(raw, 0, raw.Length);
#endif
        return buffer.ToArray();
    }

    private byte[] Decompress(byte[] blob)
    {
        Stream stream;
        if (_codec == CodecDeflate) stream = new DeflateStream(new MemoryStream(blob, writable: false), CompressionMode.Decompress);
#if NETFRAMEWORK
        else throw new InvalidDataException("Window store: Brotli windows cannot be read on this runtime.");
#else
        else stream = new BrotliStream(new MemoryStream(blob, writable: false), CompressionMode.Decompress);
#endif
        using (stream)
        {
            var output = new MemoryStream();
            var chunk = new byte[1 << 16];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0) output.Write(chunk, 0, read);
            return output.ToArray();
        }
    }
}
