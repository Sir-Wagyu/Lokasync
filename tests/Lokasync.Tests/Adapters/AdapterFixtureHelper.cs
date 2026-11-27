using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using CUE4Parse.UE4.Objects.Core.i18N;

namespace Lokasync.Tests.Adapters;

internal sealed class AdapterFixtureHelper : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);
    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    public string RootPath { get; }

    public AdapterFixtureHelper()
    {
        RootPath = Path.Combine(
            Path.GetTempPath(),
            "Lokasync_AdapterTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
    }

    public string CreateSubDir(string relativeDir)
    {
        string full = Path.Combine(RootPath, relativeDir.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(full);
        return full;
    }

    public string WriteTextFile(string relativePath, string content, bool withUtf8Bom = false)
    {
        string fullPath = Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(fullPath, content, withUtf8Bom ? Utf8WithBom : Utf8NoBom);
        return fullPath;
    }

    public byte[] ReadRawBytes(string relativePath)
    {
        string fullPath = Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.ReadAllBytes(fullPath);
    }

    public string ReadTextFile(string relativePath)
    {
        string fullPath = Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.ReadAllText(fullPath, Encoding.UTF8);
    }

    public void CreateLooseFixture(bool includeBomOnSrt = true)
    {
        string srtContent =
            "1\r\n" +
            "00:00:01,000 --> 00:00:04,250\r\n" +
            "<b>Welcome</b>, {player_name}!\r\n" +
            "\r\n" +
            "2\r\n" +
            "00:00:05,000 --> 00:00:08,500\r\n" +
            "<color=#FFD700>The Warden</color> guards %s.\r\n" +
            "Stay quiet.\r\n" +
            "\r\n";

        var jsonPayload = new Dictionary<string, object?>
        {
            ["meta_version"] = 42,
            ["is_demo"] = false,
            ["nullable_flag"] = null,
            ["ui_start"] = "Start Expedition",
            ["menu/start"] = "Continue Journey",
            ["tilde~key"] = "Tilde Value",
            ["dialog"] = new Dictionary<string, object?>
            {
                ["line_1"] = "Check\tinventory.\nMove to {0}.",
                ["lines"] = new object?[]
                {
                    new { text = "Array first line" },
                    123,
                    new { text = "Array third line" }
                }
            }
        };
        string jsonContent = JsonSerializer.Serialize(jsonPayload, JsonWriteOptions);

        string xmlContent =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<!-- Cutscene subtitles -->\n" +
            "<dialogues version=\"1\">\n" +
            "  <line id=\"L001\" speaker=\"Aria\">Welcome to &lt;b&gt;Arcadia&lt;/b&gt;, {partner}!</line>\n" +
            "  <line id=\"L002\" speaker=\"Noah\"><![CDATA[Hold the <line> & wait!]]></line>\n" +
            "</dialogues>\n";

        WriteTextFile("subtitles/intro.srt", srtContent, withUtf8Bom: includeBomOnSrt);
        WriteTextFile("localization/dialog_en.json", jsonContent);
        WriteTextFile("data/cutscene.xml", xmlContent);
    }

    public void CreateRpgMakerFixture(bool isMv = false)
    {
        string dataRel = isMv ? "www/data" : "data";

        if (isMv)
        {
            WriteTextFile("www/package.json", "{\"name\":\"mock-rpgmv\",\"main\":\"www/index.html\"}");
        }
        else
        {
            WriteTextFile("game.rmmzproject", "RPGMZ 1.7.0");
        }

        var map001 = new
        {
            autoplayBgm = false,
            displayName = "Aether Harbor",
            events = new object?[]
            {
                null,
                new
                {
                    id = 1,
                    name = "EV_Captain",
                    pages = new object[]
                    {
                        new
                        {
                            list = new object[]
                            {
                                new { code = 101, indent = 0, parameters = new object[] { "Actor1", 0, 0, 2 } },
                                new { code = 401, indent = 1, parameters = new object[] { "Hello, \\N[1]!\nWelcome to Aether Harbor." } },
                                new { code = 401, indent = 1, parameters = new object[] { "Bring {crystal_count} shards." } },
                                new { code = 105, indent = 0, parameters = new object[] { 2, false } },
                                new { code = 405, indent = 0, parameters = new object[] { "Long ago, the Warden sealed the sky." } },
                                new { code = 102, indent = 0, parameters = new object[] { new[] { "Accept mission", "Wait here" }, 1, 0, 2, 0 } },
                                new { code = 402, indent = 0, parameters = new object[] { 0, "Accept mission" } },
                                new { code = 404, indent = 0, parameters = Array.Empty<object>() },
                                new { code = 0, indent = 0, parameters = Array.Empty<object>() }
                            }
                        }
                    }
                },
                null
            }
        };

        var commonEvents = new object?[]
        {
            null,
            new
            {
                id = 1,
                name = "CE_SystemHint",
                list = new object[]
                {
                    new { code = 101, indent = 0, parameters = new object[] { "Guide", 1, 0, 2 } },
                    new { code = 401, indent = 0, parameters = new object[] { "Save often at crystals, \\N[1]." } },
                    new { code = 405, indent = 0, parameters = new object[] { "Chapter One: Signal Lost" } },
                    new { code = 102, indent = 0, parameters = new object[] { new[] { "Continue", "Title Screen" }, 1, 0, 2, 0 } },
                    new { code = 0, indent = 0, parameters = Array.Empty<object>() }
                }
            }
        };

        var mapInfos = new object?[]
        {
            null,
            new { id = 1, name = "Aether Harbor", order = 1, parentId = 0 }
        };

        var systemJson = new
        {
            gameTitle = isMv ? "MockMV" : "MockMZ",
            versionId = 12345
        };

        WriteTextFile($"{dataRel}/Map001.json", JsonSerializer.Serialize(map001, JsonWriteOptions));
        WriteTextFile($"{dataRel}/CommonEvents.json", JsonSerializer.Serialize(commonEvents, JsonWriteOptions));
        WriteTextFile($"{dataRel}/MapInfos.json", JsonSerializer.Serialize(mapInfos, JsonWriteOptions));
        WriteTextFile($"{dataRel}/System.json", JsonSerializer.Serialize(systemJson, JsonWriteOptions));
    }

    public (string ResourcesPath, string SharedAssets0Path) CreateUnityFixture(
        string gameName = "MockUnityGame",
        bool typeTreeEnabled = true)
    {
        CreateSubDir($"{gameName}_Data");
        File.WriteAllBytes(Path.Combine(RootPath, "UnityPlayer.dll"), "MOCK_UNITY_PLAYER"u8.ToArray());

        string resourcesPath = Path.Combine(RootPath, $"{gameName}_Data", "resources.assets");
        string sharedPath = Path.Combine(RootPath, $"{gameName}_Data", "sharedassets0.assets");

        WriteUnityResourcesAssets(resourcesPath, typeTreeEnabled);
        WriteUnitySharedAssets(sharedPath, typeTreeEnabled);

        return (resourcesPath, sharedPath);
    }

    public string CreateUnityBundleFixture(
        string gameName = "MockBundleGame",
        bool compressLz4 = true,
        byte[]? extraSiblingBlob = null)
    {
        string dataRel = $"{gameName}_Data";
        string dataDir = CreateSubDir(dataRel);
        File.WriteAllBytes(Path.Combine(RootPath, "UnityPlayer.dll"), "MOCK_UNITY_PLAYER"u8.ToArray());

        // Add realistic UnityEngine C# XML docs inside Managed/ to ensure Unity + LooseFile skip them cleanly.
        WriteTextFile(
            $"{dataRel}/Managed/UnityEngine.CoreModule.xml",
            "<?xml version=\"1.0\"?><doc><assembly><name>UnityEngine.CoreModule</name></assembly><members><member name=\"T:UnityEngine.Object\"><summary>Unity base object doc.</summary></member></members></doc>");

        byte[] resourcesBytes = BuildBundleResourcesAssetsBytes();
        string bundlePath = Path.Combine(dataDir, "data.unity3d");
        List<(string EntryName, byte[] Payload)> entries = [("resources.assets", resourcesBytes)];
        if (extraSiblingBlob is not null)
        {
            entries.Add(("Resources/sibling_blob.dat", extraSiblingBlob));
        }

        WriteUnityFsBundle(bundlePath, entries, compressLz4);

        return bundlePath;
    }

    public (string BasePakPath, string ExpectedModsPakPath, string LocResVfsPath) CreateUnrealPakFixture(
        bool nestedSubdir = false,
        uint sourceStringHash = 0x12345678u,
        ELocResVersion version = ELocResVersion.Optimized_CRC32,
        IReadOnlyList<(string Namespace, string Key, uint SourceStringHash, string Text)>? customEntries = null)
    {
        string contentRoot = nestedSubdir ? CreateSubDir("MockUnrealGame") : RootPath;
        string paksDir = Path.Combine(contentRoot, "Content", "Paks");
        Directory.CreateDirectory(paksDir);

        IReadOnlyList<(string Namespace, string Key, uint SourceStringHash, string Text)> entries = customEntries ??
        [
            ("Dialog", "Line_001", sourceStringHash, "<RichText.Bold>Welcome</>, {PlayerName}!"),
            ("Dialog", "Line_002", sourceStringHash + 1, "Press [KEY]\tnow.\nProceed?"),
            ("UI", "Title", sourceStringHash + 2, "Aether Core")
        ];

        byte[] locResBytes = BuildLocresBytes(version, entries);

        string basePakPath = Path.Combine(paksDir, "pakchunk0-WindowsNoEditor.pak");
        string expectedModsPakPath = Path.Combine(paksDir, "~mods", "Lokasync_ID_P.pak");
        string vfsPath = "Game/Content/Localization/Game/en/Game.locres";

        WriteTestPakV8(
            basePakPath,
            mountPoint: "../../../Game/Content/",
            files: [("Localization/Game/en/Game.locres", locResBytes)]);

        return (basePakPath, expectedModsPakPath, vfsPath);
    }

    public string CreateUnrealLooseLocresFixture(uint sourceStringHash = 0x87654321u)
    {
        string locDir = CreateSubDir("Content/Localization/Game/en");
        string locResPath = Path.Combine(locDir, "Game.locres");

        byte[] locResBytes = BuildLocresBytes(
            ELocResVersion.Optimized_CRC32,
            [
                ("LooseNS", "Greeting", sourceStringHash, "Welcome from loose locres!")
            ]);

        File.WriteAllBytes(locResPath, locResBytes);
        return locResPath;
    }

    public static byte[] BuildLocresBytes(
        ELocResVersion version,
        IReadOnlyList<(string Namespace, string Key, uint SourceStringHash, string Text)> entries)
    {
        using MemoryStream ms = new();
        using BinaryWriter bw = new(ms, Encoding.UTF8, leaveOpen: true);

        List<IGrouping<string, (string Namespace, string Key, uint SourceStringHash, string Text)>> groups = entries
            .GroupBy(e => e.Namespace, StringComparer.Ordinal)
            .ToList();

        if (version == ELocResVersion.Legacy)
        {
            bw.Write((uint)groups.Count);
            foreach (IGrouping<string, (string Namespace, string Key, uint SourceStringHash, string Text)> nsGroup in groups)
            {
                WriteFString(bw, nsGroup.Key);

                List<(string Namespace, string Key, uint SourceStringHash, string Text)> nsEntries = nsGroup.ToList();
                bw.Write((uint)nsEntries.Count);

                foreach ((string _, string key, uint sourceStringHash, string text) in nsEntries)
                {
                    WriteFString(bw, key);
                    bw.Write(sourceStringHash);
                    WriteFString(bw, text);
                }
            }

            bw.Flush();
            return ms.ToArray();
        }

        bw.Write(0x7574140Eu);
        bw.Write(0xFC034A67u);
        bw.Write(0x9D90154Au);
        bw.Write(0x1B7F37C3u);
        bw.Write((byte)version);

        long offsetPlaceholderPos = ms.Position;
        bw.Write(0L);

        if (version >= ELocResVersion.Optimized_CRC32)
        {
            bw.Write((uint)entries.Count);
        }

        bw.Write((uint)groups.Count);

        List<string> stringLut = new(entries.Count);
        foreach (IGrouping<string, (string Namespace, string Key, uint SourceStringHash, string Text)> nsGroup in groups)
        {
            if (version >= ELocResVersion.Optimized_CRC32)
            {
                bw.Write(ComputeCrc32(nsGroup.Key));
            }

            WriteFString(bw, nsGroup.Key);

            List<(string Namespace, string Key, uint SourceStringHash, string Text)> nsEntries = nsGroup.ToList();
            bw.Write((uint)nsEntries.Count);

            foreach ((string _, string key, uint sourceStringHash, string text) in nsEntries)
            {
                if (version >= ELocResVersion.Optimized_CRC32)
                {
                    bw.Write(ComputeCrc32(key));
                }

                WriteFString(bw, key);
                bw.Write(sourceStringHash);
                int lutIndex = stringLut.Count;
                stringLut.Add(text);
                bw.Write(lutIndex);
            }
        }

        long lutOffset = ms.Position;
        bw.Write(stringLut.Count);
        foreach (string text in stringLut)
        {
            WriteFString(bw, text);
            if (version >= ELocResVersion.Optimized_CRC32)
            {
                bw.Write(1);
            }
        }

        ms.Position = offsetPlaceholderPos;
        bw.Write(lutOffset);
        bw.Flush();
        return ms.ToArray();
    }

    private static void WriteFString(BinaryWriter bw, string value)
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

    private static void WriteTestPakV8(
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

    private static void WriteUnityResourcesAssets(string outputPath, bool typeTreeEnabled)
    {
        AssetsFile afile = CreateBlankAssetsFile(typeTreeEnabled);

        TypeTreeType textAssetStringType = BuildTextAssetStringTypeTree();
        TypeTreeType locAssetType = BuildLocalizationAssetTypeTree();
        TypeTreeType stringTableMonoType = BuildMonoBehaviourStringTableTypeTree();

        afile.Metadata.TypeTreeTypes.Add(textAssetStringType);
        afile.Metadata.TypeTreeTypes.Add(locAssetType);
        afile.Metadata.TypeTreeTypes.Add(stringTableMonoType);

        AssetTypeTemplateField textTemplate = new();
        textTemplate.FromTypeTree(textAssetStringType);
        AssetTypeValueField textField = ValueBuilder.DefaultValueFieldFromTemplate(textTemplate);
        textField["m_Name"].AsString = "dialogue_01";
        textField["m_Script"].AsString = "Hello, <color=#FFF>hero</color>!";
        AssetFileInfo textInfo = AssetFileInfo.Create(afile, 1, (int)AssetClassID.TextAsset, null, true);
        textInfo.SetNewData(textField);
        afile.Metadata.AddAssetInfo(textInfo);

        AssetTypeTemplateField locTemplate = new();
        locTemplate.FromTypeTree(locAssetType);
        AssetTypeValueField locField = ValueBuilder.DefaultValueFieldFromTemplate(locTemplate);
        locField["m_Name"].AsString = "UI_Strings";
        locField["m_LocaleIsoCode"].AsString = "en";
        AssetTypeTemplateField pairTemplate = locField["m_LocalizedStrings"]["Array"].TemplateField.Children[1];
        AssetTypeValueField pair1 = ValueBuilder.DefaultValueFieldFromTemplate(pairTemplate);
        pair1["first"].AsString = "BTN_START";
        pair1["second"].AsString = "Start Game";
        AssetTypeValueField pair2 = ValueBuilder.DefaultValueFieldFromTemplate(pairTemplate);
        pair2["first"].AsString = "BTN_QUIT";
        pair2["second"].AsString = "Quit Game";
        locField["m_LocalizedStrings"]["Array"].Children.Add(pair1);
        locField["m_LocalizedStrings"]["Array"].Children.Add(pair2);
        AssetFileInfo locInfo = AssetFileInfo.Create(afile, 2, (int)AssetClassID.LocalizationAsset, null, true);
        locInfo.SetNewData(locField);
        afile.Metadata.AddAssetInfo(locInfo);

        AssetTypeTemplateField monoTemplate = new();
        monoTemplate.FromTypeTree(stringTableMonoType);
        AssetTypeValueField monoField = ValueBuilder.DefaultValueFieldFromTemplate(monoTemplate);
        monoField["m_Name"].AsString = "StringTable_EN";
        AssetTypeTemplateField rowTemplate = monoField["m_TableData"]["Array"].TemplateField.Children[1];
        AssetTypeValueField row1 = ValueBuilder.DefaultValueFieldFromTemplate(rowTemplate);
        row1["m_Id"].AsLong = 1001;
        row1["m_Localized"].AsString = "Welcome to Aether!";
        monoField["m_TableData"]["Array"].Children.Add(row1);
        AssetFileInfo monoInfo = AssetFileInfo.Create(afile, 3, (int)AssetClassID.MonoBehaviour, 0, null, true);
        monoInfo.SetNewData(monoField);
        afile.Metadata.AddAssetInfo(monoInfo);

        using AssetsFileWriter writer = new(outputPath);
        afile.Write(writer);
    }

    private static void WriteUnitySharedAssets(string outputPath, bool typeTreeEnabled)
    {
        AssetsFile afile = CreateBlankAssetsFile(typeTreeEnabled);

        TypeTreeType textBytesType = BuildTextAssetByteArrayTypeTree();
        afile.Metadata.TypeTreeTypes.Add(textBytesType);

        AssetTypeTemplateField template = new();
        template.FromTypeTree(textBytesType);

        AssetTypeValueField validTextField = ValueBuilder.DefaultValueFieldFromTemplate(template);
        validTextField["m_Name"].AsString = "subtitles_utf8_bytes";
        validTextField["m_Script"].AsByteArray = Encoding.UTF8.GetBytes("Byte-backed subtitle {0}");
        AssetFileInfo validInfo = AssetFileInfo.Create(afile, 10, (int)AssetClassID.TextAsset, null, true);
        validInfo.SetNewData(validTextField);
        afile.Metadata.AddAssetInfo(validInfo);

        AssetTypeValueField binaryField = ValueBuilder.DefaultValueFieldFromTemplate(template);
        binaryField["m_Name"].AsString = "binary_blob";
        binaryField["m_Script"].AsByteArray = [0x00, 0xFF, 0xFE, 0x01, 0x02];
        AssetFileInfo binaryInfo = AssetFileInfo.Create(afile, 11, (int)AssetClassID.TextAsset, null, true);
        binaryInfo.SetNewData(binaryField);
        afile.Metadata.AddAssetInfo(binaryInfo);

        using AssetsFileWriter writer = new(outputPath);
        afile.Write(writer);
    }

    private static AssetsFile CreateBlankAssetsFile(bool typeTreeEnabled) =>
        new()
        {
            Header = new AssetsFileHeader { Version = 22, Endianness = false },
            Metadata = new AssetsFileMetadata
            {
                UnityVersion = "2022.3.10f1",
                TargetPlatform = 19,
                TypeTreeEnabled = typeTreeEnabled,
                TypeTreeTypes = new List<TypeTreeType>(),
                AssetInfos = new List<AssetFileInfo>(),
                ScriptTypes = new List<AssetPPtr>(),
                Externals = new List<AssetsFileExternal>(),
                RefTypes = new List<TypeTreeType>(),
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

    private static TypeTreeType BuildLocalizationAssetTypeTree()
    {
        using MemoryStream sb = new();
        uint sLocAsset = AddString(sb, "LocalizationAsset");
        uint sBase = AddString(sb, "Base");
        uint sString = AddString(sb, "string");
        uint sMName = AddString(sb, "m_Name");
        uint sMLocale = AddString(sb, "m_LocaleIsoCode");
        uint sMap = AddString(sb, "map");
        uint sMLocStrings = AddString(sb, "m_LocalizedStrings");
        uint sArray = AddString(sb, "Array");
        uint sInt = AddString(sb, "int");
        uint sSize = AddString(sb, "size");
        uint sChar = AddString(sb, "char");
        uint sData = AddString(sb, "data");
        uint sPair = AddString(sb, "pair");
        uint sFirst = AddString(sb, "first");
        uint sSecond = AddString(sb, "second");

        List<TypeTreeNode> nodes =
        [
            new() { Version = 1, Level = 0, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sLocAsset, NameStrOffset = sBase, ByteSize = -1, Index = 0, MetaFlags = 0 },
            new() { Version = 1, Level = 1, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sString, NameStrOffset = sMName, ByteSize = -1, Index = 1, MetaFlags = 0x4000 },
            new() { Version = 1, Level = 2, TypeFlags = TypeTreeNodeFlags.Array, TypeStrOffset = sArray, NameStrOffset = sArray, ByteSize = -1, Index = 2, MetaFlags = 0x4000 },
            new() { Version = 1, Level = 3, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sInt, NameStrOffset = sSize, ByteSize = 4, Index = 3, MetaFlags = 0 },
            new() { Version = 1, Level = 3, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sChar, NameStrOffset = sData, ByteSize = 1, Index = 4, MetaFlags = 0 },
            new() { Version = 1, Level = 1, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sString, NameStrOffset = sMLocale, ByteSize = -1, Index = 5, MetaFlags = 0x4000 },
            new() { Version = 1, Level = 2, TypeFlags = TypeTreeNodeFlags.Array, TypeStrOffset = sArray, NameStrOffset = sArray, ByteSize = -1, Index = 6, MetaFlags = 0x4000 },
            new() { Version = 1, Level = 3, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sInt, NameStrOffset = sSize, ByteSize = 4, Index = 7, MetaFlags = 0 },
            new() { Version = 1, Level = 3, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sChar, NameStrOffset = sData, ByteSize = 1, Index = 8, MetaFlags = 0 },
            new() { Version = 1, Level = 1, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sMap, NameStrOffset = sMLocStrings, ByteSize = -1, Index = 9, MetaFlags = 0 },
            new() { Version = 1, Level = 2, TypeFlags = TypeTreeNodeFlags.Array, TypeStrOffset = sArray, NameStrOffset = sArray, ByteSize = -1, Index = 10, MetaFlags = 0 },
            new() { Version = 1, Level = 3, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sInt, NameStrOffset = sSize, ByteSize = 4, Index = 11, MetaFlags = 0 },
            new() { Version = 1, Level = 3, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sPair, NameStrOffset = sData, ByteSize = -1, Index = 12, MetaFlags = 0 },
            new() { Version = 1, Level = 4, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sString, NameStrOffset = sFirst, ByteSize = -1, Index = 13, MetaFlags = 0x4000 },
            new() { Version = 1, Level = 5, TypeFlags = TypeTreeNodeFlags.Array, TypeStrOffset = sArray, NameStrOffset = sArray, ByteSize = -1, Index = 14, MetaFlags = 0x4000 },
            new() { Version = 1, Level = 6, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sInt, NameStrOffset = sSize, ByteSize = 4, Index = 15, MetaFlags = 0 },
            new() { Version = 1, Level = 6, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sChar, NameStrOffset = sData, ByteSize = 1, Index = 16, MetaFlags = 0 },
            new() { Version = 1, Level = 4, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sString, NameStrOffset = sSecond, ByteSize = -1, Index = 17, MetaFlags = 0x4000 },
            new() { Version = 1, Level = 5, TypeFlags = TypeTreeNodeFlags.Array, TypeStrOffset = sArray, NameStrOffset = sArray, ByteSize = -1, Index = 18, MetaFlags = 0x4000 },
            new() { Version = 1, Level = 6, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sInt, NameStrOffset = sSize, ByteSize = 4, Index = 19, MetaFlags = 0 },
            new() { Version = 1, Level = 6, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sChar, NameStrOffset = sData, ByteSize = 1, Index = 20, MetaFlags = 0 }
        ];

        return BuildTypeTreeType((int)AssetClassID.LocalizationAsset, 0xffff, nodes, sb.ToArray());
    }

    private static TypeTreeType BuildMonoBehaviourStringTableTypeTree()
    {
        using MemoryStream sb = new();
        uint sMono = AddString(sb, "MonoBehaviour");
        uint sBase = AddString(sb, "Base");
        uint sString = AddString(sb, "string");
        uint sMName = AddString(sb, "m_Name");
        uint sVector = AddString(sb, "vector");
        uint sTableData = AddString(sb, "m_TableData");
        uint sArray = AddString(sb, "Array");
        uint sInt = AddString(sb, "int");
        uint sSize = AddString(sb, "size");
        uint sChar = AddString(sb, "char");
        uint sData = AddString(sb, "data");
        uint sRow = AddString(sb, "TableEntryData");
        uint sSInt64 = AddString(sb, "SInt64");
        uint sMId = AddString(sb, "m_Id");
        uint sMLocalized = AddString(sb, "m_Localized");

        List<TypeTreeNode> nodes =
        [
            new() { Version = 1, Level = 0, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sMono, NameStrOffset = sBase, ByteSize = -1, Index = 0, MetaFlags = 0 },
            new() { Version = 1, Level = 1, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sString, NameStrOffset = sMName, ByteSize = -1, Index = 1, MetaFlags = 0x4000 },
            new() { Version = 1, Level = 2, TypeFlags = TypeTreeNodeFlags.Array, TypeStrOffset = sArray, NameStrOffset = sArray, ByteSize = -1, Index = 2, MetaFlags = 0x4000 },
            new() { Version = 1, Level = 3, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sInt, NameStrOffset = sSize, ByteSize = 4, Index = 3, MetaFlags = 0 },
            new() { Version = 1, Level = 3, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sChar, NameStrOffset = sData, ByteSize = 1, Index = 4, MetaFlags = 0 },
            new() { Version = 1, Level = 1, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sVector, NameStrOffset = sTableData, ByteSize = -1, Index = 5, MetaFlags = 0 },
            new() { Version = 1, Level = 2, TypeFlags = TypeTreeNodeFlags.Array, TypeStrOffset = sArray, NameStrOffset = sArray, ByteSize = -1, Index = 6, MetaFlags = 0 },
            new() { Version = 1, Level = 3, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sInt, NameStrOffset = sSize, ByteSize = 4, Index = 7, MetaFlags = 0 },
            new() { Version = 1, Level = 3, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sRow, NameStrOffset = sData, ByteSize = -1, Index = 8, MetaFlags = 0 },
            new() { Version = 1, Level = 4, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sSInt64, NameStrOffset = sMId, ByteSize = 8, Index = 9, MetaFlags = 0 },
            new() { Version = 1, Level = 4, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sString, NameStrOffset = sMLocalized, ByteSize = -1, Index = 10, MetaFlags = 0x4000 },
            new() { Version = 1, Level = 5, TypeFlags = TypeTreeNodeFlags.Array, TypeStrOffset = sArray, NameStrOffset = sArray, ByteSize = -1, Index = 11, MetaFlags = 0x4000 },
            new() { Version = 1, Level = 6, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sInt, NameStrOffset = sSize, ByteSize = 4, Index = 12, MetaFlags = 0 },
            new() { Version = 1, Level = 6, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sChar, NameStrOffset = sData, ByteSize = 1, Index = 13, MetaFlags = 0 }
        ];

        return BuildTypeTreeType((int)AssetClassID.MonoBehaviour, 0, nodes, sb.ToArray());
    }

    private static byte[] BuildBundleResourcesAssetsBytes()
    {
        AssetsFile afile = CreateBlankAssetsFile(typeTreeEnabled: true);

        TypeTreeType textAssetStringType = BuildTextAssetStringTypeTree();
        TypeTreeType locAssetType = BuildLocalizationAssetTypeTree();
        TypeTreeType i2MonoType = BuildI2LocMonoBehaviourTypeTree();

        afile.Metadata.TypeTreeTypes.Add(textAssetStringType);
        afile.Metadata.TypeTreeTypes.Add(locAssetType);
        afile.Metadata.TypeTreeTypes.Add(i2MonoType);

        AssetTypeTemplateField textTemplate = new();
        textTemplate.FromTypeTree(textAssetStringType);

        void AddTextAsset(long pathId, string name, string script)
        {
            AssetTypeValueField tf = ValueBuilder.DefaultValueFieldFromTemplate(textTemplate);
            tf["m_Name"].AsString = name;
            tf["m_Script"].AsString = script;
            AssetFileInfo info = AssetFileInfo.Create(afile, pathId, (int)AssetClassID.TextAsset, null, true);
            info.SetNewData(tf);
            afile.Metadata.AddAssetInfo(info);
        }

        AddTextAsset(1, "dialogue_01", "Hello, <color=#FFF>hero</color>!");
        AddTextAsset(2, "LineBreaking Following Characters", ")]}!?");
        AddTextAsset(3, "BossBalanceChartNewGame", "Id,Name,Life,Strength\nBS01,Ten Piedad,1200,25\nBS02,Our Lady,1800,35");

        AssetTypeTemplateField locTemplate = new();
        locTemplate.FromTypeTree(locAssetType);
        AssetTypeValueField locField = ValueBuilder.DefaultValueFieldFromTemplate(locTemplate);
        locField["m_Name"].AsString = "UI_Strings";
        locField["m_LocaleIsoCode"].AsString = "en";
        AssetTypeTemplateField pairTemplate = locField["m_LocalizedStrings"]["Array"].TemplateField.Children[1];
        AssetTypeValueField pair1 = ValueBuilder.DefaultValueFieldFromTemplate(pairTemplate);
        pair1["first"].AsString = "BTN_START";
        pair1["second"].AsString = "Start Game";
        locField["m_LocalizedStrings"]["Array"].Children.Add(pair1);
        AssetFileInfo locInfo = AssetFileInfo.Create(afile, 4, (int)AssetClassID.LocalizationAsset, null, true);
        locInfo.SetNewData(locField);
        afile.Metadata.AddAssetInfo(locInfo);

        AssetTypeTemplateField i2Template = new();
        i2Template.FromTypeTree(i2MonoType);
        AssetTypeValueField i2Field = ValueBuilder.DefaultValueFieldFromTemplate(i2Template);
        i2Field["m_Name"].AsString = "I2Languages";
        AssetTypeValueField mSource = i2Field["mSource"];

        AssetTypeTemplateField langTemplate = mSource["mLanguages"]["Array"].TemplateField.Children[1];
        AssetTypeValueField esLang = ValueBuilder.DefaultValueFieldFromTemplate(langTemplate);
        esLang["Name"].AsString = "Spanish";
        esLang["Code"].AsString = "es";
        AssetTypeValueField enLang = ValueBuilder.DefaultValueFieldFromTemplate(langTemplate);
        enLang["Name"].AsString = "English";
        enLang["Code"].AsString = "en";
        mSource["mLanguages"]["Array"].Children.Add(esLang);
        mSource["mLanguages"]["Array"].Children.Add(enLang);

        AssetTypeTemplateField termTemplate = mSource["mTerms"]["Array"].TemplateField.Children[1];
        AssetTypeValueField term0 = ValueBuilder.DefaultValueFieldFromTemplate(termTemplate);
        term0["Term"].AsString = "DIALOG_01";
        term0["TermType"].AsInt = 0;
        AssetTypeTemplateField langValTemplate = term0["Languages"]["Array"].TemplateField.Children[1];
        AssetTypeValueField t0Es = ValueBuilder.DefaultValueFieldFromTemplate(langValTemplate);
        t0Es.AsString = "Hola Caballero";
        AssetTypeValueField t0En = ValueBuilder.DefaultValueFieldFromTemplate(langValTemplate);
        t0En.AsString = "Hello Knight";
        term0["Languages"]["Array"].Children.Add(t0Es);
        term0["Languages"]["Array"].Children.Add(t0En);

        AssetTypeValueField term1 = ValueBuilder.DefaultValueFieldFromTemplate(termTemplate);
        term1["Term"].AsString = "FONT_MAIN";
        term1["TermType"].AsInt = 1;
        AssetTypeValueField t1Es = ValueBuilder.DefaultValueFieldFromTemplate(langValTemplate);
        t1Es.AsString = "FontES";
        AssetTypeValueField t1En = ValueBuilder.DefaultValueFieldFromTemplate(langValTemplate);
        t1En.AsString = "FontEN";
        term1["Languages"]["Array"].Children.Add(t1Es);
        term1["Languages"]["Array"].Children.Add(t1En);

        mSource["mTerms"]["Array"].Children.Add(term0);
        mSource["mTerms"]["Array"].Children.Add(term1);

        AssetFileInfo monoInfo = AssetFileInfo.Create(afile, 5, (int)AssetClassID.MonoBehaviour, 0, null, true);
        monoInfo.SetNewData(i2Field);
        afile.Metadata.AddAssetInfo(monoInfo);

        using MemoryStream ms = new();
        using AssetsFileWriter writer = new(ms);
        afile.Write(writer);
        writer.Flush();
        return ms.ToArray();
    }

    private static TypeTreeType BuildI2LocMonoBehaviourTypeTree()
    {
        using MemoryStream sb = new();
        uint sMono = AddString(sb, "MonoBehaviour");
        uint sBase = AddString(sb, "Base");
        uint sString = AddString(sb, "string");
        uint sMName = AddString(sb, "m_Name");
        uint sSourceData = AddString(sb, "LanguageSourceData");
        uint sMSource = AddString(sb, "mSource");
        uint sVector = AddString(sb, "vector");
        uint sMLangs = AddString(sb, "mLanguages");
        uint sMTerms = AddString(sb, "mTerms");
        uint sArray = AddString(sb, "Array");
        uint sInt = AddString(sb, "int");
        uint sSize = AddString(sb, "size");
        uint sChar = AddString(sb, "char");
        uint sData = AddString(sb, "data");
        uint sLangData = AddString(sb, "LanguageData");
        uint sName = AddString(sb, "Name");
        uint sCode = AddString(sb, "Code");
        uint sTermData = AddString(sb, "TermData");
        uint sTerm = AddString(sb, "Term");
        uint sTermType = AddString(sb, "TermType");
        uint sLanguages = AddString(sb, "Languages");

        List<TypeTreeNode> nodes = [];
        uint idx = 0;
        void AddStringNodes(byte level, uint fieldName)
        {
            nodes.Add(new TypeTreeNode { Version = 1, Level = level, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sString, NameStrOffset = fieldName, ByteSize = -1, Index = idx++, MetaFlags = 0x4000 });
            nodes.Add(new TypeTreeNode { Version = 1, Level = (byte)(level + 1), TypeFlags = TypeTreeNodeFlags.Array, TypeStrOffset = sArray, NameStrOffset = sArray, ByteSize = -1, Index = idx++, MetaFlags = 0x4000 });
            nodes.Add(new TypeTreeNode { Version = 1, Level = (byte)(level + 2), TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sInt, NameStrOffset = sSize, ByteSize = 4, Index = idx++, MetaFlags = 0 });
            nodes.Add(new TypeTreeNode { Version = 1, Level = (byte)(level + 2), TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sChar, NameStrOffset = sData, ByteSize = 1, Index = idx++, MetaFlags = 0 });
        }

        nodes.Add(new TypeTreeNode { Version = 1, Level = 0, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sMono, NameStrOffset = sBase, ByteSize = -1, Index = idx++, MetaFlags = 0 });
        AddStringNodes(1, sMName);
        nodes.Add(new TypeTreeNode { Version = 1, Level = 1, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sSourceData, NameStrOffset = sMSource, ByteSize = -1, Index = idx++, MetaFlags = 0 });

        // mLanguages
        nodes.Add(new TypeTreeNode { Version = 1, Level = 2, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sVector, NameStrOffset = sMLangs, ByteSize = -1, Index = idx++, MetaFlags = 0 });
        nodes.Add(new TypeTreeNode { Version = 1, Level = 3, TypeFlags = TypeTreeNodeFlags.Array, TypeStrOffset = sArray, NameStrOffset = sArray, ByteSize = -1, Index = idx++, MetaFlags = 0 });
        nodes.Add(new TypeTreeNode { Version = 1, Level = 4, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sInt, NameStrOffset = sSize, ByteSize = 4, Index = idx++, MetaFlags = 0 });
        nodes.Add(new TypeTreeNode { Version = 1, Level = 4, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sLangData, NameStrOffset = sData, ByteSize = -1, Index = idx++, MetaFlags = 0 });
        AddStringNodes(5, sName);
        AddStringNodes(5, sCode);

        // mTerms
        nodes.Add(new TypeTreeNode { Version = 1, Level = 2, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sVector, NameStrOffset = sMTerms, ByteSize = -1, Index = idx++, MetaFlags = 0 });
        nodes.Add(new TypeTreeNode { Version = 1, Level = 3, TypeFlags = TypeTreeNodeFlags.Array, TypeStrOffset = sArray, NameStrOffset = sArray, ByteSize = -1, Index = idx++, MetaFlags = 0 });
        nodes.Add(new TypeTreeNode { Version = 1, Level = 4, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sInt, NameStrOffset = sSize, ByteSize = 4, Index = idx++, MetaFlags = 0 });
        nodes.Add(new TypeTreeNode { Version = 1, Level = 4, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sTermData, NameStrOffset = sData, ByteSize = -1, Index = idx++, MetaFlags = 0 });
        AddStringNodes(5, sTerm);
        nodes.Add(new TypeTreeNode { Version = 1, Level = 5, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sInt, NameStrOffset = sTermType, ByteSize = 4, Index = idx++, MetaFlags = 0 });
        nodes.Add(new TypeTreeNode { Version = 1, Level = 5, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sVector, NameStrOffset = sLanguages, ByteSize = -1, Index = idx++, MetaFlags = 0 });
        nodes.Add(new TypeTreeNode { Version = 1, Level = 6, TypeFlags = TypeTreeNodeFlags.Array, TypeStrOffset = sArray, NameStrOffset = sArray, ByteSize = -1, Index = idx++, MetaFlags = 0 });
        nodes.Add(new TypeTreeNode { Version = 1, Level = 7, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sInt, NameStrOffset = sSize, ByteSize = 4, Index = idx++, MetaFlags = 0 });
        AddStringNodes(7, sData);

        return BuildTypeTreeType((int)AssetClassID.MonoBehaviour, 0, nodes, sb.ToArray());
    }

    private static void WriteUnityFsBundle(
        string outputPath,
        IReadOnlyList<(string EntryName, byte[] Payload)> entries,
        bool compressLz4)
    {
        byte[] dirBlockBytes;
        long payloadSize = entries.Sum(e => e.Payload.LongLength);

        using (MemoryStream dirMs = new())
        {
            dirMs.Write(new byte[16]); // Hash128
            WriteBigEndianInt32(dirMs, 1); // 1 block
            WriteBigEndianUInt32(dirMs, (uint)payloadSize);
            WriteBigEndianUInt32(dirMs, (uint)payloadSize);
            WriteBigEndianUInt16(dirMs, 0);

            WriteBigEndianInt32(dirMs, entries.Count);
            long offset = 0;
            foreach ((string entryName, byte[] payload) in entries)
            {
                WriteBigEndianInt64(dirMs, offset);
                WriteBigEndianInt64(dirMs, payload.LongLength);
                WriteBigEndianUInt32(dirMs, 4); // Serialized file flag
                byte[] nameBytes = Encoding.UTF8.GetBytes(entryName);
                dirMs.Write(nameBytes);
                dirMs.WriteByte(0);
                offset += payload.LongLength;
            }

            dirBlockBytes = dirMs.ToArray();
        }

        byte[] headerPrefix;
        using (MemoryStream hdrMs = new())
        {
            void WriteCString(string s)
            {
                hdrMs.Write(Encoding.ASCII.GetBytes(s));
                hdrMs.WriteByte(0);
            }

            WriteCString("UnityFS");
            WriteBigEndianUInt32(hdrMs, 6);
            WriteCString("5.x.x");
            WriteCString("2022.3.10f1");
            headerPrefix = hdrMs.ToArray();
        }

        long totalFileSize = headerPrefix.LongLength + 8 + 4 + 4 + 4 + dirBlockBytes.LongLength + payloadSize;
        string rawPath = compressLz4 ? outputPath + ".raw" : outputPath;

        using (FileStream fs = new(rawPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(headerPrefix);
            WriteBigEndianInt64(fs, totalFileSize);
            WriteBigEndianUInt32(fs, (uint)dirBlockBytes.Length);
            WriteBigEndianUInt32(fs, (uint)dirBlockBytes.Length);
            WriteBigEndianUInt32(fs, 0x40); // BlocksInfoAtTheEnd = false, no compression
            fs.Write(dirBlockBytes);
            foreach ((_, byte[] payload) in entries)
            {
                fs.Write(payload);
            }
        }

        if (compressLz4)
        {
            using (FileStream readFs = File.OpenRead(rawPath))
            using (AssetsFileReader reader = new(readFs))
            using (FileStream writeFs = new(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (AssetsFileWriter writer = new(writeFs))
            {
                AssetBundleFile bundle = new();
                bundle.Read(reader);
                bundle.Pack(writer, AssetBundleCompressionType.LZ4);
                bundle.Close();
            }

            File.Delete(rawPath);
        }
    }

    private static void WriteBigEndianInt32(Stream s, int value)
    {
        Span<byte> buf = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(buf, value);
        s.Write(buf);
    }

    private static void WriteBigEndianUInt32(Stream s, uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(buf, value);
        s.Write(buf);
    }

    private static void WriteBigEndianInt64(Stream s, long value)
    {
        Span<byte> buf = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(buf, value);
        s.Write(buf);
    }

    private static void WriteBigEndianUInt16(Stream s, ushort value)
    {
        Span<byte> buf = stackalloc byte[2];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(buf, value);
        s.Write(buf);
    }

    private static TypeTreeType BuildTypeTreeType(
        int typeId,
        ushort scriptTypeIndex,
        List<TypeTreeNode> nodes,
        byte[] stringBuffer) =>
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

    public void Dispose()
    {
        if (Directory.Exists(RootPath))
        {
            try
            {
                Directory.Delete(RootPath, recursive: true);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }
}
