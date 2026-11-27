namespace Lokasync.Adapters;

public static class EngineNames
{
    public const string Unreal = "Unreal";
    public const string Unity = "Unity";
    public const string RpgMaker = "RpgMaker";
    public const string LooseFile = "LooseFile";

    public static int GetPriority(string? engineName) => engineName switch
    {
        var name when string.Equals(name, Unreal, StringComparison.OrdinalIgnoreCase) => 0,
        var name when string.Equals(name, "Unreal Engine", StringComparison.OrdinalIgnoreCase) => 0,
        var name when string.Equals(name, Unity, StringComparison.OrdinalIgnoreCase) => 1,
        var name when string.Equals(name, RpgMaker, StringComparison.OrdinalIgnoreCase) => 2,
        var name when string.Equals(name, LooseFile, StringComparison.OrdinalIgnoreCase) => 3,
        _ => int.MaxValue
    };
}
