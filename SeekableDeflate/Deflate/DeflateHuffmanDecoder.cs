namespace SeekableDeflate;

/// <summary>
/// One canonical Huffman code (literal/length, distance or code-length alphabet) built from code
/// lengths as RFC 1951 §3.2.2 prescribes. Decoding peeks <see cref="FastBits"/> bits into a lookup
/// table whose entry already carries what the symbol means — a literal byte, a length or distance
/// base with its count of extra bits, the end of the block — the way zlib's inflate_fast tables
/// (inftrees.c: op / bits / val) save the hot loop a second lookup. Codes longer than that fall
/// back to the bit-by-bit canonical walk (zlib's puff.c method).
/// </summary>
internal sealed class DeflateHuffmanDecoder
{
    public const int FastBits = 10;
    private const int MaxBits = 15;

    /// <summary>What a code stands for, in the entry's kind field.</summary>
    public const uint KindLiteral = 0;     // value = the byte (or the symbol itself, for the code-length alphabet)
    public const uint KindBase = 1;        // value = base length or distance, extra = its extra bits
    public const uint KindEndOfBlock = 2;
    public const uint KindInvalid = 3;     // the unused symbols 286-287 / 30-31

    /// <summary>Which alphabet the lengths describe: decides what the fast entries carry.</summary>
    public enum Alphabet { Symbols, LiteralLength, Distance }

    private static readonly ushort[] LengthBase =
        [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];
    private static readonly byte[] LengthExtraBits =
        [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
    private static readonly ushort[] DistanceBase =
        [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];
    private static readonly byte[] DistanceExtraBits =
        [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];

    /// <summary>
    /// Fast[peekedBits]: bits 0-3 the code length (0 = the code is longer than <see cref="FastBits"/>: walk), bits 4-5 the
    /// kind, bits 6-10 the extra bits, bits 16-31 the value.
    /// </summary>
    public readonly uint[] Fast = new uint[1 << FastBits];
    private readonly Alphabet _alphabet;
    // Canonical walk tables: count of codes per length, symbols ordered by (length, value).
    private readonly ushort[] _countPerLength = new ushort[MaxBits + 1];
    private readonly ushort[] _symbolsByCode;

    public DeflateHuffmanDecoder(ReadOnlySpan<byte> codeLengths, Alphabet alphabet = Alphabet.Symbols)
    {
        _alphabet = alphabet;
        _symbolsByCode = new ushort[codeLengths.Length];
        foreach (byte length in codeLengths) _countPerLength[length]++;
        _countPerLength[0] = 0;

        var firstCodeOfLength = new int[MaxBits + 2];
        var nextSymbolSlot = new int[MaxBits + 2];
        int code = 0;
        for (int length = 1; length <= MaxBits; length++)
        {
            code = (code + _countPerLength[length - 1]) << 1;
            firstCodeOfLength[length] = code;
        }
        int slot = 0;
        for (int length = 1; length <= MaxBits; length++)
        {
            nextSymbolSlot[length] = slot;
            slot += _countPerLength[length];
        }

        for (int symbol = 0; symbol < codeLengths.Length; symbol++)
        {
            int length = codeLengths[symbol];
            if (length == 0) continue;
            int symbolCode = firstCodeOfLength[length]++;
            _symbolsByCode[nextSymbolSlot[length]++] = (ushort)symbol;
            if (length > FastBits) continue;

            // Deflate writes Huffman codes MSB first, the bit reader hands bits LSB first:
            // the table index is the bit-reversed code, padded with every possible suffix.
            int reversed = 0;
            for (int bit = 0; bit < length; bit++) reversed |= ((symbolCode >> bit) & 1) << (length - 1 - bit);
            uint entry = Entry(symbol) | (uint)length;
            for (int suffix = reversed; suffix < Fast.Length; suffix += 1 << length) Fast[suffix] = entry;
        }
    }

    /// <summary>The meaning of a symbol (kind, extra bits, value) — without the code length.</summary>
    public uint Entry(int symbol)
    {
        switch (_alphabet)
        {
            case Alphabet.LiteralLength:
                if (symbol < 256) return (KindLiteral << 4) | ((uint)symbol << 16);
                if (symbol == 256) return KindEndOfBlock << 4;
                if (symbol - 257 >= LengthBase.Length) return KindInvalid << 4;
                return (KindBase << 4) | ((uint)LengthExtraBits[symbol - 257] << 6) | ((uint)LengthBase[symbol - 257] << 16);
            case Alphabet.Distance:
                if (symbol >= DistanceBase.Length) return KindInvalid << 4;
                return (KindBase << 4) | ((uint)DistanceExtraBits[symbol] << 6) | ((uint)DistanceBase[symbol] << 16);
            default:
                return (KindLiteral << 4) | ((uint)symbol << 16);
        }
    }

    /// <summary>Decodes one symbol (any alphabet) through the reader; the caller has already refilled it (≥ 15 bits unless the data ended).</summary>
    public int Decode(DeflateBitReader bits)
    {
        uint entry = Fast[bits.PeekBits(FastBits)];
        if (entry != 0)
        {
            bits.DropBits((int)(entry & 0xF));
            return _alphabet == Alphabet.Symbols ? (int)(entry >> 16) : throw new InvalidOperationException("Decode(reader) serves the symbol alphabets; the hot loop reads the entries.");
        }
        return WalkSlow(bits);
    }

    private int WalkSlow(DeflateBitReader bits)
    {
        int code = 0, first = 0, index = 0;
        for (int length = 1; length <= MaxBits; length++)
        {
            code |= bits.ReadBits(1);
            int count = _countPerLength[length];
            if (code - count < first) return _symbolsByCode[index + (code - first)];
            index += count;
            first = (first + count) << 1;
            code <<= 1;
        }
        throw new InvalidDataException("Deflate: invalid Huffman code.");
    }

    /// <summary>
    /// The slow path of the hot loop: a code longer than <see cref="FastBits"/>, walked one bit at a time (MSB-first code
    /// value) out of the loop's own bit buffer, which holds at least 15 bits. Returns the symbol's entry (kind, extra, value).
    /// </summary>
    public uint WalkSlow(ref ulong hold, ref int bits)
    {
        int code = 0, first = 0, index = 0;
        for (int length = 1; length <= MaxBits; length++)
        {
            code |= (int)(hold & 1);
            hold >>= 1;
            bits--;
            int count = _countPerLength[length];
            if (code - count < first) return Entry(_symbolsByCode[index + (code - first)]);
            index += count;
            first = (first + count) << 1;
            code <<= 1;
        }
        throw new InvalidDataException("Deflate: invalid Huffman code.");
    }
}
