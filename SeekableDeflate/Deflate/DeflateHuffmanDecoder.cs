namespace SeekableDeflate;

/// <summary>
/// One canonical Huffman code (literal/length, distance or code-length alphabet) built from code
/// lengths as RFC 1951 §3.2.2 prescribes. Decoding peeks <see cref="FastBits"/> bits into a lookup
/// table; codes longer than that fall back to the bit-by-bit canonical walk (zlib's puff.c method).
/// </summary>
internal sealed class DeflateHuffmanDecoder
{
    public const int FastBits = 10;
    private const int MaxBits = 15;

    // fast[peekedBits] = symbol << 4 | codeLength, or 0 when the code is longer than FastBits.
    private readonly ushort[] _fast = new ushort[1 << FastBits];
    // Canonical walk tables: count of codes per length, symbols ordered by (length, value).
    private readonly ushort[] _countPerLength = new ushort[MaxBits + 1];
    private readonly ushort[] _symbolsByCode;

    public DeflateHuffmanDecoder(ReadOnlySpan<byte> codeLengths)
    {
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
            ushort entry = (ushort)((symbol << 4) | length);
            for (int suffix = reversed; suffix < _fast.Length; suffix += 1 << length) _fast[suffix] = entry;
        }
    }

    /// <summary>Decodes one symbol; the caller has already refilled the reader (≥ 15 bits unless the data ended).</summary>
    public int Decode(DeflateBitReader bits)
    {
        ushort entry = _fast[bits.PeekBits(FastBits)];
        if (entry != 0)
        {
            bits.DropBits(entry & 0xF);
            return entry >> 4;
        }

        // Slow path: codes longer than FastBits. Walk one bit at a time (MSB-first code value).
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
}
