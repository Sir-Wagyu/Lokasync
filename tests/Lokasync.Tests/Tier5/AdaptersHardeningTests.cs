using System.IO;
using System.Security.Cryptography;
using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Pak;
using CUE4Parse.UE4.Readers;
using Lokasync.Adapters;
using Lokasync.Core.Models;
using Lokasync.Tests.Adapters;
using Xunit;

namespace Lokasync.Tests.Tier5;

public sealed class AdaptersHardeningTests : IDisposable
{
    private readonly AdapterFixtureHelper _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task UnityAdapter_PatchAsyncWithNullAssetPath_PatchesViaEntryKeyFallback()
    {
        // Arrange
        _fixture.CreateUnityFixture();
        UnityAdapter adapter = new();
        List<SubtitleEntry> extracted = await adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None);

        List<SubtitleEntry> patched = extracted
            .Select(e => e.RawText switch
            {
                "Hello, <color=#FFF>hero</color>!" => e with
                {
                    AssetPath = null,
                    Status = EntryStatus.Translated,
                    FinalPatchedText = "Halo, <color=#FFF>pahlawan</color>!"
                },
                "Start Game" => e with
                {
                    AssetPath = null,
                    Status = EntryStatus.Translated,
                    FinalPatchedText = "Mulai Permainan"
                },
                "Welcome to Aether!" => e with
                {
                    AssetPath = null,
                    Status = EntryStatus.Translated,
                    FinalPatchedText = "Selamat datang di Aether!"
                },
                "Byte-backed subtitle {0}" => e with
                {
                    AssetPath = null,
                    Status = EntryStatus.Translated,
                    FinalPatchedText = "Subtitel byte {0}"
                },
                _ => e with { AssetPath = null }
            })
            .ToList();

        // Act
        await adapter.PatchAsync(_fixture.RootPath, patched, CancellationToken.None);
        List<SubtitleEntry> reExtracted = await adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None);

        // Assert
        Assert.Contains(reExtracted, e => e.RawText == "Halo, <color=#FFF>pahlawan</color>!");
        Assert.Contains(reExtracted, e => e.RawText == "Mulai Permainan");
        Assert.Contains(reExtracted, e => e.RawText == "Selamat datang di Aether!");
        Assert.Contains(reExtracted, e => e.RawText == "Subtitel byte {0}");
    }

    [Fact]
    public async Task UnityAdapter_ExtractAndPatch_EmptyNameAndMissingPathIdAndUtf8BomBytes_HandlesSafely()
    {
        // Arrange
        _fixture.CreateSubDir("CustomGame_Data");
        string resourcesPath = Path.Combine(_fixture.RootPath, "CustomGame_Data", "resources.assets");
        string sharedPath = Path.Combine(_fixture.RootPath, "CustomGame_Data", "sharedassets0.assets");
        WriteCustomUnityStringAssets(resourcesPath);
        WriteCustomUnityByteAssets(sharedPath);

        UnityAdapter adapter = new();

        // Act
        List<SubtitleEntry> extracted = await adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None);
        byte[] resourcesBeforeMissingPatch = await File.ReadAllBytesAsync(resourcesPath);
        byte[] sharedBeforeMissingPatch = await File.ReadAllBytesAsync(sharedPath);

        SubtitleEntry missingPathIdEntry = new(
            Id: 0,
            EntryKey: "CustomGame_Data/resources.assets::TextAsset::99999::Missing",
            SourceFile: "CustomGame_Data/resources.assets",
            AssetPath: "TextAsset/99999/Missing",
            RawText: "Missing",
            MaskedText: "Missing",
            Placeholders: new Dictionary<string, string>(),
            TranslatedText: "Hilang",
            FinalPatchedText: "Hilang",
            Status: EntryStatus.Translated,
            HasOverflowWarning: false);

        await adapter.PatchAsync(_fixture.RootPath, [missingPathIdEntry], CancellationToken.None);
        byte[] resourcesAfterMissingPatch = await File.ReadAllBytesAsync(resourcesPath);
        byte[] sharedAfterMissingPatch = await File.ReadAllBytesAsync(sharedPath);

        // Assert
        SubtitleEntry emptyNameEntry = extracted.Single(e => e.RawText == "Nameless text");
        Assert.Equal("TextAsset/42/TextAsset_42", emptyNameEntry.AssetPath);
        Assert.Equal("CustomGame_Data/resources.assets::TextAsset::42::TextAsset_42", emptyNameEntry.EntryKey);

        SubtitleEntry bomEntry = extracted.Single(e => e.EntryKey.EndsWith("::43::bom_bytes", StringComparison.Ordinal));
        Assert.Equal("BOM text", bomEntry.RawText);

        Assert.Equal(resourcesBeforeMissingPatch, resourcesAfterMissingPatch);
        Assert.Equal(sharedBeforeMissingPatch, sharedAfterMissingPatch);
    }

    [Fact]
    public async Task UnrealAdapter_CompactLocResAndEntryKeyFallbackAndMultiLocRes_RoundTripsViaCue4Parse()
    {
        // Arrange
        string paksDir = _fixture.CreateSubDir("Content/Paks");
        string basePakPath = Path.Combine(paksDir, "pakchunk0-WindowsNoEditor.pak");
        string modsPakPath = Path.Combine(paksDir, "~mods", UnrealAdapter.ModsPakFileName);

        byte[] enCompactBytes = AdapterFixtureHelper.BuildLocresBytes(
            ELocResVersion.Compact,
            [("Dialog", "CompactLine", 0x11223344u, "Hello Compact")]);

        byte[] frOptimizedBytes = AdapterFixtureHelper.BuildLocresBytes(
            ELocResVersion.Optimized_CRC32,
            [("Dialog", "FrenchLine", 0x55667788u, "Bonjour")]);

        WriteMultiFilePakV8(
            basePakPath,
            mountPoint: "../../../Game/Content/",
            files:
            [
                ("Localization/Game/en/Game.locres", enCompactBytes),
                ("Localization/Game/fr/Game.locres", frOptimizedBytes)
            ]);

        UnrealAdapter adapter = new();
        List<SubtitleEntry> extracted = await adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None);

        List<SubtitleEntry> enOnlyPatched = extracted
            .Where(e => e.EntryKey.Contains("/en/Game.locres::", StringComparison.OrdinalIgnoreCase))
            .Select(e => e with
            {
                AssetPath = null,
                Status = EntryStatus.Translated,
                FinalPatchedText = "Halo Compact — ✨"
            })
            .ToList();

        // Act
        await adapter.PatchAsync(_fixture.RootPath, enOnlyPatched, CancellationToken.None);

        // Assert
        Assert.True(File.Exists(modsPakPath));
        using PakFileReader pakReader = new(modsPakPath);
        pakReader.Mount(StringComparer.OrdinalIgnoreCase);

        Assert.True(pakReader.Files.TryGetValue("Game/Content/Localization/Game/en/Game.locres", out GameFile? enFile));
        Assert.False(pakReader.Files.ContainsKey("Game/Content/Localization/Game/fr/Game.locres"));

        using FArchive archive = enFile!.CreateReader();
        FTextLocalizationResource parsed = new(archive);
        FEntry? enEntry = null;
        foreach (KeyValuePair<FTextKey, Dictionary<FTextKey, FEntry>> ns in parsed.Entries)
        {
            if (ns.Key.Str == "Dialog")
            {
                foreach (KeyValuePair<FTextKey, FEntry> kv in ns.Value)
                {
                    if (kv.Key.Str == "CompactLine")
                    {
                        enEntry = kv.Value;
                    }
                }
            }
        }

        Assert.NotNull(enEntry);
        Assert.Equal("Halo Compact — ✨", enEntry!.LocalizedString);
    }

    [Fact]
    public async Task LooseFileAdapter_EntryKeyFallbackMixedXmlBomJsoncAndEscapedPointers_PatchesInPlace()
    {
        // Arrange
        _fixture.WriteTextFile(
            "localization/dialog.json",
            "// JSONC comment\n{\n  \"a~b/c\": \"Original Pointer\",\n}\n",
            withUtf8Bom: true);

        _fixture.WriteTextFile(
            "data/mixed.xml",
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<root><line>Hello <b>Bob</b> world</line><line>Second line</line></root>\n",
            withUtf8Bom: true);

        LooseFileAdapter adapter = new();
        List<SubtitleEntry> extracted = await adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None);

        List<SubtitleEntry> patched = extracted
            .Select(e => e.RawText switch
            {
                "Original Pointer" => e with { AssetPath = null, Status = EntryStatus.Translated, FinalPatchedText = "Updated Pointer" },
                "Hello " => e with { AssetPath = null, Status = EntryStatus.Translated, FinalPatchedText = "Halo " },
                " world" => e with { AssetPath = null, Status = EntryStatus.Translated, FinalPatchedText = " dunia" },
                "Second line" => e with { AssetPath = null, Status = EntryStatus.Translated, FinalPatchedText = "Baris kedua" },
                _ => e with { AssetPath = null }
            })
            .ToList();

        // Act
        await adapter.PatchAsync(_fixture.RootPath, patched, CancellationToken.None);

        // Assert
        byte[] jsonBytes = _fixture.ReadRawBytes("localization/dialog.json");
        byte[] xmlBytes = _fixture.ReadRawBytes("data/mixed.xml");
        Assert.True(jsonBytes.Length >= 3 && jsonBytes[0] == 0xEF && jsonBytes[1] == 0xBB && jsonBytes[2] == 0xBF);
        Assert.True(xmlBytes.Length >= 3 && xmlBytes[0] == 0xEF && xmlBytes[1] == 0xBB && xmlBytes[2] == 0xBF);

        string jsonText = _fixture.ReadTextFile("localization/dialog.json");
        string xmlText = _fixture.ReadTextFile("data/mixed.xml");
        Assert.Contains("\"a~b/c\": \"Updated Pointer\"", jsonText, StringComparison.Ordinal);
        Assert.Contains("<line>Halo <b>Bob</b> dunia</line>", xmlText, StringComparison.Ordinal);
        Assert.Contains("<line>Baris kedua</line>", xmlText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RpgMakerAdapter_EntryKeyFallbackAndOutOfRangeIndices_PatchesValidAndSkipsInvalidSafely()
    {
        // Arrange
        _fixture.CreateRpgMakerFixture(isMv: false);
        RpgMakerAdapter adapter = new();
        List<SubtitleEntry> extracted = await adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None);

        SubtitleEntry firstMapLine = extracted.First(e =>
            e.SourceFile == "data/Map001.json" &&
            e.RawText.Contains("Welcome to Aether Harbor", StringComparison.Ordinal));

        SubtitleEntry validFallback = firstMapLine with
        {
            AssetPath = null,
            Status = EntryStatus.Translated,
            FinalPatchedText = "Halo RPG"
        };

        SubtitleEntry outOfRangeEntry = new(
            Id: 0,
            EntryKey: "data/Map001.json::events[999].pages[0].list[0].parameters[0]",
            SourceFile: "data/Map001.json",
            AssetPath: "events[999].pages[0].list[0].parameters[0]",
            RawText: "Ghost",
            MaskedText: "Ghost",
            Placeholders: new Dictionary<string, string>(),
            TranslatedText: "Hantu",
            FinalPatchedText: "Hantu",
            Status: EntryStatus.Translated,
            HasOverflowWarning: false);

        // Act
        await adapter.PatchAsync(_fixture.RootPath, [validFallback, outOfRangeEntry], CancellationToken.None);

        // Assert
        string patchedMap = _fixture.ReadTextFile("data/Map001.json");
        Assert.Contains("Halo RPG", patchedMap, StringComparison.Ordinal);
        Assert.DoesNotContain("Hantu", patchedMap, StringComparison.Ordinal);
    }

    [Fact]
    public void EngineFingerprinter_TryGetAdapterByNameAliasAndPriority_ResolvesDeterministically()
    {
        // Arrange
        EngineFingerprinter fingerprinter = new();

        // Act
        bool unrealAliasFound = fingerprinter.TryGetAdapterByName("Unreal Engine", out var unrealAdapter);
        bool rpgCaseInsensitiveFound = fingerprinter.TryGetAdapterByName("rpgmaker", out var rpgAdapter);
        bool unknownFound = fingerprinter.TryGetAdapterByName("unknown", out var unknownAdapter);
        bool nullFound = fingerprinter.TryGetAdapterByName(null, out var nullAdapter);

        // Assert
        Assert.True(unrealAliasFound);
        Assert.IsType<UnrealAdapter>(unrealAdapter);

        Assert.True(rpgCaseInsensitiveFound);
        Assert.IsType<RpgMakerAdapter>(rpgAdapter);

        Assert.False(unknownFound);
        Assert.Null(unknownAdapter);
        Assert.False(nullFound);
        Assert.Null(nullAdapter);
    }

    private static void WriteCustomUnityStringAssets(string outputPath)
    {
        AssetsFile afile = CreateBlankAssetsFile();

        TypeTreeType stringType = BuildTextAssetStringTypeTree();
        afile.Metadata.TypeTreeTypes.Add(stringType);

        AssetTypeTemplateField stringTemplate = new();
        stringTemplate.FromTypeTree(stringType);
        AssetTypeValueField namelessField = ValueBuilder.DefaultValueFieldFromTemplate(stringTemplate);
        namelessField["m_Name"].AsString = string.Empty;
        namelessField["m_Script"].AsString = "Nameless text";
        AssetFileInfo namelessInfo = AssetFileInfo.Create(afile, 42, (int)AssetClassID.TextAsset, null, true);
        namelessInfo.SetNewData(namelessField);
        afile.Metadata.AddAssetInfo(namelessInfo);

        using AssetsFileWriter writer = new(outputPath);
        afile.Write(writer);
    }

    private static void WriteCustomUnityByteAssets(string outputPath)
    {
        AssetsFile afile = CreateBlankAssetsFile();

        TypeTreeType bytesType = BuildTextAssetByteArrayTypeTree();
        afile.Metadata.TypeTreeTypes.Add(bytesType);

        AssetTypeTemplateField bytesTemplate = new();
        bytesTemplate.FromTypeTree(bytesType);
        AssetTypeValueField bomField = ValueBuilder.DefaultValueFieldFromTemplate(bytesTemplate);
        bomField["m_Name"].AsString = "bom_bytes";
        bomField["m_Script"].AsByteArray = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("BOM text")];
        AssetFileInfo bomInfo = AssetFileInfo.Create(afile, 43, (int)AssetClassID.TextAsset, null, true);
        bomInfo.SetNewData(bomField);
        afile.Metadata.AddAssetInfo(bomInfo);

        using AssetsFileWriter writer = new(outputPath);
        afile.Write(writer);
    }

    private static AssetsFile CreateBlankAssetsFile() =>
        new()
        {
            Header = new AssetsFileHeader { Version = 22, Endianness = false },
            Metadata = new AssetsFileMetadata
            {
                UnityVersion = "2022.3.10f1",
                TargetPlatform = 19,
                TypeTreeEnabled = true,
                TypeTreeTypes = [],
                AssetInfos = [],
                ScriptTypes = [],
                Externals = [],
                RefTypes = [],
                UserInformation = string.Empty
            }
        };

    private static TypeTreeType BuildTextAssetStringTypeTree()
    {
        using MemoryStream sb = new();
        uint sTextAsset = AddString(sb, "TextAsset");
        uint sBase = AddString(sb, "Base");
        uint sString = AddString(sb, "string");
        uint sMName = AddString(sb, "m_Name");
        uint sArray = AddString(sb, "Array");
        uint sInt = AddString(sb, "int");
        uint sSize = AddString(sb, "size");
        uint sChar = AddString(sb, "char");
        uint sData = AddString(sb, "data");
        uint sMScript = AddString(sb, "m_Script");

        List<TypeTreeNode> nodes =
        [
            new() { Version = 1, Level = 0, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sTextAsset, NameStrOffset = sBase, ByteSize = -1, Index = 0, MetaFlags = 0 },
            new() { Version = 1, Level = 1, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sString, NameStrOffset = sMName, ByteSize = -1, Index = 1, MetaFlags = 0x4000 },
            new() { Version = 1, Level = 2, TypeFlags = TypeTreeNodeFlags.Array, TypeStrOffset = sArray, NameStrOffset = sArray, ByteSize = -1, Index = 2, MetaFlags = 0x4000 },
            new() { Version = 1, Level = 3, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sInt, NameStrOffset = sSize, ByteSize = 4, Index = 3, MetaFlags = 0 },
            new() { Version = 1, Level = 3, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sChar, NameStrOffset = sData, ByteSize = 1, Index = 4, MetaFlags = 0 },
            new() { Version = 1, Level = 1, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sString, NameStrOffset = sMScript, ByteSize = -1, Index = 5, MetaFlags = 0x4000 },
            new() { Version = 1, Level = 2, TypeFlags = TypeTreeNodeFlags.Array, TypeStrOffset = sArray, NameStrOffset = sArray, ByteSize = -1, Index = 6, MetaFlags = 0x4000 },
            new() { Version = 1, Level = 3, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sInt, NameStrOffset = sSize, ByteSize = 4, Index = 7, MetaFlags = 0 },
            new() { Version = 1, Level = 3, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sChar, NameStrOffset = sData, ByteSize = 1, Index = 8, MetaFlags = 0 }
        ];

        return BuildTypeTreeType((int)AssetClassID.TextAsset, 0xffff, nodes, sb.ToArray());
    }

    private static TypeTreeType BuildTextAssetByteArrayTypeTree()
    {
        using MemoryStream sb = new();
        uint sTextAsset = AddString(sb, "TextAsset");
        uint sBase = AddString(sb, "Base");
        uint sString = AddString(sb, "string");
        uint sMName = AddString(sb, "m_Name");
        uint sArray = AddString(sb, "Array");
        uint sInt = AddString(sb, "int");
        uint sSize = AddString(sb, "size");
        uint sChar = AddString(sb, "char");
        uint sData = AddString(sb, "data");
        uint sTypelessData = AddString(sb, "TypelessData");
        uint sMScript = AddString(sb, "m_Script");
        uint sUInt8 = AddString(sb, "UInt8");

        List<TypeTreeNode> nodes =
        [
            new() { Version = 1, Level = 0, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sTextAsset, NameStrOffset = sBase, ByteSize = -1, Index = 0, MetaFlags = 0 },
            new() { Version = 1, Level = 1, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sString, NameStrOffset = sMName, ByteSize = -1, Index = 1, MetaFlags = 0x4000 },
            new() { Version = 1, Level = 2, TypeFlags = TypeTreeNodeFlags.Array, TypeStrOffset = sArray, NameStrOffset = sArray, ByteSize = -1, Index = 2, MetaFlags = 0x4000 },
            new() { Version = 1, Level = 3, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sInt, NameStrOffset = sSize, ByteSize = 4, Index = 3, MetaFlags = 0 },
            new() { Version = 1, Level = 3, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sChar, NameStrOffset = sData, ByteSize = 1, Index = 4, MetaFlags = 0 },
            new() { Version = 1, Level = 1, TypeFlags = TypeTreeNodeFlags.Array, TypeStrOffset = sTypelessData, NameStrOffset = sMScript, ByteSize = -1, Index = 5, MetaFlags = 0x4000 },
            new() { Version = 1, Level = 2, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sInt, NameStrOffset = sSize, ByteSize = 4, Index = 6, MetaFlags = 0 },
            new() { Version = 1, Level = 2, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sUInt8, NameStrOffset = sData, ByteSize = 1, Index = 7, MetaFlags = 0 }
        ];

        return BuildTypeTreeType((int)AssetClassID.TextAsset, 0xffff, nodes, sb.ToArray());
    }

    private static TypeTreeType BuildTypeTreeType(int typeId, ushort scriptTypeIndex, List<TypeTreeNode> nodes, byte[] stringBuffer) =>
        new()
        {
            TypeId = typeId,
            IsStrippedType = false,
            ScriptTypeIndex = scriptTypeIndex,
            ScriptIdHash = Hash128.NewBlankHash(),
            TypeHash = Hash128.NewBlankHash(),
            ExtTypeHash = Hash128.NewBlankHash(),
            TypeBlobIsDefinition = true,
            IsRefType = false,
            TypeDependencies = Array.Empty<int>(),
            TypeBlob = new TypeTreeBlob
            {
                Nodes = nodes,
                StringBufferBytes = stringBuffer
            }
        };

    private static uint AddString(MemoryStream ms, string value)
    {
        uint offset = (uint)ms.Position;
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        ms.Write(bytes, 0, bytes.Length);
        ms.WriteByte(0);
        return offset;
    }

    private static void WriteMultiFilePakV8(
        string pakPath,
        string mountPoint,
        IReadOnlyList<(string RelativePath, byte[] Payload)> files)
    {
        using FileStream fs = new(pakPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using BinaryWriter bw = new(fs, Encoding.UTF8, leaveOpen: true);

        List<(string RelativePath, long StartOffset, long Size, byte[] Sha1)> indexEntries = new(files.Count);
        foreach ((string relativePath, byte[] payload) in files)
        {
            long startOffset = fs.Position;
            byte[] sha1 = SHA1.HashData(payload);
            WritePakEntryHeaderV8(bw, 0L, payload.LongLength, sha1);
            bw.Write(payload);
            indexEntries.Add((relativePath, startOffset, payload.LongLength, sha1));
        }

        const int footerSize8 = 189;
        if (fs.Position + footerSize8 < 253)
        {
            long padBytes = 253 - footerSize8 - fs.Position;
            if (padBytes > 0)
            {
                bw.Write(new byte[padBytes]);
            }
        }

        long indexOffset = fs.Position;
        byte[] indexBytes;
        using (MemoryStream idxMs = new())
        using (BinaryWriter idxBw = new(idxMs, Encoding.UTF8, leaveOpen: true))
        {
            WriteFString(idxBw, mountPoint);
            idxBw.Write(indexEntries.Count);

            foreach ((string relativePath, long startOffset, long size, byte[] sha1) in indexEntries)
            {
                WriteFString(idxBw, relativePath);
                WritePakEntryHeaderV8(idxBw, startOffset, size, sha1);
            }

            idxBw.Flush();
            indexBytes = idxMs.ToArray();
        }

        bw.Write(indexBytes);
        byte[] indexHash = SHA1.HashData(indexBytes);

        bw.Write(new byte[16]);
        bw.Write((byte)0);
        bw.Write(0x5A6F12E1u);
        bw.Write(8);
        bw.Write(indexOffset);
        bw.Write((long)indexBytes.Length);
        bw.Write(indexHash);
        bw.Write(new byte[128]);
        bw.Flush();
    }

    private static void WritePakEntryHeaderV8(BinaryWriter bw, long offset, long size, byte[] sha1)
    {
        bw.Write(offset);
        bw.Write(size);
        bw.Write(size);
        bw.Write((byte)0);
        bw.Write(sha1);
        bw.Write((byte)0);
        bw.Write(0u);
    }

    private static void WriteFString(BinaryWriter bw, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            bw.Write(0);
            return;
        }

        byte[] ascii = Encoding.ASCII.GetBytes(value);
        bw.Write(ascii.Length + 1);
        bw.Write(ascii);
        bw.Write((byte)0);
    }
}
