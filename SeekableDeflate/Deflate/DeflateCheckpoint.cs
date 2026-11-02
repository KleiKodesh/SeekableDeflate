using System.IO.Compression;

namespace SeekableDeflate;

/// <summary>
/// Everything needed to resume inflating a raw deflate stream at one block boundary (the zran
/// "access point"): where the next block starts in the compressed input (byte + leftover bits of
/// the previous byte, because blocks are not byte aligned), how many bytes were produced before it,
/// and the last 32 KB of output that later back-references may copy from.
///
/// The window is kept in one of three forms. Freshly captured it is raw (<see cref="Window"/>). Once the
/// inflater has read 32 KB past it, it <see cref="Seal"/>s it: the window bytes no match of the following
/// 32 KB copied are zeroed (nothing further away can reach back past the checkpoint) and the result is
/// deflated — a sparse window is ~17x smaller (measured 2.7 MB → 156 KB over 83 checkpoints). A checkpoint read
/// back from a sidecar takes its window from a shared <see cref="DeflateWindowStore"/> (all sparse windows of
/// a part compressed together), by index.
/// </summary>
public sealed class DeflateCheckpoint
{
    public const int WindowSize = 32768;

    private byte[]? _window;              // raw form
    private byte[]? _compressedWindow;    // sealed form (deflate of the raw/sparse window)
    private DeflateWindowStore? _store;   // stored form: window number _storeIndex of a shared store
    private int _storeIndex;

    /// <summary>Position (relative to the deflate data start) of the first byte not yet fully consumed.</summary>
    public long CompressedBytePosition { get; }

    /// <summary>Number of still-unused low bits of the byte before <see cref="CompressedBytePosition"/> (0..7).</summary>
    public int LeftoverBitCount { get; }

    /// <summary>The value of those leftover bits, LSB first (the deflate bit order).</summary>
    public int LeftoverBits { get; }

    /// <summary>Bytes of output produced before this block.</summary>
    public long UncompressedOffset { get; }

    /// <summary>min(32 KB, UncompressedOffset): how many output bytes the window holds.</summary>
    public int WindowLength { get; }

    /// <summary>
    /// How many window bytes the following span actually copies from: 0 = a history-free boundary (the window
    /// can be dropped altogether), -1 = not known (raw window not yet sealed, or sealed whole).
    /// </summary>
    public int UsedWindowBytes { get; private set; } = -1;

    /// <summary>True once the window is stored deflated (sparse or full) or lives in a window store.</summary>
    public bool IsSealed => _compressedWindow is not null || _store is not null;

    /// <summary>
    /// The last <see cref="WindowLength"/> output bytes, oldest first. For a sealed checkpoint this inflates a fresh copy
    /// each call (bytes a later span never referenced read as zero) — resume paths use <see cref="InflateWindowInto"/>.
    /// </summary>
    public byte[] Window
    {
        get
        {
            if (_window is not null) return _window;
            var window = new byte[WindowLength];
            InflateWindowInto(window);
            return window;
        }
    }

    /// <summary>The deflated window (sealing the raw window whole if it was not sealed yet) — what the per-checkpoint sidecar layout stores.</summary>
    public byte[] CompressedWindow
    {
        get
        {
            if (_compressedWindow is null)
            {
                if (_store is not null) { _window = Window; _store = null; }
                Seal(null);
            }
            return _compressedWindow!;
        }
    }

    public DeflateCheckpoint(long compressedBytePosition, int leftoverBitCount, int leftoverBits,
                             long uncompressedOffset, byte[] window)
    {
        CompressedBytePosition = compressedBytePosition;
        LeftoverBitCount = leftoverBitCount;
        LeftoverBits = leftoverBits;
        UncompressedOffset = uncompressedOffset;
        WindowLength = window.Length;
        _window = window;
    }

    /// <summary>A checkpoint read back from storage: the window as <see cref="CompressedWindow"/> wrote it.</summary>
    public DeflateCheckpoint(long compressedBytePosition, int leftoverBitCount, int leftoverBits,
                             long uncompressedOffset, int windowLength, byte[] compressedWindow)
    {
        if (windowLength < 0 || windowLength > WindowSize) throw new ArgumentOutOfRangeException(nameof(windowLength));
        CompressedBytePosition = compressedBytePosition;
        LeftoverBitCount = leftoverBitCount;
        LeftoverBits = leftoverBits;
        UncompressedOffset = uncompressedOffset;
        WindowLength = windowLength;
        _compressedWindow = compressedWindow;
    }

    /// <summary>A checkpoint read back from a compact table: its window is entry <paramref name="storeIndex"/> of <paramref name="store"/>.</summary>
    public DeflateCheckpoint(long compressedBytePosition, int leftoverBitCount, int leftoverBits,
                             long uncompressedOffset, int windowLength, DeflateWindowStore store, int storeIndex, int usedWindowBytes)
    {
        if (windowLength < 0 || windowLength > WindowSize) throw new ArgumentOutOfRangeException(nameof(windowLength));
        CompressedBytePosition = compressedBytePosition;
        LeftoverBitCount = leftoverBitCount;
        LeftoverBits = leftoverBits;
        UncompressedOffset = uncompressedOffset;
        WindowLength = windowLength;
        _store = store;
        _storeIndex = storeIndex;
        UsedWindowBytes = usedWindowBytes;
    }

    /// <summary>Writes the window into <paramref name="destination"/>[0..WindowLength) (inflating a sealed one).</summary>
    public void InflateWindowInto(byte[] destination)
    {
        if (_window is not null) { Buffer.BlockCopy(_window, 0, destination, 0, WindowLength); return; }
        if (WindowLength == 0) return;
        if (_store is not null) { _store.CopyWindow(_storeIndex, destination, WindowLength); return; }
        using var inflate = new DeflateStream(new MemoryStream(_compressedWindow!, writable: false), CompressionMode.Decompress);
        int filled = 0;
        while (filled < WindowLength)
        {
            int read = inflate.Read(destination, filled, WindowLength - filled);
            if (read <= 0) throw new InvalidDataException("Deflate checkpoint: window blob shorter than its length.");
            filled += read;
        }
    }

    /// <summary>
    /// Replaces the raw window by its deflated form. <paramref name="referencedBytes"/> (one flag per window byte, or null
    /// for "all") says which bytes a match of the following span copied from — the others are zeroed first. Safe only
    /// once the inflater has produced 32 KB past the checkpoint (or reached the end of the stream): no later match can
    /// reach back past it.
    /// </summary>
    internal void Seal(bool[]? referencedBytes)
    {
        if (_compressedWindow is not null) return;
        byte[] window = _window!;
        if (referencedBytes is not null)
        {
            window = (byte[])window.Clone();
            int used = 0;
            for (int i = 0; i < window.Length; i++) { if (!referencedBytes[i]) window[i] = 0; else used++; }
            UsedWindowBytes = used;
        }
        var buffer = new MemoryStream();
        using (var deflate = new DeflateStream(buffer, CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(window, 0, window.Length);
        _compressedWindow = buffer.ToArray();
        _window = null;
    }
}
