namespace NocturneFlatScroll;

/// <summary>
/// The width and height a PNG or JPEG file says it has, read from its header without decoding
/// it, so a card image too big to decode can be refused first.
/// This file has no Unity or game dependencies.
/// </summary>
internal static class ImageSize
{
    /// <summary>The image's size, or null when it isn't a PNG or JPEG whose size can be read.</summary>
    internal static (int Width, int Height)? Read(byte[] bytes)
    {
        if (bytes.Length >= 24 && bytes[0] == 0x89 && bytes[1] == 'P' && bytes[2] == 'N' && bytes[3] == 'G'
            && bytes[12] == 'I' && bytes[13] == 'H' && bytes[14] == 'D' && bytes[15] == 'R')
            return Sized(BigEndian32(bytes, 16), BigEndian32(bytes, 20));
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8) return Jpeg(bytes);
        return null;
    }

    private static (int, int)? Sized(long width, long height) =>
        width > 0 && height > 0 && width <= int.MaxValue && height <= int.MaxValue ? ((int)width, (int)height) : null;

    private static long BigEndian32(byte[] b, int at) => ((long)b[at] << 24) | ((long)b[at + 1] << 16) | ((long)b[at + 2] << 8) | b[at + 3];

    // The markers after the start of the image, up to the frame header (SOF), which holds the size.
    private static (int, int)? Jpeg(byte[] b)
    {
        int i = 2;
        while (i < b.Length)
        {
            if (b[i] != 0xFF) return null;
            // Any number of 0xFF can come before a marker.
            while (i < b.Length && b[i] == 0xFF) i++;
            if (i >= b.Length) return null;
            byte marker = b[i++];
            // Markers without a length.
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD8)) continue;
            // The end, or the image data, before any frame header.
            if (marker == 0xD9 || marker == 0xDA) return null;
            if (i + 2 > b.Length) return null;
            int length = (b[i] << 8) | b[i + 1];
            if (length < 2) return null;
            bool frame = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
            if (frame)
            {
                if (length < 7 || i + 7 > b.Length) return null;
                int height = (b[i + 3] << 8) | b[i + 4];
                int width = (b[i + 5] << 8) | b[i + 6];
                return Sized(width, height);
            }
            i += length;
        }
        return null;
    }
}
