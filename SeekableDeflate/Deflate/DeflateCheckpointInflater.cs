using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace SeekableDeflate;

/// <summary>
/// Raw-deflate (RFC 1951) inflater that pauses between symbols, so a read can stop at any output
/// offset, and that calls <see cref="BlockBoundaryReached"/> before each new block — the only
/// moments where <see cref="CaptureCheckpoint"/> yields a resumable state — and can later resume
/// from such a checkpoint via <see cref="ResumeFrom"/>. Managed code only, no zlib dependency.
///
/// The hot loop follows zlib's inflate_fast (inffast.c) and libdeflate's decompress_template.h:
/// the bit buffer lives in locals and is refilled eight bytes at a time, one table entry carries
/// the symbol's meaning (literal, length base + extra bits, distance base + extra bits), and the
/// output goes into one linear buffer whose first 32 KB are the history, so a match is a copy
/// inside that buffer — eight bytes per store for distances of eight or more, the pattern
/// repeated for shorter ones — with no circular window and no per-byte bounds checks. The loop
/// may produce up to a match length past what the caller asked for; those bytes are handed out
/// by the next read, and a checkpoint is only captured once everything produced was delivered.
/// </summary>
public sealed class DeflateCheckpointInflater
{
    private enum State { BlockHeader, StoredBlock, HuffmanBlock, Finished }

    private const int WindowSize = DeflateCheckpoint.WindowSize;
    private const int OutputChunk = 1 << 16;                   // produced per pass before the history is moved to the front
    private const int OutputEnd = WindowSize + OutputChunk;    // the hot loop writes no literal at or past this
    private const int MaxMatch = 258;
    private const int OutputSlack = MaxMatch + 16;             // a match may run past OutputEnd, and the word copies past the match
    private const int FastMask = (1 << DeflateHuffmanDecoder.FastBits) - 1;

    private static readonly byte[] CodeLengthOrder = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

    private static readonly DeflateHuffmanDecoder FixedLiteralDecoder = BuildFixedLiteralDecoder();
    private static readonly DeflateHuffmanDecoder FixedDistanceDecoder = BuildFixedDistanceDecoder();

    private readonly DeflateBitReader _bits;

    // [0, WindowSize) the history, then the output of the current pass; [_outDelivered, _outPosition) produced, not yet handed out
    private readonly byte[] _out = new byte[OutputEnd + OutputSlack];
    private int _outPosition = WindowSize;
    private int _outDelivered = WindowSize;
    private long _totalProduced;      // bytes produced, including the undelivered ones
    private long _totalOut;           // bytes handed to callers

    private State _state = State.BlockHeader;
    private bool _isFinalBlock;
    private int _storedRemaining;
    private DeflateHuffmanDecoder? _literalDecoder;
    private DeflateHuffmanDecoder? _distanceDecoder;

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
    private readonly byte[] _resumeWindow = new byte[WindowSize];

    /// <summary>Raised just before a new block's header is read; <see cref="CaptureCheckpoint"/> is valid inside it.</summary>
    public event Action<DeflateCheckpointInflater>? BlockBoundaryReached;

    /// <summary>Raised when a captured checkpoint's window is sealed (sparse): 32 KB of output past it, or the end of the stream.</summary>
    public event Action<DeflateCheckpoint>? CheckpointSealed;

    public long TotalOut => _totalOut;

    /// <summary>True at the boundary after the final block: the stream is over, a checkpoint there would be useless.</summary>
    public bool IsPastFinalBlock => _isFinalBlock;
    public bool IsFinished => _state == State.Finished && _outDelivered == _outPosition;

    public DeflateCheckpointInflater(Stream input, long dataStart, long dataLength)
    {
        _bits = new DeflateBitReader(input, dataStart, dataLength);
    }

    public DeflateCheckpoint CaptureCheckpoint()
    {
        if (_state != State.BlockHeader || _outDelivered != _outPosition)
            throw new InvalidOperationException("A checkpoint can only be captured at a block boundary.");
        int windowLength = (int)Math.Min(_totalOut, WindowSize);
        var window = new byte[windowLength];
        Buffer.BlockCopy(_out, _outPosition - windowLength, window, 0, windowLength);
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
        while (_tracked.Count > 0 && (_totalOut - _tracked[0].SpanStart >= WindowSize || _state == State.Finished))
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
            bool spanCoversWindow = _totalOut - tracked.SpanStart >= WindowSize || IsFinished;
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
        Array.Clear(_out, 0, WindowSize - checkpoint.WindowLength);
        Buffer.BlockCopy(_resumeWindow, 0, _out, WindowSize - checkpoint.WindowLength, checkpoint.WindowLength);
        _outPosition = _outDelivered = WindowSize;
        _totalOut = _totalProduced = checkpoint.UncompressedOffset;
        _state = State.BlockHeader;
        _isFinalBlock = false;
        _literalDecoder = _distanceDecoder = null;
    }

    /// <summary>Inflates up to <paramref name="count"/> bytes; returns 0 only at the end of the stream.</summary>
    public int Read(byte[] destination, int offset, int count)
    {
        int written = 0;
        while (written < count)
        {
            int undelivered = _outPosition - _outDelivered;
            if (undelivered > 0)
            {
                int toDeliver = Math.Min(undelivered, count - written);
                Buffer.BlockCopy(_out, _outDelivered, destination, offset + written, toDeliver);
                _outDelivered += toDeliver;
                _totalOut += toDeliver;
                written += toDeliver;
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
                    ProduceStored(count - written);
                    break;
                case State.HuffmanBlock:
                    ProduceHuffman(count - written);
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
        _literalDecoder = new DeflateHuffmanDecoder(lengths.AsSpan(0, literalCount), DeflateHuffmanDecoder.Alphabet.LiteralLength);
        _distanceDecoder = new DeflateHuffmanDecoder(lengths.AsSpan(literalCount, distanceCount), DeflateHuffmanDecoder.Alphabet.Distance);
    }

    /// <summary>Moves the last 32 KB of output to the front once the pass area is used up, so the next pass has a whole chunk of room.</summary>
    private void MakeRoom()
    {
        if (_outPosition < OutputEnd) return;
        int shift = _outPosition - WindowSize;
        Buffer.BlockCopy(_out, shift, _out, 0, WindowSize);
        _outPosition = WindowSize;
        _outDelivered -= shift;
    }

    private void ProduceStored(int wanted)
    {
        MakeRoom();
        int toCopy = Math.Min(Math.Min(wanted, _storedRemaining), OutputEnd - _outPosition);
        for (int i = 0; i < toCopy; i++) _out[_outPosition++] = (byte)_bits.ReadBits(8);
        _totalProduced += toCopy;
        _storedRemaining -= toCopy;
        if (_storedRemaining == 0) _state = State.BlockHeader;
    }

    /// <summary>
    /// Decodes symbols until about <paramref name="wanted"/> bytes are produced (a match may run past), the pass area is
    /// full, or the block ends. The bit buffer, the input position and the output position are locals for the duration.
    /// </summary>
#if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]   // fully optimised from the first call: the tiered JIT's tier-0 version of this loop ran at a quarter of the speed for the first seconds of a process (a reopen's first windows)
#endif
    private void ProduceHuffman(int wanted)
    {
        MakeRoom();
        uint[] literalTable = _literalDecoder!.Fast, distanceTable = _distanceDecoder!.Fast;
        DeflateHuffmanDecoder literals = _literalDecoder, distances = _distanceDecoder;
        DeflateBitReader reader = _bits;
        byte[] input = reader.Buffer;
        reader.Export(out ulong hold, out int bits, out int inputPosition, out int inputLength);
        byte[] output = _out;
        ref byte outputBase = ref output[0];
        int outStart = _outPosition;
        int position = outStart;
        int limit = (int)Math.Min(OutputEnd, (long)outStart + wanted);
        bool tracking = _tracked.Count > 0;
        bool inputExhausted = false;

        while (position < limit)
        {
            // Refill to ≥ 57 bits: the longest literal/length code, its extra bits, the longest distance code and its extra
            // bits take at most 15 + 5 + 15 + 13 = 48 (zlib inffast.c refills before each code; one refill per symbol here).
            if (bits <= 56)
            {
                if (inputPosition + 8 <= inputLength)
                {
                    int take = (63 - bits) >> 3;                                                     // 1..7 whole bytes
                    hold |= (BinaryPrimitives.ReadUInt64LittleEndian(input.AsSpan(inputPosition)) & ((1UL << (take * 8)) - 1)) << bits;
                    bits += take * 8;
                    inputPosition += take;
                }
                else
                {
                    while (bits <= 56)
                    {
                        if (inputPosition == inputLength)
                        {
                            if (inputExhausted || !reader.FillForLoop()) { inputExhausted = true; break; }
                            inputPosition = 0;
                            inputLength = reader.BufferLength;
                        }
                        hold |= (ulong)input[inputPosition++] << bits;
                        bits += 8;
                    }
                    // Past the end of the data: zero bits let a final code complete (a truncated stream errors on garbage).
                    if (inputExhausted && bits < 48) bits = 48;
                }
            }

            uint entry = literalTable[(int)hold & FastMask];
            if (entry == 0) entry = literals.WalkSlow(ref hold, ref bits);
            else { int codeBits = (int)(entry & 0xF); hold >>= codeBits; bits -= codeBits; }
            uint kind = (entry >> 4) & 3;
            if (kind == DeflateHuffmanDecoder.KindLiteral)
            {
                Unsafe.Add(ref outputBase, position++) = (byte)(entry >> 16);
                continue;
            }
            if (kind == DeflateHuffmanDecoder.KindEndOfBlock) { _state = State.BlockHeader; break; }
            if (kind == DeflateHuffmanDecoder.KindInvalid) throw new InvalidDataException("Deflate: bad length symbol.");

            int extra = (int)((entry >> 6) & 0x1F);
            int length = (int)(entry >> 16) + (int)(hold & ((1UL << extra) - 1));
            hold >>= extra;
            bits -= extra;

            entry = distanceTable[(int)hold & FastMask];
            if (entry == 0) entry = distances.WalkSlow(ref hold, ref bits);
            else { int codeBits = (int)(entry & 0xF); hold >>= codeBits; bits -= codeBits; }
            if (((entry >> 4) & 3) != DeflateHuffmanDecoder.KindBase) throw new InvalidDataException("Deflate: bad distance symbol.");
            extra = (int)((entry >> 6) & 0x1F);
            int distance = (int)(entry >> 16) + (int)(hold & ((1UL << extra) - 1));
            hold >>= extra;
            bits -= extra;

            long produced = _totalProduced + (position - outStart);
            if (distance > produced) throw new InvalidDataException("Deflate: distance before start of output.");
            if (tracking) MarkReferenced(produced - distance, length);

            // The match: a copy inside the output buffer. Stores run in eight-byte words, past the match end when the length
            // is not a multiple of eight (into bytes the following output overwrites; OutputSlack keeps it inside the array).
            int end = position + length;
            int source = position - distance;
            if (distance >= 8)
            {
                do
                {
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref outputBase, position), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref outputBase, source)));
                    position += 8;
                    source += 8;
                } while (position < end);
            }
            else if (distance == 1)
            {
                ulong fill = 0x0101010101010101UL * Unsafe.Add(ref outputBase, source);
                do
                {
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref outputBase, position), fill);
                    position += 8;
                } while (position < end);
            }
            else
            {
                // libdeflate's short-offset copy: each word store reproduces the pattern, the next load starts `distance` later
                do
                {
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref outputBase, position), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref outputBase, source)));
                    position += distance;
                    source += distance;
                } while (position < end);
            }
            position = end;
        }

        _totalProduced += position - outStart;
        _outPosition = position;
        reader.Import(hold, bits, inputPosition);
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

    private static DeflateHuffmanDecoder BuildFixedLiteralDecoder()
    {
        var lengths = new byte[288];
        for (int i = 0; i < 144; i++) lengths[i] = 8;
        for (int i = 144; i < 256; i++) lengths[i] = 9;
        for (int i = 256; i < 280; i++) lengths[i] = 7;
        for (int i = 280; i < 288; i++) lengths[i] = 8;
        return new DeflateHuffmanDecoder(lengths, DeflateHuffmanDecoder.Alphabet.LiteralLength);
    }

    private static DeflateHuffmanDecoder BuildFixedDistanceDecoder()
    {
        var lengths = new byte[30];
        for (int i = 0; i < 30; i++) lengths[i] = 5;
        return new DeflateHuffmanDecoder(lengths, DeflateHuffmanDecoder.Alphabet.Distance);
    }
}
