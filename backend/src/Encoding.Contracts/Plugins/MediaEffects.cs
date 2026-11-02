namespace Encoding.Contracts.Plugins;

/// <summary>Effects on the current work file, never permission to write a library source.</summary>
public static class MediaEffects
{
    public const string ReadOnly = "readOnly";
    public const string ProducesOutput = "producesOutput";
    public const string ModifiesCurrent = "modifiesCurrent";

    public static bool IsValid(string? value) => value is null or ReadOnly or ProducesOutput or ModifiesCurrent;
}
