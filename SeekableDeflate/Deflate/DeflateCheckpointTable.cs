namespace SeekableDeflate;

/// <summary>
/// The compact sidecar layout of a part's checkpoints: positions as varint deltas (compressed byte
/// position, uncompressed offset), the leftover bits in two bytes, the window length, then every
/// window in one <see cref="DeflateWindowStore"/>. 44 checkpoints of a 91 MB part: ~36 KB, nearly all
/// of it the windows; the positions are ~10 bytes each.
/// </summary>
public static class DeflateCheckpointTable
{
    public static void Write(BinaryWriter writer, IReadOnlyList<DeflateCheckpoint> checkpoints, int groupSize = DeflateWindowStore.DefaultGroupSize)
    {
        writer.Write(checkpoints.Count);
        long previousCompressed = 0, previousOffset = 0;
        foreach (DeflateCheckpoint checkpoint in checkpoints)
        {
            if (checkpoint.CompressedBytePosition < previousCompressed || checkpoint.UncompressedOffset < previousOffset)
                throw new InvalidDataException("Checkpoints must ascend.");
            DeflateWindowStore.WriteVarint(writer, (ulong)(checkpoint.CompressedBytePosition - previousCompressed));
            DeflateWindowStore.WriteVarint(writer, (ulong)(checkpoint.UncompressedOffset - previousOffset));
            writer.Write((byte)checkpoint.LeftoverBitCount);
            writer.Write((byte)checkpoint.LeftoverBits);
            DeflateWindowStore.WriteVarint(writer, (ulong)checkpoint.WindowLength);
            previousCompressed = checkpoint.CompressedBytePosition;
            previousOffset = checkpoint.UncompressedOffset;
        }
        DeflateWindowStore.Build(checkpoints, groupSize).Write(writer);
    }

    public static List<DeflateCheckpoint> Read(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count < 0) throw new InvalidDataException("Checkpoint table: negative count.");
        var positions = new (long compressed, long offset, int bitCount, int bits, int windowLength)[count];
        long compressed = 0, offset = 0;
        for (int i = 0; i < count; i++)
        {
            compressed += (long)DeflateWindowStore.ReadVarint(reader);
            offset += (long)DeflateWindowStore.ReadVarint(reader);
            int bitCount = reader.ReadByte(), bits = reader.ReadByte();
            int windowLength = (int)DeflateWindowStore.ReadVarint(reader);
            if (bitCount > 7 || windowLength > DeflateCheckpoint.WindowSize) throw new InvalidDataException("Checkpoint table: bad entry.");
            positions[i] = (compressed, offset, bitCount, bits, windowLength);
        }
        DeflateWindowStore store = DeflateWindowStore.Read(reader);
        if (store.Count != count) throw new InvalidDataException("Checkpoint table: window store does not match.");
        var checkpoints = new List<DeflateCheckpoint>(count);
        for (int i = 0; i < count; i++)
        {
            (long c, long o, int bitCount, int bits, int windowLength) = positions[i];
            checkpoints.Add(new DeflateCheckpoint(c, bitCount, bits, o, windowLength, store, i, store.HasBytes(i) ? -1 : 0));
        }
        return checkpoints;
    }
}
