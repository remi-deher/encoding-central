using System.Text.Json;
using Encoding.Core;
using Encoding.Core.Nodes;

namespace Encoding.Launcher;

/// <summary>
/// Persiste le mode du nœud dans son dossier de données. Le fichier prévaut sur la variable <c>Node__Roles</c>,
/// qui ne donne que la valeur initiale ; un fichier illisible est refusé plutôt que remplacé silencieusement.
/// </summary>
internal sealed class NodeModeStore(string directory, string? initialRoles, bool fixedWorker = false)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string FilePath => Path.Combine(directory, "node-mode.json");

    public NodeMode Load()
    {
        if (!File.Exists(FilePath)) return Clamp(NodeMode.Parse(initialRoles));
        try
        {
            var mode = JsonSerializer.Deserialize<NodeMode>(File.ReadAllText(FilePath), Json);
            return Clamp(NodeMode.Validate(mode ?? throw new InvalidOperationException("fichier vide")));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException($"Le mode du nœud enregistré dans {FilePath} est invalide : {exception.Message}", exception);
        }
    }

    public void Save(NodeMode mode)
    {
        mode = Clamp(mode);
        Directory.CreateDirectory(directory);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(NodeMode.Validate(mode), Json));
        File.Move(temporary, FilePath, overwrite: true);
        AtomicFilePublication.Confirm(FilePath);
    }

    private NodeMode Clamp(NodeMode mode) => fixedWorker ? new NodeMode(false, true) : mode;
}
