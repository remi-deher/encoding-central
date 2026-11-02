using System.Globalization;
using System.Text.Json;

namespace Encoding.Contracts.Flows;

public sealed class FlowArtifactReferenceException(string message) : IOException(message);

/// <summary>Reference to a durable additional file produced earlier in the same execution.</summary>
public sealed record FlowArtifactReference(string NodeId, int Index)
{
    public const string Prefix = "artifact:";
    public const string Capability = "artifact-references";
    public string VariableKey => "file." + Uri.EscapeDataString(NodeId) + "." + Index.ToString(CultureInfo.InvariantCulture);
    public override string ToString() => Prefix + Uri.EscapeDataString(NodeId) + "/" + Index.ToString(CultureInfo.InvariantCulture);
    public static bool TryParse(string? value, out FlowArtifactReference reference)
    {
        reference = null!;
        if (value is null || !value.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var parts = value[Prefix.Length..].Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index is < 0 or > 63) return false;
        var node = Uri.UnescapeDataString(parts[0]);
        if (string.IsNullOrWhiteSpace(node) || node.Length > 128 || node.Any(char.IsControl)) return false;
        reference = new(node, index);
        return true;
    }
    public JsonElement Resolve(IReadOnlyDictionary<string, JsonElement> variables)
    {
        if (!variables.TryGetValue(VariableKey, out var value))
            throw new FlowArtifactReferenceException("Le bloc « " + NodeId + " » n’a pas produit le fichier " + (Index + 1) + " sur le chemin suivi.");
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("sha256", out var digest)
            || digest.ValueKind != JsonValueKind.String || !System.Text.RegularExpressions.Regex.IsMatch(digest.GetString()!, "^[0-9a-f]{64}$")
            || !value.TryGetProperty("name", out var file) || file.ValueKind != JsonValueKind.String
            || file.GetString() is not { Length: > 0 and <= 255 } name || name is "." or ".." || name.Any(char.IsControl) || name.IndexOfAny(['/', '\\', ':']) >= 0)
            throw new FlowArtifactReferenceException("Référence de fichier produit corrompue.");
        return value;
    }
}
