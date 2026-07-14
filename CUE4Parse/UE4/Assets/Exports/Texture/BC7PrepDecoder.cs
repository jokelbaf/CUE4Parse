using System.Buffers.Binary;

namespace CUE4Parse.UE4.Assets.Exports.Texture;

internal static class BC7PrepDecoder
{
    private const int ModeCount = 10;
    private const uint SplitFlag = 1;
    private const uint ColorSpaceFlag = 1 << 16;

    private static readonly int[] ModeSizes = [16, 16, 16, 16, 16, 16, 16, 16, 16, 4];
    private static readonly int[] SplitPoints = [8, 8, 12, 12, 6, 8, 8, 12, 0, 0];

    private delegate void ModeDecoder(byte[] output, byte[] input, int firstOffset, int secondOffset,
        int firstStride, int secondStride, List<int> blockIndices, bool convertColorSpace);

    private static readonly ModeDecoder[] Decoders =
    [
        DecodeMode0, DecodeMode1, DecodeMode2, DecodeMode3, DecodeMode4,
        DecodeMode5, DecodeMode6, DecodeMode7
    ];

    public static byte[]? Decode(byte[] input, int[] modeCounts, uint flags)
    {
        if (!TryGetLayout(modeCounts, out var blockCount, out var payloadSize) || input.Length < payloadSize)
            return null;

        if (blockCount == 0)
            return [];

        var modeOffsets = new int[ModeCount + 1];
        var blockIndices = new List<int>[ModeCount];
        for (var mode = 0; mode < ModeCount; mode++)
        {
            modeOffsets[mode + 1] = modeOffsets[mode] + modeCounts[mode] * ModeSizes[mode];
            blockIndices[mode] = new List<int>(modeCounts[mode]);
        }

        ReadBlockModes(input, modeOffsets[ModeCount], blockIndices, blockCount);
        for (var mode = 0; mode < ModeCount; mode++)
        {
            if (blockIndices[mode].Count != modeCounts[mode])
                return null;
        }

        var output = new byte[blockCount * 16];
        var convertColorSpace = (flags & ColorSpaceFlag) != 0;
        for (var mode = 0; mode < ModeCount; mode++)
        {
            var indices = blockIndices[mode];
            if (indices.Count == 0)
                continue;

            if (mode == 8)
            {
                DecodeMode8(output, input, modeOffsets[mode], indices);
                continue;
            }

            if (mode == 9)
            {
                DecodeMode9(output, input, modeOffsets[mode], indices, convertColorSpace);
                continue;
            }

            var splitPoint = SplitPoints[mode];
            var firstStride = ModeSizes[mode];
            var secondStride = ModeSizes[mode];
            var secondOffset = modeOffsets[mode] + splitPoint;
            if ((flags & (SplitFlag << mode)) != 0)
            {
                firstStride = splitPoint;
                secondStride -= splitPoint;
                secondOffset = modeOffsets[mode] + splitPoint * modeCounts[mode];
            }

            Decoders[mode](output, input, modeOffsets[mode], secondOffset,
                firstStride, secondStride, indices, convertColorSpace);
        }

        return output;
    }

    private static bool TryGetLayout(int[] modeCounts, out int blockCount, out int payloadSize)
    {
        blockCount = 0;
        payloadSize = 0;
        if (modeCounts.Length != ModeCount)
            return false;

        long blocks = 0;
        long bytes = 0;
        for (var mode = 0; mode < ModeCount; mode++)
        {
            if (modeCounts[mode] < 0)
                return false;

            blocks += modeCounts[mode];
            bytes += (long) modeCounts[mode] * ModeSizes[mode];
        }

        bytes += (blocks + 1) / 2;
        if (blocks > int.MaxValue / 16 || bytes > int.MaxValue)
            return false;

        blockCount = (int) blocks;
        payloadSize = (int) bytes;
        return true;
    }

    private static void ReadBlockModes(byte[] input, int offset, List<int>[] blockIndices, int blockCount)
    {
        for (var block = 0; block < blockCount; block++)
        {
            var packedModes = input[offset + block / 2];
            var mode = (block & 1) == 0 ? packedModes & 0x0f : packedModes >> 4;
            if (mode < ModeCount)
                blockIndices[mode].Add(block);
        }
    }

    private static ulong Extract(ulong value, int offset, int count) =>
        count == 0 ? 0 : value >> offset & ((1UL << count) - 1);

    private static ulong PackedAdd(ulong left, ulong right, ulong highBitMask, ulong lowBitMask)
    {
        var lowBits = (left & lowBitMask) + (right & lowBitMask);
        return lowBits ^ ((left ^ right) & highBitMask);
    }

    private static ulong PackedAdd(ulong left, ulong right, ulong highBitMask) =>
        PackedAdd(left, right, highBitMask, ~highBitMask);

    private static void ConvertToRgb(ref ulong red, ref ulong green, ref ulong blue,
        ulong highBitMask, ulong lowBitMask)
    {
        var luminance = red;
        red = PackedAdd(luminance, green, highBitMask, lowBitMask);
        green = luminance;
        blue = PackedAdd(luminance, blue, highBitMask, lowBitMask);
    }

    private static void ConvertToRgb(ref ulong red, ref ulong green, ref ulong blue, ulong highBitMask) =>
        ConvertToRgb(ref red, ref green, ref blue, highBitMask, ~highBitMask);

    private static void ConvertToRgb(ref int red, ref int green, ref int blue)
    {
        var luminance = red;
        red = (luminance + green) & 0xff;
        green = luminance;
        blue = (luminance + blue) & 0xff;
    }

    private static ulong ConvertMode4ToRgb(ulong value)
    {
        var luminance = ((uint) value & 0x3ffUL) * (1UL + (1UL << 10) + (1UL << 20));
        var chroma = (value >> 10 & 0x3ff) | (value & 0xfffff00000UL);
        return PackedAdd(luminance, chroma, 0x8421084210UL);
    }

    private static uint Compact32To7x2(ulong value) =>
        (uint) value & 0x7f | (uint) (value >> 25) & 0x3f80;

    private static uint Compact24To7x3(ulong value) =>
        (uint) value & 0x7f | (uint) (value >> 17) & 0x3f80 | (uint) (value >> 34) & 0x1fc000;

    private static uint Compact24To7x2(ulong value) =>
        (uint) value & 0x7f | (uint) (value >> 17) & 0x3f80;

    private static uint Compact16To1x4(ulong value)
    {
        value &= 0x0001000100010001UL;
        value *= 0x0001000200040008UL;
        return (uint) (value >> 48);
    }

    private static uint Compact16To1x2(uint value) => (value & 1) | (value >> 15 & 2);

    private static ulong Compact16To5x4(ulong value)
    {
        value &= 0x001f001f001f001fUL;
        value = (value >> 11 | value) & 0x000003ff000003ffUL;
        return (value >> 22 | value) & 0xfffff;
    }

    private static uint Compact16To5x2(uint value) => (value & 0x1f) | (value >> 11 & 0x3e0);

    private static ulong Compact16To6x4(ulong value)
    {
        value = (value >> 10 | value) & 0x00000fff00000fffUL;
        return (value >> 20 | value) & 0xffffffUL;
    }

    private static ulong Compact8To7x8(ulong value)
    {
        const ulong low7Bits = 0x007f007f007f007fUL;
        value = (value & low7Bits << 8) >> 1 | value & low7Bits;
        const ulong low14Bits = 0x00003fff00003fffUL;
        value = (value & ~low14Bits) >> 2 | value & low14Bits;
        const ulong low28Bits = 0x000000000fffffffUL;
        return (value & ~low28Bits) >> 4 | value & low28Bits;
    }

    private static ulong Compact8To7x6(ulong value)
    {
        const ulong low7Bits = 0x007f007f007fUL;
        value = (value & low7Bits << 8) >> 1 | value & low7Bits;
        return value & 0x0000000ffffUL |
               value >> 2 & 0x0000fffc000UL |
               value >> 4 & 0x3fff0000000UL;
    }

    private static uint Compact8To1x8(ulong value)
    {
        value &= 0x0101010101010101UL;
        value *= 0x0102040810204080UL;
        return (uint) (value >> 56);
    }

    private static ulong Expand4To5x12(ulong value)
    {
        value = (value & 0x0000ffff00000000UL) << 8 |
                (value & 0x00000000ffff0000UL) << 4 |
                value & 0x000000000000ffffUL;
        const ulong lowGroups = 0x0000ff000ff000ffUL;
        value = (value & ~lowGroups) << 2 | value & lowGroups;
        return value + (value & 0x03c0f03c0f03c0f0UL);
    }

    private static ulong Expand4To5x4(ulong value)
    {
        value = (value & 0x0ff00) << 2 | value & 0x000ff;
        return value + (value & 0x3c0f0);
    }

    private static uint Expand2To6x4(uint value)
    {
        value = (value << 8 | value) & 0x0f00f;
        return (value << 4 | value) & 0xc30c3;
    }

    private static ulong Expand1To5x12(ulong value)
    {
        value = (value & 0xfff) * 0x100010001UL & 0xf0000f0000fUL;
        return value * 0x1111 & 0x084210842108421UL;
    }

    private static ulong Expand1To5x4(ulong value) =>
        ((uint) value & 0xf) * 0x1111u & 0x8421;

    private static void WriteBlock(byte[] output, int blockIndex, ulong low, ulong high)
    {
        var offset = blockIndex * 16;
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(offset), low);
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(offset + 8), high);
    }

    private static ulong ReadUInt64(byte[] input, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(input.AsSpan(offset));

    private static uint ReadUInt32(byte[] input, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(offset));

    private static ushort ReadUInt16(byte[] input, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(input.AsSpan(offset));

    private static void DecodeMode0(byte[] output, byte[] input, int firstOffset, int secondOffset,
        int firstStride, int secondStride, List<int> blockIndices, bool convertColorSpace)
    {
        for (var i = 0; i < blockIndices.Count; i++)
        {
            var lowInput = ReadUInt64(input, firstOffset + i * firstStride);
            var highInput = ReadUInt64(input, secondOffset + i * secondStride);
            var red = Extract(lowInput, 0, 24);
            var green = Extract(lowInput, 24, 24);
            var blue = Extract(lowInput, 48, 16) | Extract(highInput, 0, 8) << 16;
            var partition = Extract(highInput, 8, 4);

            if (convertColorSpace)
                ConvertToRgb(ref red, ref green, ref blue, 0x888888);

            var low = 1UL | partition << 1 | red << 5 | green << 29 | blue << 53;
            var high = blue >> 11 | highInput & ~0x1fffUL;
            WriteBlock(output, blockIndices[i], low, high);
        }
    }

    private static void DecodeMode1(byte[] output, byte[] input, int firstOffset, int secondOffset,
        int firstStride, int secondStride, List<int> blockIndices, bool convertColorSpace)
    {
        for (var i = 0; i < blockIndices.Count; i++)
        {
            var endpoints = ReadUInt64(input, firstOffset + i * firstStride);
            var extra = ReadUInt64(input, secondOffset + i * secondStride);
            var red = Compact16To6x4(endpoints & 0x003f003f003f003fUL);
            var green = Compact16To6x4(endpoints >> 5 & 0x003e003e003e003eUL);
            var blue = Compact16To6x4(endpoints >> 10 & 0x003e003e003e003eUL);
            var expanded = Expand2To6x4((uint) extra & 0xff);
            green |= expanded & 0x41041;
            blue |= expanded >> 1 & 0x41041;

            if (convertColorSpace)
                ConvertToRgb(ref red, ref green, ref blue, 0x820820);

            var low = 0x2UL | extra >> 6 & 0xfc | red << 8 | green << 32 | blue << 56;
            var high = blue >> 8 | extra & ~0xffffUL;
            WriteBlock(output, blockIndices[i], low, high);
        }
    }

    private static void DecodeMode2(byte[] output, byte[] input, int firstOffset, int secondOffset,
        int firstStride, int secondStride, List<int> blockIndices, bool convertColorSpace)
    {
        for (var i = 0; i < blockIndices.Count; i++)
        {
            var firstEndpoint = ReadUInt64(input, firstOffset + i * firstStride);
            var secondEndpoint = ReadUInt32(input, firstOffset + i * firstStride + 8);
            var indices = ReadUInt32(input, secondOffset + i * secondStride);
            var red = Compact16To5x4(firstEndpoint >> 1) | (ulong) Compact16To5x2(secondEndpoint >> 1) << 20;
            var green = Compact16To5x4(firstEndpoint >> 6) | (ulong) Compact16To5x2(secondEndpoint >> 6) << 20;
            var blue = Compact16To5x4(firstEndpoint >> 11) | (ulong) Compact16To5x2(secondEndpoint >> 11) << 20;
            var partition = Compact16To1x4(firstEndpoint) | (ulong) Compact16To1x2(secondEndpoint) << 4;

            if (convertColorSpace)
                ConvertToRgb(ref red, ref green, ref blue, 0x21084210);

            var low = 0x4UL | partition << 3 | red << 9 | green << 39;
            var high = green >> 25 | blue << 5 | (ulong) (indices & ~7u) << 32;
            WriteBlock(output, blockIndices[i], low, high);
        }
    }

    private static void DecodeMode3(byte[] output, byte[] input, int firstOffset, int secondOffset,
        int firstStride, int secondStride, List<int> blockIndices, bool convertColorSpace)
    {
        for (var i = 0; i < blockIndices.Count; i++)
        {
            var firstEndpoint = ReadUInt64(input, firstOffset + i * firstStride);
            var secondEndpoint = ReadUInt32(input, firstOffset + i * firstStride + 8);
            var indices = ReadUInt32(input, secondOffset + i * secondStride);
            var red = Compact24To7x3(firstEndpoint) | ((ulong) secondEndpoint & 0x007f00) << 13;
            var green = Compact24To7x3(firstEndpoint >> 8) | ((ulong) secondEndpoint & 0x7f0000) << 5;
            var blue = Compact24To7x2(firstEndpoint >> 16) | (ulong) Compact24To7x2(secondEndpoint) << 14;
            var partition = Compact8To1x8(firstEndpoint >> 7);

            if (convertColorSpace)
                ConvertToRgb(ref red, ref green, ref blue, 0x8102040);

            var low = 0x8UL | (partition & 0x3f) << 4 | red << 10 | green << 38;
            var high = green >> 26 | blue << 2 | (ulong) (partition & 0xc0) << 24 | (ulong) indices << 32;
            WriteBlock(output, blockIndices[i], low, high);
        }
    }

    private static void DecodeMode4(byte[] output, byte[] input, int firstOffset, int secondOffset,
        int firstStride, int secondStride, List<int> blockIndices, bool convertColorSpace)
    {
        for (var i = 0; i < blockIndices.Count; i++)
        {
            var firstPosition = firstOffset + i * firstStride;
            var lowInput = ReadUInt32(input, firstPosition) | (ulong) ReadUInt16(input, firstPosition + 4) << 32;
            var secondPosition = secondOffset + i * secondStride;
            var highInputLow = ReadUInt16(input, secondPosition);
            var highInputHigh = ReadUInt64(input, secondPosition + 2);
            var endpoints = Extract(lowInput, 2, 40);

            if (convertColorSpace)
                endpoints = ConvertMode4ToRgb(endpoints);

            var rotation = Extract(highInputLow, 0, 2);
            var shift = (int) ((0UL - rotation & 3) * 10);
            var exchangeMask = (endpoints ^ endpoints << shift) & 0xffc0000000UL;
            endpoints ^= exchangeMask;
            endpoints ^= exchangeMask >> shift;
            endpoints += endpoints & 0x0ffc0000000UL;
            endpoints += endpoints & 0x1f000000000UL;
            endpoints |= (((uint) lowInput & 3) * 0x21u & 0x41u) * 1UL << 30;

            var low = 0x10UL | rotation << 5 | Extract(lowInput, 42, 1) << 7 |
                      endpoints << 8 | Extract(highInputLow, 2, 14) << 50;
            WriteBlock(output, blockIndices[i], low, highInputHigh);
        }
    }

    private static void DecodeMode5(byte[] output, byte[] input, int firstOffset, int secondOffset,
        int firstStride, int secondStride, List<int> blockIndices, bool convertColorSpace)
    {
        for (var i = 0; i < blockIndices.Count; i++)
        {
            var endpoints = ReadUInt64(input, firstOffset + i * firstStride);
            var indices = ReadUInt64(input, secondOffset + i * secondStride);
            var rotation = (uint) indices & 3;
            var low = 0x20UL;

            if (convertColorSpace)
            {
                var red = endpoints & 0x000000fe000000feUL;
                var green = endpoints >> 8 & 0x000000fe000000feUL;
                var blue = endpoints >> 16 & 0x000000fe000000feUL;
                ConvertToRgb(ref red, ref green, ref blue, 0x8000000080UL, 0x7e0000007eUL);
                endpoints = red | green << 8 | blue << 16 | endpoints & 0xff010101ff010101UL;

                var shift = (int) ((0u - rotation & 3) << 3);
                var exchangeMask = (endpoints ^ endpoints << shift) & 0xff000000ff000000UL;
                endpoints ^= exchangeMask;
                endpoints ^= exchangeMask >> shift;

                low |= (ulong) rotation << 6;
                low |= (ulong) Compact32To7x2(endpoints >> 1) << 8;
                low |= (ulong) Compact32To7x2(endpoints >> 9) << 22;
                low |= (ulong) Compact32To7x2(endpoints >> 17) << 36;
                low |= (endpoints >> 24 & 0xff) << 50;
                low |= (endpoints >> 56 & 0x3f) << 58;
            }
            else
            {
                var shift = (int) ((0u - rotation & 3) << 4);
                var exchangeMask = (endpoints ^ endpoints << shift) & 0xffff000000000000UL;
                endpoints ^= exchangeMask;
                endpoints ^= exchangeMask >> shift;

                low |= (ulong) rotation << 6;
                low |= Compact8To7x6(endpoints >> 1) << 8;
                low |= (endpoints >> 48 & 0xff) << 50;
                low |= (endpoints >> 56 & 0x3f) << 58;
            }

            var high = endpoints >> 62 & 3 | indices & ~3UL;
            WriteBlock(output, blockIndices[i], low, high);
        }
    }

    private static void DecodeMode6(byte[] output, byte[] input, int firstOffset, int secondOffset,
        int firstStride, int secondStride, List<int> blockIndices, bool convertColorSpace)
    {
        for (var i = 0; i < blockIndices.Count; i++)
        {
            var endpoints = ReadUInt64(input, firstOffset + i * firstStride);
            var indices = ReadUInt64(input, secondOffset + i * secondStride);
            var low = 0x40UL;

            if (convertColorSpace)
            {
                var red = (ulong) Compact32To7x2(endpoints);
                var green = (ulong) Compact32To7x2(endpoints >> 8);
                var blue = (ulong) Compact32To7x2(endpoints >> 16);
                var alpha = (ulong) Compact32To7x2(endpoints >> 24);
                ConvertToRgb(ref red, ref green, ref blue, 0x2040);
                low |= red << 7 | green << 21 | blue << 35 | alpha << 49;
            }
            else
            {
                low |= Compact8To7x8(endpoints) << 7;
            }

            low |= endpoints & 1UL << 63;
            WriteBlock(output, blockIndices[i], low, indices);
        }
    }

    private static void DecodeMode7(byte[] output, byte[] input, int firstOffset, int secondOffset,
        int firstStride, int secondStride, List<int> blockIndices, bool convertColorSpace)
    {
        for (var i = 0; i < blockIndices.Count; i++)
        {
            var endpoints = ReadUInt64(input, firstOffset + i * firstStride);
            var rest = (ulong) ReadUInt32(input, firstOffset + i * firstStride + 8) |
                       (ulong) ReadUInt32(input, secondOffset + i * secondStride) << 32;
            ulong low;
            ulong high;

            if (convertColorSpace)
            {
                var red = Compact16To5x4(endpoints >> 1);
                var green = Compact16To5x4(endpoints >> 6);
                var blue = Compact16To5x4(endpoints >> 11);
                var pBits = Compact16To1x4(endpoints);
                var alpha = Extract(rest, 8, 20);
                ConvertToRgb(ref red, ref green, ref blue, 0x84210);

                low = 0x80UL | Extract(rest, 28, 6) << 8 | red << 14 | green << 34 | blue << 54;
                high = blue >> 10 | alpha << 10 | pBits << 30;
            }
            else
            {
                var pBits = Extract(rest, 8, 4);
                var rgb = Expand4To5x12(endpoints) << 1 | Expand1To5x12(rest >> 12);
                var alpha = Expand4To5x4(endpoints >> 48) << 1 | Expand1To5x4(rest >> 24);
                low = 0x80UL | Extract(rest, 28, 6) << 8 | rgb << 14;
                high = rgb >> 50 | alpha << 10 | pBits << 30;
            }

            high |= rest & ~0x3FFFFFFFFUL;
            WriteBlock(output, blockIndices[i], low, high);
        }
    }

    private static void DecodeMode8(byte[] output, byte[] input, int offset, List<int> blockIndices)
    {
        for (var i = 0; i < blockIndices.Count; i++)
            input.AsSpan(offset + i * 16, 16).CopyTo(output.AsSpan(blockIndices[i] * 16));
    }

    private static void DecodeMode9(byte[] output, byte[] input, int offset,
        List<int> blockIndices, bool convertColorSpace)
    {
        const ulong highBitMask = (0x40UL << 8) | (0x40UL << 22) | (0x40UL << 36);
        const ulong lowBitMask = (0x7fUL << 8) | (0x7fUL << 22) | (0x7fUL << 36);

        var block = 0;
        var position = offset;
        while (block < blockIndices.Count)
        {
            var red = (int) input[position];
            var green = (int) input[position + 1];
            var blue = (int) input[position + 2];
            if (convertColorSpace)
                ConvertToRgb(ref red, ref green, ref blue);

            var color = (ulong) red << 8 | (ulong) green << 22 | (ulong) blue << 36;
            var shiftedColor = color << 6;
            color = (color >> 1 & lowBitMask) - (color & ~lowBitMask);
            color += shiftedColor + (shiftedColor & highBitMask);
            color |= (ulong) input[position + 3] << 50 | 0x20;

            var repeatedValue = ReadUInt32(input, position);
            do
            {
                WriteBlock(output, blockIndices[block++], color, 0xaaaaaaacUL);
                position += 4;
            } while (block < blockIndices.Count && ReadUInt32(input, position) == repeatedValue);
        }
    }
}
