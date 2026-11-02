namespace SeekableDeflate;

/// <summary>
/// Raw-deflate (RFC 1951) inflater that pauses between symbols, so a read can stop at any output
/// offset, and that calls <see cref="BlockBoundaryReached"/> before each new block — the only
/// moments where <see cref="CaptureCheckpoint"/> yields a resumable state — and can later resume
/// from such a checkpoint via <see cref="ResumeFrom"/>. Managed code only, no zlib dependency.
/// The hot loop writes literals straight through, copies non-overlapping matches in bulk and keeps
/// the 32 KB window circular with a mask instead of a modulo.
/// </summary>
public sealed class DeflateCheckpointInflater
{
    private enum State { BlockHeader, StoredBlock, HuffmanBlock, Finished }

    private const int WindowMask = DeflateCheckpoint.WindowSize - 1;

    private static readonly ushort[] LengthBase =
        [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];
    private static readonly byte[] LengthExtraBits =
        [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
    private static readonly ushort[] DistanceBase =
        [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];
    private static readonly byte[] DistanceExtraBits =
        [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];
    private static readonly byte[] CodeLengthOrder = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

    private static readonly DeflateHuffmanDecoder FixedLiteralDecoder = BuildFixedLiteralDecoder();
    private static readonly DeflateHuffmanDecoder FixedDistanceDecoder = BuildFixedDistanceDecoder();

    private readonly DeflateBitReader _bits;
    private readonly byte[] _window = new byte[DeflateCheckpoint.WindowSize];
    private int _windowPosition;      // next write slot in the circular window
    private long _totalOut;

    private State _state = State.BlockHeader;
    private bool _isFinalBlock;
    private int _storedRemaining;
    private DeflateHuffmanDecoder? _literalDecoder;
    private DeflateHuffmanDecoder? _distanceDecoder;
    private int _pendingCopyLength;   // a match that did not fit the caller's buffer
    private int _pendingCopyDistance;

    // captured, still-raw checkpoints: which of their window bytes a match of the following 32 KB copies from.
    // Several may be open at once (candidates of one region, see DeflateCheckpointSelector); each is sealed sparse
    // once the output is 32 KB past it — nothing later can reach back over it.
    private sealed class Tracked
    {
        public DeflateCheckpoint Checkpoint = null!;
        public bool[]? Referenced;
        public long SpanStart;           // = Checkpoint.UncompressedOffset
        public long WindowStartOffset;   // uncompressed offset of the window's first byte
    }
    private readonly List<Tracked> _tracked = new();
    private DeflateCheckpoint? _lastResumedCheckpoint;
    private readonly byte[] _resumeWindow = new byte[DeflateCheckpoint.WindowSize];

    /// <summary>Raised just before a new block's header is read; <see cref="CaptureCheckpoint"/> is valid inside it.</summary>
    public event Action<DeflateCheckpointInflater>? BlockBoundaryReached;

    /// <summary>Raised when a captured checkpoint's window is sealed (sparse): 32 KB of output past it, or the end of the stream.</summary>
    public event Action<DeflateCheckpoint>? CheckpointSealed;

    public long TotalOut => _totalOut;

    /// <summary>True at the boundary after the final block: the stream is over, a checkpoint there would be useless.</summary>
    public bool IsPastFinalBlock => _isFinalBlock;
    public bool IsFinished => _state == State.Finished && _pendingCopyLength == 0;

    public DeflateCheckpointInflater(Stream input, long dataStart, long dataLength)
    {
        _bits = new DeflateBitReader(input, dataStart, dataLength);
    }

    public DeflateCheckpoint CaptureCheckpoint()
    {
        if (_state != State.BlockHeader || _pendingCopyLength != 0)
            throw new InvalidOperationException("A checkpoint can only be captured at a block boundary.");
        int windowLength = (int)Math.Min(_totalOut, DeflateCheckpoint.WindowSize);
        var window = new byte[windowLength];
        int start = (_windowPosition - windowLength) & WindowMask;
        for (int i = 0; i < windowLength; i++) window[i] = _window[(start + i) & WindowMask];
        var checkpoint = new DeflateCheckpoint(_bits.CompressedBytePosition, _bits.LeftoverBitCount, _bits.LeftoverBits,
                                               _totalOut, window);
        _tracked.Add(new Tracked
        {
            Checkpoint = checkpoint,
            Referenced = windowLength > 0 ? new bool[windowLength] : null,
            SpanStart = _totalOut,
            WindowStartOffset = _totalOut - windowLength,
        });
        if (windowLength == 0) SealTracked(0);                 // nothing to track at the very start
        return checkpoint;
    }

    /// <summary>Seals every tracked checkpoint the output has moved 32 KB past (and all of them at the end of the stream).</summary>
    private void SealFinishedTracked()
    {
        while (_tracked.Count > 0 && (_totalOut - _tracked[0].SpanStart >= DeflateCheckpoint.WindowSize || _state == State.Finished))
            SealTracked(0);
    }

    private void SealTracked(int index)
    {
        Tracked tracked = _tracked[index];
        _tracked.RemoveAt(index);
        tracked.Checkpoint.Seal(tracked.Referenced ?? new bool[0]);
        CheckpointSealed?.Invoke(tracked.Checkpoint);
    }

    /// <summary>Seals every still-raw checkpoint now, sparsely when the output is 32 KB past it, else whole (kept for callers that stop reading early).</summary>
    public void SealTrackedCheckpoint()
    {
        while (_tracked.Count > 0)
        {
            Tracked tracked = _tracked[0];
            bool spanCoversWindow = _totalOut - tracked.SpanStart >= DeflateCheckpoint.WindowSize || IsFinished;
            if (spanCoversWindow) SealTracked(0);
            else { _tracked.RemoveAt(0); tracked.Checkpoint.Seal(null); CheckpointSealed?.Invoke(tracked.Checkpoint); }
        }
    }

    public void ResumeFrom(DeflateCheckpoint checkpoint)
    {
        _bits.SeekTo(checkpoint.CompressedBytePosition, checkpoint.LeftoverBitCount, checkpoint.LeftoverBits);
        _tracked.Clear();
        if (!ReferenceEquals(checkpoint, _lastResumedCheckpoint))
        {
            checkpoint.InflateWindowInto(_resumeWindow);   // ~0.03 ms for a sealed window; cached for repeated resumes at the same checkpoint
            _lastResumedCheckpoint = checkpoint;
        }
        Array.Clear(_window, 0, _window.Length);
        Buffer.BlockCopy(_resumeWindow, 0, _window, 0, checkpoint.WindowLength);
        _windowPosition = checkpoint.WindowLength & WindowMask;
        _totalOut = checkpoint.UncompressedOffset;
        _state = State.BlockHeader;
        _isFinalBlock = false;
        _pendingCopyLength = 0;
        _literalDecoder = _distanceDecoder = null;
    }

    /// <summary>Inflates up to <paramref name="count"/> bytes; returns 0 only at the end of the stream.</summary>
    public int Read(byte[] destination, int offset, int count)
    {
        int written = 0;
        while (written < count)
        {
            if (_pendingCopyLength > 0)
            {
                written += CopyFromWindow(destination, offset + written, count - written);
                continue;
            }
            switch (_state)
            {
                case State.Finished:
                    return written;
                case State.BlockHeader:
                    SealFinishedTracked();
                    BlockBoundaryReached?.Invoke(this);
                    ReadBlockHeader();
                    break;
                case State.StoredBlock:
                    written += ReadStored(destination, offset + written, count - written);
                    break;
                case State.HuffmanBlock:
                    written += ReadHuffman(destination, offset + written, count - written);
                    break;
            }
        }
        return written;
    }

    private void ReadBlockHeader()
    {
        if (_isFinalBlock) { _state = State.Finished; SealFinishedTracked(); return; }
        _isFinalBlock = _bits.ReadBits(1) == 1;
        switch (_bits.ReadBits(2))
        {
            case 0:
                _bits.AlignToByte();
                int length = _bits.ReadBits(16);
                int complement = _bits.ReadBits(16);
                if ((length ^ 0xFFFF) != complement) throw new InvalidDataException("Deflate: stored block length mismatch.");
                _storedRemaining = length;
                _state = State.StoredBlock;
                break;
            case 1:
                _literalDecoder = FixedLiteralDecoder;
                _distanceDecoder = FixedDistanceDecoder;
                _state = State.HuffmanBlock;
                break;
            case 2:
                ReadDynamicTables();
                _state = State.HuffmanBlock;
                break;
            default:
                throw new InvalidDataException("Deflate: reserved block type.");
        }
    }

    private void ReadDynamicTables()
    {
        int literalCount = _bits.ReadBits(5) + 257;
        int distanceCount = _bits.ReadBits(5) + 1;
        int codeLengthCount = _bits.ReadBits(4) + 4;

        Span<byte> codeLengthLengths = stackalloc byte[19];
        for (int i = 0; i < codeLengthCount; i++) codeLengthLengths[CodeLengthOrder[i]] = (byte)_bits.ReadBits(3);
        var codeLengthDecoder = new DeflateHuffmanDecoder(codeLengthLengths);

        var lengths = new byte[literalCount + distanceCount];
        int filled = 0;
        while (filled < lengths.Length)
        {
            _bits.EnsureBits(15);
            int symbol = codeLengthDecoder.Decode(_bits);
            if (symbol < 16) { lengths[filled++] = (byte)symbol; continue; }
            int repeat;
            byte value = 0;
            if (symbol == 16)
            {
                if (filled == 0) throw new InvalidDataException("Deflate: repeat with no previous length.");
                value = lengths[filled - 1];
                repeat = 3 + _bits.ReadBits(2);
            }
            else if (symbol == 17) repeat = 3 + _bits.ReadBits(3);
            else repeat = 11 + _bits.ReadBits(7);
            if (filled + repeat > lengths.Length) throw new InvalidDataException("Deflate: code lengths overflow.");
            for (int i = 0; i < repeat; i++) lengths[filled++] = value;
        }
        _literalDecoder = new DeflateHuffmanDecoder(lengths.AsSpan(0, literalCount));
        _distanceDecoder = new DeflateHuffmanDecoder(lengths.AsSpan(literalCount, distanceCount));
    }

    private int ReadStored(byte[] destination, int offset, int count)
    {
        int toCopy = Math.Min(count, _storedRemaining);
        for (int i = 0; i < toCopy; i++) destination[offset + i] = EmitByte((byte)_bits.ReadBits(8));
        _storedRemaining -= toCopy;
        if (_storedRemaining == 0) _state = State.BlockHeader;
        return toCopy;
    }

    private int ReadHuffman(byte[] destination, int offset, int count)
    {
        DeflateHuffmanDecoder literals = _literalDecoder!, distances = _distanceDecoder!;
        DeflateBitReader bits = _bits;
        byte[] window = _window;
        int windowPosition = _windowPosition;   // literals update this local; it is flushed to the field before any match copy
        int written = 0;
        int unflushedLiterals = 0;
        while (written < count)
        {
            // One refill (≥ 57 bits) covers the longest symbol + extra bits + distance + its extra bits (≤ 48).
            bits.Refill();
            int symbol = literals.Decode(bits);
            if (symbol < 256)
            {
                byte value = (byte)symbol;
                destination[offset + written++] = value;
                window[windowPosition] = value;
                windowPosition = (windowPosition + 1) & WindowMask;
                unflushedLiterals++;
                continue;
            }
            if (symbol == 256) { _state = State.BlockHeader; break; }

            int lengthIndex = symbol - 257;
            if (lengthIndex >= LengthBase.Length) throw new InvalidDataException("Deflate: bad length symbol.");
            int length = LengthBase[lengthIndex] + bits.ReadBits(LengthExtraBits[lengthIndex]);
            int distanceSymbol = distances.Decode(bits);
            if (distanceSymbol >= DistanceBase.Length) throw new InvalidDataException("Deflate: bad distance symbol.");
            int distance = DistanceBase[distanceSymbol] + bits.ReadBits(DistanceExtraBits[distanceSymbol]);

            _windowPosition = windowPosition;
            _totalOut += unflushedLiterals;
            unflushedLiterals = 0;
            if (distance > _totalOut) throw new InvalidDataException("Deflate: distance before start of output.");
            _pendingCopyLength = length;
            _pendingCopyDistance = distance;
            written += CopyFromWindow(destination, offset + written, count - written);
            windowPosition = _windowPosition;
        }
        _windowPosition = windowPosition;
        _totalOut += unflushedLiterals;
        return written;
    }

    private int CopyFromWindow(byte[] destination, int offset, int count)
    {
        int toCopy = Math.Min(count, _pendingCopyLength);
        int distance = _pendingCopyDistance;
        if (_tracked.Count > 0) MarkReferenced(_totalOut - distance, toCopy);
        int source = (_windowPosition - distance) & WindowMask;
        bool wraps = source + toCopy > _window.Length || _windowPosition + toCopy > _window.Length;
        if (!wraps)
        {
            if (distance >= toCopy)
            {
                Buffer.BlockCopy(_window, source, _window, _windowPosition, toCopy);
            }
            else
            {
                // Overlapping match (a repeating pattern of `distance` bytes): write the first period,
                // then double what is already written — O(log n) block copies instead of a byte loop.
                Buffer.BlockCopy(_window, source, _window, _windowPosition, distance);
                int done = distance;
                while (done < toCopy)
                {
                    int chunk = Math.Min(done, toCopy - done);
                    Buffer.BlockCopy(_window, _windowPosition, _window, _windowPosition + done, chunk);
                    done += chunk;
                }
            }
            Buffer.BlockCopy(_window, _windowPosition, destination, offset, toCopy);
            _windowPosition = (_windowPosition + toCopy) & WindowMask;
            _totalOut += toCopy;
        }
        else
        {
            for (int i = 0; i < toCopy; i++)
            {
                destination[offset + i] = EmitByte(_window[source]);
                source = (source + 1) & WindowMask;
            }
        }
        _pendingCopyLength -= toCopy;
        return toCopy;
    }

    /// <summary>Flags, for every tracked checkpoint, the window bytes in [source, source + length) that lie before its span start.</summary>
    private void MarkReferenced(long source, int length)
    {
        foreach (Tracked tracked in _tracked)
        {
            if (tracked.Referenced is null) continue;
            long end = Math.Min(source + length, tracked.SpanStart);
            if (source >= end) continue;
            int first = (int)(source - tracked.WindowStartOffset), last = (int)(end - tracked.WindowStartOffset);
            if (first < 0) first = 0;   // cannot happen (distance <= window), kept as a guard
            bool[] referenced = tracked.Referenced;
            for (int i = first; i < last; i++) referenced[i] = true;
        }
    }

    private byte EmitByte(byte value)
    {
        _window[_windowPosition] = value;
        _windowPosition = (_windowPosition + 1) & WindowMask;
        _totalOut++;
        return value;
    }

    private static DeflateHuffmanDecoder BuildFixedLiteralDecoder()
    {
        var lengths = new byte[288];
        for (int i = 0; i < 144; i++) lengths[i] = 8;
        for (int i = 144; i < 256; i++) lengths[i] = 9;
        for (int i = 256; i < 280; i++) lengths[i] = 7;
        for (int i = 280; i < 288; i++) lengths[i] = 8;
        return new DeflateHuffmanDecoder(lengths);
    }

    private static DeflateHuffmanDecoder BuildFixedDistanceDecoder()
    {
        var lengths = new byte[30];
        for (int i = 0; i < 30; i++) lengths[i] = 5;
        return new DeflateHuffmanDecoder(lengths);
    }
}
