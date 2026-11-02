using System.Globalization;

namespace Encoding.Contracts.Flows;

/// <summary>Référence à une version de ressource du catalogue du cluster, écrite « identifiant:version » dans la configuration d'un bloc.</summary>
public readonly record struct ResourceReference(Guid Id, int Version)
{
    public override string ToString() => $"{Id:D}:{Version.ToString(CultureInfo.InvariantCulture)}";

    public static bool TryParse(string? text, out ResourceReference reference)
    {
        reference = default;
        var parts = (text ?? "").Trim().Split(':');
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out var id) || id == Guid.Empty
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1) return false;
        reference = new(id, version);
        return true;
    }
}

/// <summary>Fichier d'une version de ressource tel que le serveur l'annonce à un worker.</summary>
public sealed record ResourceFileInfo(string Name, string Sha256, long Bytes);

/// <summary>Contenu d'une version de ressource, annoncé à un worker.</summary>
public sealed record ResourceVersionInfo(Guid ResourceId, int Version, IReadOnlyList<ResourceFileInfo> Files);
