using System.Text;
using CUE4Parse.UE4.Objects.Core.i18N;

namespace Lokasync.Adapters.Internal;

internal sealed record LocResNamespaceWriteModel(
    string Name,
    uint StrHash,
    IReadOnlyList<LocResEntryWriteModel> Entries);

internal sealed record LocResEntryWriteModel(
    string Key,
    uint KeyHash,
    uint SourceStringHash,
    string Text);

internal static class LocResWriter
{
    public const uint MagicA = 0x7574140Eu;
    public const uint MagicB = 0xFC034A67u;
    public const uint MagicC = 0x9D90154Au;
    public const uint MagicD = 0x1B7F37C3u;

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    public static ELocResVersion DetectVersion(ReadOnlySpan<byte> rawBytes)
    {
        if (rawBytes.Length >= 17 &&
            BitConverter.ToUInt32(rawBytes[0..4]) == MagicA &&
            BitConverter.ToUInt32(rawBytes[4..8]) == MagicB &&
            BitConverter.ToUInt32(rawBytes[8..12]) == MagicC &&
            BitConverter.ToUInt32(rawBytes[12..16]) == MagicD)
        {
            return (ELocResVersion)rawBytes[16];
        }

        return ELocResVersion.Legacy;
    }

    public static byte[] Write(
        ELocResVersion targetVersion,
        IReadOnlyList<LocResNamespaceWriteModel> namespaces)
    {
        ELocResVersion version = targetVersion == ELocResVersion.Legacy
            ? ELocResVersion.Optimized_CRC32
            : targetVersion;

        using MemoryStream ms = new();
        using BinaryWriter bw = new(ms, Encoding.UTF8, leaveOpen: true);

        bw.Write(MagicA);
        bw.Write(MagicB);
        bw.Write(MagicC);
        bw.Write(MagicD);
        bw.Write((byte)version);

        long offsetPlaceholderPos = ms.Position;
        bw.Write(-1L);

        uint totalEntriesCount = 0;
        foreach (LocResNamespaceWriteModel ns in namespaces)
        {
            totalEntriesCount += (uint)ns.Entries.Count;
        }

        if (version >= ELocResVersion.Optimized_CRC32)
        {
            bw.Write(totalEntriesCount);
        }

        bw.Write((uint)namespaces.Count);

        List<(string Text, int RefCount)> stringLut = [];
        Dictionary<string, int> lutIndexByText = new(StringComparer.Ordinal);

        foreach (LocResNamespaceWriteModel ns in namespaces)
        {
            if (version >= ELocResVersion.Optimized_CRC32)
            {
                uint nsHash = ns.StrHash != 0 ? ns.StrHash : ComputeCrc32(ns.Name);
                bw.Write(nsHash);
            }

            WriteFString(bw, ns.Name);
            bw.Write((uint)ns.Entries.Count);

            foreach (LocResEntryWriteModel entry in ns.Entries)
            {
                if (version >= ELocResVersion.Optimized_CRC32)
                {
                    uint keyHash = entry.KeyHash != 0 ? entry.KeyHash : ComputeCrc32(entry.Key);
                    bw.Write(keyHash);
                }

                WriteFString(bw, entry.Key);
                bw.Write(entry.SourceStringHash);

                int lutIndex = AddToStringLut(stringLut, lutIndexByText, entry.Text);
                bw.Write(lutIndex);
            }
        }

        long lutOffset = ms.Position;
        bw.Write(stringLut.Count);
        foreach ((string text, int refCount) in stringLut)
        {
            WriteFString(bw, text);
            if (version >= ELocResVersion.Optimized_CRC32)
            {
                bw.Write(refCount);
            }
        }

        ms.Position = offsetPlaceholderPos;
        bw.Write(lutOffset);
        bw.Flush();

        return ms.ToArray();
    }

    public static void WriteFString(BinaryWriter bw, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            bw.Write(0);
            return;
        }

        bool isAscii = true;
        foreach (char ch in value)
        {
            if (ch > 0x7F)
            {
                isAscii = false;
                break;
            }
        }

        if (isAscii)
        {
            byte[] ascii = Encoding.ASCII.GetBytes(value);
            bw.Write(ascii.Length + 1);
            bw.Write(ascii);
            bw.Write((byte)0);
        }
        else
        {
            byte[] utf16 = Encoding.Unicode.GetBytes(value);
            bw.Write(-(value.Length + 1));
            bw.Write(utf16);
            bw.Write((ushort)0);
        }
    }

    private static int AddToStringLut(
        List<(string Text, int RefCount)> stringLut,
        Dictionary<string, int> lutIndexByText,
        string text)
    {
        if (lutIndexByText.TryGetValue(text, out int existingIdx))
        {
            (string existingText, int count) = stringLut[existingIdx];
            stringLut[existingIdx] = (existingText, count + 1);
            return existingIdx;
        }

        int newIndex = stringLut.Count;
        stringLut.Add((text, 1));
        lutIndexByText[text] = newIndex;
        return newIndex;
    }

    private static uint ComputeCrc32(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in bytes)
        {
            crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] BuildCrc32Table()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }
}
