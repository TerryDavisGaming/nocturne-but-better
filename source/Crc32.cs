namespace NocturneFlatScroll;

/// <summary>
/// CRC-32 with the zip and PNG polynomial (0xEDB88320, reflected). PngWriter's chunks and the
/// hub's zip checks and zip writer use it. This file has no Unity or game dependencies.
/// </summary>
internal static class Crc32
{
    private static readonly uint[] Table = MakeTable();

    /// <summary>The start value; pass it to <see cref="Update"/>, then <see cref="Finish"/> the result.</summary>
    internal const uint Start = 0xFFFFFFFFu;

    /// <summary>Carries a running CRC over more bytes (the value is kept inverted, as the algorithm has it).</summary>
    internal static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    internal static uint Finish(uint crc) => crc ^ 0xFFFFFFFFu;

    /// <summary>The CRC-32 of a whole buffer.</summary>
    internal static uint Of(ReadOnlySpan<byte> data) => Finish(Update(Start, data));

    private static uint[] MakeTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
