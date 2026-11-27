using System.IO;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace Lokasync.Adapters.Internal;

/// <summary>
/// Builds a minimal in-memory <see cref="ClassDatabaseFile"/> for Unity base classes
/// (<c>TextAsset</c>, <c>LocalizationAsset</c>, <c>MonoBehaviour</c>, <c>MonoScript</c>)
/// so <see cref="AssetsManager"/> and <c>MonoCecilTempGenerator</c> can deserialize
/// stripped-TypeTree Unity assets when a <c>*_Data/Managed</c> folder is present.
/// </summary>
internal static class UnityMinimalClassDatabase
{
    private const uint AlignBytesFlag = 0x4000;
    private const byte ArrayTypeFlag = 0x01;

    public static ClassDatabaseFile Create(string unityVersion)
    {
        ClassDatabaseFile db = new()
        {
            Header = new ClassDatabaseFileHeader
            {
                Magic = "CLDB",
                FileVersion = 1,
                Version = new UnityVersion(string.IsNullOrWhiteSpace(unityVersion) ? "2017.4.40f1" : unityVersion),
                CompressionType = ClassFileCompressionType.Uncompressed
            },
            Classes = [],
            StringTable = new ClassDatabaseStringTable { Strings = [] },
            CommonStringBufferIndices = []
        };

        ClassDatabaseStringTable st = db.StringTable;

        db.Classes.Add(BuildTextAssetClass(st));
        db.Classes.Add(BuildLocalizationAssetClass(st));
        db.Classes.Add(BuildMonoBehaviourClass(st));
        db.Classes.Add(BuildMonoScriptClass(st));

        // Round-trip through MemoryStream via LoadClassDatabase if needed, or return directly.
        return db;
    }

    public static void LoadInto(AssetsManager manager, string unityVersion)
    {
        ArgumentNullException.ThrowIfNull(manager);

        ClassDatabaseFile db = Create(unityVersion);
        using MemoryStream ms = new();
        using AssetsFileWriter writer = new(ms);
        db.Write(writer, ClassFileCompressionType.Uncompressed);
        writer.Flush();
        ms.Position = 0;
        manager.LoadClassDatabase(ms);
    }

    private static ClassDatabaseType BuildTextAssetClass(ClassDatabaseStringTable st)
    {
        ClassDatabaseTypeNode root = Node(st, "TextAsset", "Base", -1,
            StringNode(st, "m_Name"),
            StringNode(st, "m_Script"));

        return CreateType(st, (int)AssetClassID.TextAsset, "TextAsset", "NamedObject", root);
    }

    private static ClassDatabaseType BuildLocalizationAssetClass(ClassDatabaseStringTable st)
    {
        ClassDatabaseTypeNode pairNode = Node(st, "pair", "data", -1,
            StringNode(st, "first"),
            StringNode(st, "second"));

        ClassDatabaseTypeNode arrayNode = new()
        {
            TypeName = st.AddString("Array"),
            FieldName = st.AddString("Array"),
            ByteSize = -1,
            Version = 1,
            TypeFlags = ArrayTypeFlag,
            MetaFlag = 0,
            Children =
            [
                PrimitiveNode(st, "int", "size", 4),
                pairNode
            ]
        };

        ClassDatabaseTypeNode mapNode = Node(st, "map", "m_LocalizedStrings", -1, arrayNode);

        ClassDatabaseTypeNode root = Node(st, "LocalizationAsset", "Base", -1,
            StringNode(st, "m_Name"),
            StringNode(st, "m_LocaleIsoCode"),
            mapNode);

        return CreateType(st, (int)AssetClassID.LocalizationAsset, "LocalizationAsset", "NamedObject", root);
    }

    private static ClassDatabaseType BuildMonoBehaviourClass(ClassDatabaseStringTable st)
    {
        ClassDatabaseTypeNode root = Node(st, "MonoBehaviour", "Base", -1,
            PPtrNode(st, "PPtr<GameObject>", "m_GameObject"),
            PrimitiveNode(st, "UInt8", "m_Enabled", 1, AlignBytesFlag),
            PPtrNode(st, "PPtr<MonoScript>", "m_Script"),
            StringNode(st, "m_Name"));

        return CreateType(st, (int)AssetClassID.MonoBehaviour, "MonoBehaviour", "Behaviour", root);
    }

    private static ClassDatabaseType BuildMonoScriptClass(ClassDatabaseStringTable st)
    {
        List<ClassDatabaseTypeNode> hashChildren = new(16);
        for (int i = 0; i < 16; i++)
        {
            hashChildren.Add(PrimitiveNode(st, "UInt8", $"bytes[{i}]", 1));
        }

        ClassDatabaseTypeNode hashNode = Node(st, "Hash128", "m_PropertiesHash", 16, hashChildren.ToArray());

        ClassDatabaseTypeNode root = Node(st, "MonoScript", "Base", -1,
            StringNode(st, "m_Name"),
            PrimitiveNode(st, "int", "m_ExecutionOrder", 4),
            hashNode,
            StringNode(st, "m_ClassName"),
            StringNode(st, "m_Namespace"),
            StringNode(st, "m_AssemblyName"));

        return CreateType(st, (int)AssetClassID.MonoScript, "MonoScript", "TextAsset", root);
    }

    private static ClassDatabaseType CreateType(
        ClassDatabaseStringTable st,
        int classId,
        string name,
        string baseName,
        ClassDatabaseTypeNode releaseRoot)
    {
        return new ClassDatabaseType
        {
            ClassId = classId,
            Name = st.AddString(name),
            BaseName = st.AddString(baseName),
            Flags = ClassFileTypeFlags.HasReleaseRootNode,
            EditorRootNode = null!,
            ReleaseRootNode = releaseRoot
        };
    }

    private static ClassDatabaseTypeNode Node(
        ClassDatabaseStringTable st,
        string typeName,
        string fieldName,
        int byteSize,
        params ClassDatabaseTypeNode[] children)
    {
        return new ClassDatabaseTypeNode
        {
            TypeName = st.AddString(typeName),
            FieldName = st.AddString(fieldName),
            ByteSize = byteSize,
            Version = 1,
            TypeFlags = 0,
            MetaFlag = 0,
            Children = [.. children]
        };
    }

    private static ClassDatabaseTypeNode PrimitiveNode(
        ClassDatabaseStringTable st,
        string typeName,
        string fieldName,
        int byteSize,
        uint metaFlag = 0)
    {
        return new ClassDatabaseTypeNode
        {
            TypeName = st.AddString(typeName),
            FieldName = st.AddString(fieldName),
            ByteSize = byteSize,
            Version = 1,
            TypeFlags = 0,
            MetaFlag = metaFlag,
            Children = []
        };
    }

    private static ClassDatabaseTypeNode PPtrNode(
        ClassDatabaseStringTable st,
        string typeName,
        string fieldName)
    {
        return Node(st, typeName, fieldName, 12,
            PrimitiveNode(st, "int", "m_FileID", 4),
            PrimitiveNode(st, "SInt64", "m_PathID", 8));
    }

    private static ClassDatabaseTypeNode StringNode(ClassDatabaseStringTable st, string fieldName)
    {
        ClassDatabaseTypeNode arrayNode = new()
        {
            TypeName = st.AddString("Array"),
            FieldName = st.AddString("Array"),
            ByteSize = -1,
            Version = 1,
            TypeFlags = ArrayTypeFlag,
            MetaFlag = AlignBytesFlag,
            Children =
            [
                PrimitiveNode(st, "int", "size", 4),
                PrimitiveNode(st, "char", "data", 1)
            ]
        };

        return new ClassDatabaseTypeNode
        {
            TypeName = st.AddString("string"),
            FieldName = st.AddString(fieldName),
            ByteSize = -1,
            Version = 1,
            TypeFlags = 0,
            MetaFlag = AlignBytesFlag,
            Children = [arrayNode]
        };
    }
}
