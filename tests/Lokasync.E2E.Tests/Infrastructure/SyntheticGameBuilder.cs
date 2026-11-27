using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;

namespace Lokasync.E2E.Tests.Infrastructure;

public sealed record LooseFixtureManifest(
    string SrtRelativePath,
    string JsonRelativePath,
    string XmlRelativePath,
    IReadOnlyList<string> ExpectedDialogSnippets);

public sealed record RpgMakerFixtureManifest(
    bool IsMv,
    string DataSubdirRelativePath,
    string Map001RelativePath,
    string CommonEventsRelativePath,
    IReadOnlyList<string> ExpectedDialogLines,
    IReadOnlyList<string> UntouchedNonDialogValues);

public sealed record UnityFixtureManifest(
    string DataDirectoryPath,
    string ResourcesAssetsPath,
    string SharedAssets0Path,
    IReadOnlyList<(string Name, string Script)> ExpectedTextAssets);

public sealed record UnrealFixtureManifest(
    string PakDirectoryPath,
    string BasePakPath,
    string ExpectedModsPakPath,
    string LocresVirtualPath,
    IReadOnlyDictionary<string, string> ExpectedEntries);

public static class SyntheticGameBuilder
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    public static LooseFixtureManifest CreateLooseFileGame(string gameRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRootPath);
        Directory.CreateDirectory(gameRootPath);

        const string srtRel = "subtitles/intro.srt";
        const string jsonRel = "localization/dialog_en.json";
        const string xmlRel = "data/cutscene.xml";

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

        var jsonObject = new
        {
            meta_version = 42,
            is_demo = false,
            ui_start = "Start Expedition",
            dialog = new
            {
                line_1 = "Check\tinventory.\nMove to {0}.",
                line_2 = "<i>Aether Core</i> is unstable!"
            }
        };
        string jsonContent = JsonSerializer.Serialize(jsonObject, JsonWriteOptions);

        string xmlContent =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<dialogues version=\"1\">\n" +
            "  <line id=\"L001\" speaker=\"Aria\">Welcome to &lt;b&gt;Arcadia&lt;/b&gt;, {partner}!</line>\n" +
            "  <line id=\"L002\" speaker=\"Noah\">Hold the line &amp; wait for {0}.</line>\n" +
            "</dialogues>\n";

        WriteUtf8(Path.Combine(gameRootPath, srtRel), srtContent);
        WriteUtf8(Path.Combine(gameRootPath, jsonRel), jsonContent);
        WriteUtf8(Path.Combine(gameRootPath, xmlRel), xmlContent);

        return new LooseFixtureManifest(
            srtRel,
            jsonRel,
            xmlRel,
            [
                "<b>Welcome</b>, {player_name}!",
                "Start Expedition",
                "Check\tinventory.\nMove to {0}.",
                "Welcome to <b>Arcadia</b>, {partner}!"
            ]);
    }

    public static RpgMakerFixtureManifest CreateRpgMakerGame(string gameRootPath, bool isMv = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRootPath);
        Directory.CreateDirectory(gameRootPath);

        string dataRel = isMv ? "www/data" : "data";
        string dataDir = Path.Combine(gameRootPath, dataRel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(dataDir);

        if (isMv)
        {
            string wwwDir = Path.Combine(gameRootPath, "www");
            Directory.CreateDirectory(Path.Combine(wwwDir, "js"));
            WriteUtf8(Path.Combine(wwwDir, "package.json"), "{\"name\":\"mock-rpgmv\",\"main\":\"www/index.html\"}");
        }
        else
        {
            Directory.CreateDirectory(Path.Combine(gameRootPath, "js"));
            WriteUtf8(Path.Combine(gameRootPath, "game.rmmzproject"), "RPGMZ 1.7.0");
        }

        var map001 = new
        {
            autoplayBgm = false,
            displayName = "Aether Harbor",
            width = 17,
            height = 13,
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
                            conditions = new { },
                            list = new object[]
                            {
                                new { code = 101, indent = 0, parameters = new object[] { "Actor1", 0, 0, 2 } },
                                new { code = 401, indent = 0, parameters = new object[] { "Hello, \\N[1]!\nWelcome to \\C[2]Aether Harbor\\C[0]." } },
                                new { code = 401, indent = 0, parameters = new object[] { "Bring {crystal_count} shards." } },
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
                trigger = 0,
                switchId = 1,
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
            new { id = 1, expanded = false, name = "Aether Harbor", order = 1, parentId = 0 }
        };

        var systemJson = new
        {
            gameTitle = isMv ? "MockMV Odyssey" : "MockMZ Odyssey",
            versionId = 12345
        };

        string mapRel = $"{dataRel}/Map001.json";
        string commonRel = $"{dataRel}/CommonEvents.json";
        WriteUtf8(Path.Combine(dataDir, "Map001.json"), JsonSerializer.Serialize(map001, JsonWriteOptions));
        WriteUtf8(Path.Combine(dataDir, "CommonEvents.json"), JsonSerializer.Serialize(commonEvents, JsonWriteOptions));
        WriteUtf8(Path.Combine(dataDir, "MapInfos.json"), JsonSerializer.Serialize(mapInfos, JsonWriteOptions));
        WriteUtf8(Path.Combine(dataDir, "System.json"), JsonSerializer.Serialize(systemJson, JsonWriteOptions));

        return new RpgMakerFixtureManifest(
            isMv,
            dataRel,
            mapRel,
            commonRel,
            [
                "Hello, \\N[1]!\nWelcome to \\C[2]Aether Harbor\\C[0].",
                "Bring {crystal_count} shards.",
                "Long ago, the Warden sealed the sky.",
                "Accept mission",
                "Wait here",
                "Save often at crystals, \\N[1].",
                "Chapter One: Signal Lost",
                "Continue",
                "Title Screen"
            ],
            ["Actor1", "Guide", "EV_Captain", "CE_SystemHint"]);
    }

    public static UnityFixtureManifest CreateUnityGame(
        string gameRootPath,
        string gameName = "MockUnityGame")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameName);

        Directory.CreateDirectory(gameRootPath);
        string dataDir = Path.Combine(gameRootPath, $"{gameName}_Data");
        Directory.CreateDirectory(dataDir);

        File.WriteAllBytes(Path.Combine(gameRootPath, "UnityPlayer.dll"), "MOCK_UNITY_PLAYER"u8.ToArray());
        File.WriteAllBytes(Path.Combine(gameRootPath, $"{gameName}.exe"), "MZ_MOCK_EXE"u8.ToArray());

        string resourcesPath = Path.Combine(dataDir, "resources.assets");
        string shared0Path = Path.Combine(dataDir, "sharedassets0.assets");

        (string Name, string Script)[] resourceTexts =
        [
            ("IntroDialog", "<color=#FFD700><b>Commander</b></color>: Welcome, {player_name}!\nCheck sector %s."),
            ("LocTable_en", "{\"ui_play\":\"Play Expedition\",\"ui_exit\":\"Exit Game\"}")
        ];

        (string Name, string Script)[] sharedTexts =
        [
            ("Scene0_Cutscene", "<i>System</i>: Aether Core at {0}% capacity.")
        ];

        WriteUnityAssetsFile(resourcesPath, resourceTexts, includeNonTextAsset: true);
        WriteUnityAssetsFile(shared0Path, sharedTexts, includeNonTextAsset: false);

        List<(string Name, string Script)> allExpected = [.. resourceTexts, .. sharedTexts];
        return new UnityFixtureManifest(dataDir, resourcesPath, shared0Path, allExpected);
    }

    public static IReadOnlyList<(long PathId, string Name, string Script)> ReadUnityTextAssets(string assetsFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetsFilePath);

        AssetsManager manager = new();
        try
        {
            AssetsFileInstance instance = manager.LoadAssetsFile(assetsFilePath, loadDeps: true);
            List<(long PathId, string Name, string Script)> result = [];
            foreach (AssetFileInfo info in instance.file.GetAssetsOfType(AssetClassID.TextAsset))
            {
                AssetTypeValueField baseField = manager.GetBaseField(instance, info);
                string name = baseField["m_Name"].AsString;
                string script = baseField["m_Script"].AsString;
                result.Add((info.PathId, name, script));
            }

            return result;
        }
        finally
        {
            manager.UnloadAll(unloadClassData: true);
        }
    }

    public static UnrealFixtureManifest CreateUnrealGame(
        string gameRootPath,
        bool nestedProjectSubdir = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRootPath);
        Directory.CreateDirectory(gameRootPath);

        string contentRoot = nestedProjectSubdir
            ? Path.Combine(gameRootPath, "MockUnrealGame")
            : gameRootPath;
        string paksDir = Path.Combine(contentRoot, "Content", "Paks");
        Directory.CreateDirectory(paksDir);

        Dictionary<string, string> entries = new(StringComparer.Ordinal)
        {
            ["Dialog::Line_001"] = "<RichText.Bold>Welcome</>, {PlayerName}!",
            ["Dialog::Line_002"] = "Press [KEY]\tnow.\nProceed to sector {0}?",
            ["UI::Title"] = "Aether Core"
        };

        byte[] locresBytes = BuildLocresBytes(
        [
            ("Dialog", "Line_001", entries["Dialog::Line_001"]),
            ("Dialog", "Line_002", entries["Dialog::Line_002"]),
            ("UI", "Title", entries["UI::Title"])
        ]);

        byte[] locmetaBytes = BuildLocmetaBytes(
            nativeCulture: "en",
            nativeLocRes: "en/Game.locres",
            compiledCultures: ["en"]);

        string basePakPath = Path.Combine(paksDir, "pakchunk0-WindowsNoEditor.pak");
        string expectedModsPakPath = Path.Combine(paksDir, "~mods", "Lokasync_ID_P.pak");

        WritePakV8Size8a(
            basePakPath,
            mountPoint: "../../../Game/Content/",
            files:
            [
                ("Localization/Game/en/Game.locres", locresBytes),
                ("Localization/Game/Game.locmeta", locmetaBytes)
            ]);

        return new UnrealFixtureManifest(
            paksDir,
            basePakPath,
            expectedModsPakPath,
            "Game/Content/Localization/Game/en/Game.locres",
            entries);
    }

    public static IReadOnlyDictionary<string, string> ReadUnrealPakLocres(
        string pakDirectory,
        string locresVirtualPath = "Game/Content/Localization/Game/en/Game.locres")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pakDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(locresVirtualPath);

        using DefaultFileProvider provider = new(
            pakDirectory,
            SearchOption.AllDirectories,
            new VersionContainer(EGame.GAME_UE4_27),
            StringComparer.OrdinalIgnoreCase);

        provider.Initialize();
        provider.Mount();

        if (!provider.TryGetGameFile(locresVirtualPath, out GameFile? gameFile))
        {
            gameFile = provider.Files
                .FirstOrDefault(kv => kv.Key.EndsWith(".locres", StringComparison.OrdinalIgnoreCase))
                .Value;
        }

        if (gameFile is null)
        {
            throw new FileNotFoundException(
                $"Could not locate '{locresVirtualPath}' inside mounted Unreal .pak archives at '{pakDirectory}'. Mounted files: [{string.Join(", ", provider.Files.Keys)}]");
        }

        using FArchive archive = gameFile.CreateReader();
        FTextLocalizationResource locres = new(archive);

        Dictionary<string, string> result = new(StringComparer.Ordinal);
        foreach (KeyValuePair<FTextKey, Dictionary<FTextKey, FEntry>> nsPair in locres.Entries)
        {
            foreach (KeyValuePair<FTextKey, FEntry> keyPair in nsPair.Value)
            {
                result[$"{nsPair.Key.Str}::{keyPair.Key.Str}"] = keyPair.Value.LocalizedString ?? string.Empty;
            }
        }

        return result;
    }

    private static void WriteUnityAssetsFile(
        string outputPath,
        IReadOnlyList<(string Name, string Script)> textAssets,
        bool includeNonTextAsset)
    {
        AssetsFile afile = new()
        {
            Header = new AssetsFileHeader { Version = 22, Endianness = false },
            Metadata = new AssetsFileMetadata
            {
                UnityVersion = "2022.3.10f1",
                TargetPlatform = 19,
                TypeTreeEnabled = true,
                TypeTreeTypes = new List<TypeTreeType>(),
                AssetInfos = new List<AssetFileInfo>(),
                ScriptTypes = new List<AssetPPtr>(),
                Externals = new List<AssetsFileExternal>(),
                RefTypes = new List<TypeTreeType>(),
                UserInformation = string.Empty
            }
        };

        TypeTreeType textAssetTypeTree = BuildTextAssetTypeTree();
        afile.Metadata.TypeTreeTypes.Add(textAssetTypeTree);

        AssetTypeTemplateField textTemplate = new();
        textTemplate.FromTypeTree(textAssetTypeTree);

        long nextPathId = 1;
        foreach ((string name, string script) in textAssets)
        {
            AssetTypeValueField baseField = ValueBuilder.DefaultValueFieldFromTemplate(textTemplate);
            baseField["m_Name"].AsString = name;
            baseField["m_Script"].AsString = script;

            AssetFileInfo info = AssetFileInfo.Create(afile, nextPathId++, (int)AssetClassID.TextAsset, null, true);
            info.SetNewData(baseField);
            afile.Metadata.AddAssetInfo(info);
        }

        if (includeNonTextAsset)
        {
            TypeTreeType bundleTypeTree = BuildNamedObjectTypeTree((int)AssetClassID.AssetBundle, "AssetBundle");
            afile.Metadata.TypeTreeTypes.Add(bundleTypeTree);

            AssetTypeTemplateField bundleTemplate = new();
            bundleTemplate.FromTypeTree(bundleTypeTree);

            AssetTypeValueField bundleField = ValueBuilder.DefaultValueFieldFromTemplate(bundleTemplate);
            bundleField["m_Name"].AsString = "MockMainBundle";

            AssetFileInfo bundleInfo = AssetFileInfo.Create(afile, nextPathId, (int)AssetClassID.AssetBundle, null, true);
            bundleInfo.SetNewData(bundleField);
            afile.Metadata.AddAssetInfo(bundleInfo);
        }

        using AssetsFileWriter writer = new(outputPath);
        afile.Write(writer);
    }

    private static TypeTreeType BuildTextAssetTypeTree()
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

        return new TypeTreeType
        {
            TypeId = (int)AssetClassID.TextAsset,
            IsStrippedType = false,
            ScriptTypeIndex = 0xffff,
            ScriptIdHash = Hash128.NewBlankHash(),
            TypeHash = Hash128.NewBlankHash(),
            ExtTypeHash = Hash128.NewBlankHash(),
            TypeBlobIsDefinition = true,
            IsRefType = false,
            TypeDependencies = Array.Empty<int>(),
            TypeBlob = new TypeTreeBlob
            {
                Nodes = nodes,
                StringBufferBytes = sb.ToArray()
            }
        };
    }

    private static TypeTreeType BuildNamedObjectTypeTree(int classId, string className)
    {
        using MemoryStream sb = new();
        uint sClass = AddString(sb, className);
        uint sBase = AddString(sb, "Base");
        uint sString = AddString(sb, "string");
        uint sMName = AddString(sb, "m_Name");
        uint sArray = AddString(sb, "Array");
        uint sInt = AddString(sb, "int");
        uint sSize = AddString(sb, "size");
        uint sChar = AddString(sb, "char");
        uint sData = AddString(sb, "data");

        List<TypeTreeNode> nodes =
        [
            new() { Version = 1, Level = 0, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sClass, NameStrOffset = sBase, ByteSize = -1, Index = 0, MetaFlags = 0 },
            new() { Version = 1, Level = 1, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sString, NameStrOffset = sMName, ByteSize = -1, Index = 1, MetaFlags = 0x4000 },
            new() { Version = 1, Level = 2, TypeFlags = TypeTreeNodeFlags.Array, TypeStrOffset = sArray, NameStrOffset = sArray, ByteSize = -1, Index = 2, MetaFlags = 0x4000 },
            new() { Version = 1, Level = 3, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sInt, NameStrOffset = sSize, ByteSize = 4, Index = 3, MetaFlags = 0 },
            new() { Version = 1, Level = 3, TypeFlags = TypeTreeNodeFlags.None, TypeStrOffset = sChar, NameStrOffset = sData, ByteSize = 1, Index = 4, MetaFlags = 0 }
        ];

        return new TypeTreeType
        {
            TypeId = classId,
            IsStrippedType = false,
            ScriptTypeIndex = 0xffff,
            ScriptIdHash = Hash128.NewBlankHash(),
            TypeHash = Hash128.NewBlankHash(),
            ExtTypeHash = Hash128.NewBlankHash(),
            TypeBlobIsDefinition = true,
            IsRefType = false,
            TypeDependencies = Array.Empty<int>(),
            TypeBlob = new TypeTreeBlob
            {
                Nodes = nodes,
                StringBufferBytes = sb.ToArray()
            }
        };
    }

    private static uint AddString(MemoryStream ms, string value)
    {
        uint offset = (uint)ms.Position;
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        ms.Write(bytes, 0, bytes.Length);
        ms.WriteByte(0);
        return offset;
    }

    private static byte[] BuildLocresBytes(IReadOnlyList<(string Namespace, string Key, string Text)> entries)
    {
        using MemoryStream ms = new();
        using BinaryWriter bw = new(ms, Encoding.UTF8, leaveOpen: true);

        // ELocResVersion.Optimized_CRC32 = 2
        WriteFGuid(bw, 0x7574140Eu, 0xFC034A67u, 0x9D90154Au, 0x1B7F37C3u);
        bw.Write((byte)2);

        long offsetPlaceholderPos = ms.Position;
        bw.Write(0L); // LocalizedStringArrayOffset placeholder

        List<IGrouping<string, (string Namespace, string Key, string Text)>> groups = entries
            .GroupBy(e => e.Namespace, StringComparer.Ordinal)
            .ToList();

        bw.Write((uint)entries.Count);
        bw.Write((uint)groups.Count);

        List<string> stringLut = new(entries.Count);
        foreach (IGrouping<string, (string Namespace, string Key, string Text)> nsGroup in groups)
        {
            bw.Write(ComputeCrc32(nsGroup.Key));
            WriteFString(bw, nsGroup.Key);

            List<(string Namespace, string Key, string Text)> nsEntries = nsGroup.ToList();
            bw.Write((uint)nsEntries.Count);

            foreach ((string _, string key, string text) in nsEntries)
            {
                bw.Write(ComputeCrc32(key));
                WriteFString(bw, key);
                bw.Write(ComputeCrc32(text));
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
            bw.Write(1); // RefCount
        }

        ms.Position = offsetPlaceholderPos;
        bw.Write(lutOffset);
        bw.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildLocmetaBytes(
        string nativeCulture,
        string nativeLocRes,
        IReadOnlyList<string> compiledCultures)
    {
        using MemoryStream ms = new();
        using BinaryWriter bw = new(ms, Encoding.UTF8, leaveOpen: true);

        // ELocMetaVersion.AddedCompiledCultures = 1
        WriteFGuid(bw, 0xA14CEE4Fu, 0x83554868u, 0xBD464C6Cu, 0x7C50DA70u);
        bw.Write((byte)1);
        WriteFString(bw, nativeCulture);
        WriteFString(bw, nativeLocRes);
        bw.Write(compiledCultures.Count);
        foreach (string culture in compiledCultures)
        {
            WriteFString(bw, culture);
        }

        bw.Flush();
        return ms.ToArray();
    }

    private static void WritePakV8Size8a(
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

            // Inline 53-byte FPakEntry header (Offset = 0)
            WritePakEntryHeader(bw, offset: 0L, size: payload.LongLength, sha1);
            bw.Write(payload);

            indexEntries.Add((relativePath, startOffset, payload.LongLength, sha1));
        }

        // Guard FPakInfo OffsetsToTry.SizeMax = 253 minimum file length
        const int footerSize = 221;
        if (fs.Position + footerSize < 253)
        {
            long padBytes = 253 - footerSize - fs.Position;
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
                WritePakEntryHeader(idxBw, startOffset, size, sha1);
            }

            idxBw.Flush();
            indexBytes = idxMs.ToArray();
        }

        bw.Write(indexBytes);
        byte[] indexHash = SHA1.HashData(indexBytes);

        // FPakInfo (Size8a = 221 bytes):
        // 16 EncryptionKeyGuid + 1 EncryptedIndex + 4 Magic + 4 Version + 8 IndexOffset + 8 IndexSize + 20 IndexHash + 160 CompressionMethods
        bw.Write(new byte[16]);
        bw.Write((byte)0);
        bw.Write(0x5A6F12E1u);
        bw.Write(8);
        bw.Write(indexOffset);
        bw.Write((long)indexBytes.Length);
        bw.Write(indexHash);
        bw.Write(new byte[160]);
        bw.Flush();
    }

    private static void WritePakEntryHeader(BinaryWriter bw, long offset, long size, byte[] sha1)
    {
        bw.Write(offset);
        bw.Write(size);
        bw.Write(size);
        bw.Write(0); // CompressionMethod.None (4 bytes in Size8a sub-version)
        bw.Write(sha1); // 20 bytes
        bw.Write((byte)0); // Flags
        bw.Write(0u); // CompressionBlockSize
    }

    private static void WriteFGuid(BinaryWriter bw, uint a, uint b, uint c, uint d)
    {
        bw.Write(a);
        bw.Write(b);
        bw.Write(c);
        bw.Write(d);
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

    private static void WriteUtf8(string path, string content)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(path, content, Utf8NoBom);
    }
}
