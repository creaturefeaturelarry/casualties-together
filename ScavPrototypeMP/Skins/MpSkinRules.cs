using System;

// size checks with no unity types, so tests can run them.
internal static class MpSkinRules {
    internal const int MaxZipBytes = 4 * 1024 * 1024;
    // body + Head + WoundView custom sets can land near ~55 known pngs.
    internal const int MaxEntries = 96;
    internal const int MaxEntryBytes = 1024 * 1024;
    internal const int MaxUncompressedBytes = 16 * 1024 * 1024;
    internal const int MaxTextureSize = 2048;
    internal const long MaxTexturePixels = 32L * 1024 * 1024;
    internal const int MaxCompressionRatio = 100;

    private static readonly byte[] PngSignature =
        { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    internal static bool ZipSizeOk(long bytes) {
        return bytes > 0 && bytes <= MaxZipBytes;
    }

    internal static bool EntryCountOk(int count) {
        return count > 0 && count <= MaxEntries;
    }

    internal static bool EntrySizeOk(long bytes) {
        return bytes > 0 && bytes <= MaxEntryBytes;
    }

    internal static bool TotalSizeOk(long bytes) {
        return bytes <= MaxUncompressedBytes;
    }

    internal static bool CompressionRatioOk(long uncompressed, long compressed) {
        if (uncompressed <= 0) {
            return true;
        }

        if (compressed <= 0) {
            return false;
        }

        return uncompressed <= compressed * (long)MaxCompressionRatio;
    }

    internal static bool TextureSizeOk(int width, int height) {
        return width > 0 && height > 0 &&
               width <= MaxTextureSize && height <= MaxTextureSize;
    }

    internal static bool TexturePixelsOk(long pixels) {
        return pixels > 0 && pixels <= MaxTexturePixels;
    }

    // png size comes from the header. a bad header is skipped.
    internal static bool TryReadPngSize(byte[] data, out int width, out int height) {
        width = 0;
        height = 0;
        if (data == null || data.Length < 24) {
            return false;
        }

        for (int i = 0; i < PngSignature.Length; i++) {
            if (data[i] != PngSignature[i]) {
                return false;
            }
        }

        // ihdr width and height sit at bytes 16 and 20.
        if (data[12] != (byte)'I' || data[13] != (byte)'H' ||
            data[14] != (byte)'D' || data[15] != (byte)'R') {
            return false;
        }

        uint w = ReadBigEndian(data, 16);
        uint h = ReadBigEndian(data, 20);
        if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue) {
            return false;
        }

        width = (int)w;
        height = (int)h;
        return true;
    }

    private static uint ReadBigEndian(byte[] data, int offset) {
        return ((uint)data[offset] << 24) |
               ((uint)data[offset + 1] << 16) |
               ((uint)data[offset + 2] << 8) |
               data[offset + 3];
    }
}