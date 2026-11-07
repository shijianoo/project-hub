using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace ProjectHub.Infrastructure.Security;

/// <summary>使用内置点阵字形生成跨平台 PNG 验证码，避免依赖操作系统字体和 GDI。</summary>
internal static class CaptchaImageRenderer
{
    private const int Width = 146;
    private const int Height = 48;
    private const int BytesPerPixel = 4;
    private const int GlyphWidth = 5;
    private const int GlyphHeight = 7;
    private const int GlyphScale = 4;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    private static readonly IReadOnlyDictionary<char, string[]> Glyphs = new Dictionary<char, string[]>
    {
        ['0'] = ["01110", "10001", "10011", "10101", "11001", "10001", "01110"],
        ['1'] = ["00100", "01100", "00100", "00100", "00100", "00100", "01110"],
        ['2'] = ["01110", "10001", "00001", "00010", "00100", "01000", "11111"],
        ['3'] = ["11110", "00001", "00001", "01110", "00001", "00001", "11110"],
        ['4'] = ["00010", "00110", "01010", "10010", "11111", "00010", "00010"],
        ['5'] = ["11111", "10000", "10000", "11110", "00001", "00001", "11110"],
        ['6'] = ["01110", "10000", "10000", "11110", "10001", "10001", "01110"],
        ['7'] = ["11111", "00001", "00010", "00100", "01000", "01000", "01000"],
        ['8'] = ["01110", "10001", "10001", "01110", "10001", "10001", "01110"],
        ['9'] = ["01110", "10001", "10001", "01111", "00001", "00001", "01110"]
    };

    private static readonly RgbaColor[] TextColors =
    [
        new(37, 69, 151, 255),
        new(27, 95, 116, 255),
        new(80, 55, 145, 255),
        new(34, 91, 66, 255)
    ];

    /// <summary>将验证码字符绘制为 PNG Data URL。</summary>
    public static string RenderDataUrl(string code)
    {
        byte[] pixels = new byte[Width * Height * BytesPerPixel];
        FillBackground(pixels);
        DrawNoise(pixels, lineCount: 4, dotCount: 95, foreground: false);
        DrawCode(pixels, code);
        DrawNoise(pixels, lineCount: 2, dotCount: 45, foreground: true);

        byte[] png = EncodePng(pixels);
        return $"data:image/png;base64,{Convert.ToBase64String(png)}";
    }

    private static void FillBackground(byte[] pixels)
    {
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int variation = (x + y) % 7;
                SetPixel(pixels, x, y, new RgbaColor(
                    (byte)(247 - variation),
                    (byte)(249 - variation / 2),
                    253,
                    255));
            }
        }
    }

    private static void DrawCode(byte[] pixels, string code)
    {
        int characterWidth = GlyphWidth * GlyphScale;
        int gap = 5;
        int totalWidth = (code.Length * characterWidth) + ((code.Length - 1) * gap);
        int startX = Math.Max(4, (Width - totalWidth) / 2);

        for (int characterIndex = 0; characterIndex < code.Length; characterIndex++)
        {
            if (!Glyphs.TryGetValue(code[characterIndex], out string[]? glyph))
            {
                continue;
            }

            int x = startX + (characterIndex * (characterWidth + gap));
            int y = 8 + RandomNumberGenerator.GetInt32(-2, 3);
            int shear = RandomNumberGenerator.GetInt32(-2, 3);
            RgbaColor color = TextColors[RandomNumberGenerator.GetInt32(TextColors.Length)];
            DrawGlyph(pixels, glyph, x, y, shear, color);
        }
    }

    private static void DrawGlyph(byte[] pixels, IReadOnlyList<string> glyph, int originX, int originY, int shear, RgbaColor color)
    {
        for (int row = 0; row < GlyphHeight; row++)
        {
            int rowOffset = ((row - (GlyphHeight / 2)) * shear) / 3;
            for (int column = 0; column < GlyphWidth; column++)
            {
                if (glyph[row][column] != '1')
                {
                    continue;
                }

                int x = originX + rowOffset + (column * GlyphScale);
                int y = originY + (row * GlyphScale);
                FillRectangle(pixels, x, y, GlyphScale, GlyphScale, color);
            }
        }
    }

    private static void DrawNoise(byte[] pixels, int lineCount, int dotCount, bool foreground)
    {
        for (int index = 0; index < lineCount; index++)
        {
            RgbaColor color = foreground
                ? new RgbaColor(112, 130, 176, 175)
                : new RgbaColor(184, 196, 225, 165);
            DrawLine(
                pixels,
                RandomNumberGenerator.GetInt32(Width),
                RandomNumberGenerator.GetInt32(Height),
                RandomNumberGenerator.GetInt32(Width),
                RandomNumberGenerator.GetInt32(Height),
                color);
        }

        for (int index = 0; index < dotCount; index++)
        {
            RgbaColor color = foreground
                ? new RgbaColor(91, 113, 169, 145)
                : new RgbaColor(169, 183, 218, 145);
            SetPixel(
                pixels,
                RandomNumberGenerator.GetInt32(Width),
                RandomNumberGenerator.GetInt32(Height),
                color);
        }
    }

    private static void DrawLine(byte[] pixels, int x0, int y0, int x1, int y1, RgbaColor color)
    {
        int deltaX = Math.Abs(x1 - x0);
        int stepX = x0 < x1 ? 1 : -1;
        int deltaY = -Math.Abs(y1 - y0);
        int stepY = y0 < y1 ? 1 : -1;
        int error = deltaX + deltaY;

        while (true)
        {
            SetPixel(pixels, x0, y0, color);
            if (x0 == x1 && y0 == y1)
            {
                break;
            }

            int doubledError = error * 2;
            if (doubledError >= deltaY)
            {
                error += deltaY;
                x0 += stepX;
            }

            if (doubledError <= deltaX)
            {
                error += deltaX;
                y0 += stepY;
            }
        }
    }

    private static void FillRectangle(byte[] pixels, int x, int y, int width, int height, RgbaColor color)
    {
        for (int currentY = y; currentY < y + height; currentY++)
        {
            for (int currentX = x; currentX < x + width; currentX++)
            {
                SetPixel(pixels, currentX, currentY, color);
            }
        }
    }

    private static void SetPixel(byte[] pixels, int x, int y, RgbaColor color)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height)
        {
            return;
        }

        int offset = ((y * Width) + x) * BytesPerPixel;
        byte inverseAlpha = (byte)(255 - color.Alpha);
        pixels[offset] = Blend(pixels[offset], color.Red, color.Alpha, inverseAlpha);
        pixels[offset + 1] = Blend(pixels[offset + 1], color.Green, color.Alpha, inverseAlpha);
        pixels[offset + 2] = Blend(pixels[offset + 2], color.Blue, color.Alpha, inverseAlpha);
        pixels[offset + 3] = 255;
    }

    private static byte Blend(byte background, byte foreground, byte alpha, byte inverseAlpha)
        => (byte)(((foreground * alpha) + (background * inverseAlpha)) / 255);

    private static byte[] EncodePng(byte[] pixels)
    {
        using MemoryStream output = new();
        output.Write(PngSignature);

        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), Height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(output, "IHDR", header);

        byte[] scanLines = new byte[Height * ((Width * BytesPerPixel) + 1)];
        int sourceOffset = 0;
        int targetOffset = 0;
        for (int y = 0; y < Height; y++)
        {
            scanLines[targetOffset++] = 0;
            int rowLength = Width * BytesPerPixel;
            Buffer.BlockCopy(pixels, sourceOffset, scanLines, targetOffset, rowLength);
            sourceOffset += rowLength;
            targetOffset += rowLength;
        }

        using MemoryStream compressed = new();
        using (ZLibStream compressor = new(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            compressor.Write(scanLines);
        }

        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        uint crc = ComputeCrc(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static uint ComputeCrc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in type)
        {
            crc = UpdateCrc(crc, value);
        }

        foreach (byte value in data)
        {
            crc = UpdateCrc(crc, value);
        }

        return ~crc;
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++)
        {
            crc = (crc & 1) == 1 ? (crc >> 1) ^ 0xEDB88320U : crc >> 1;
        }

        return crc;
    }

    private readonly record struct RgbaColor(byte Red, byte Green, byte Blue, byte Alpha);
}
