using Encoding.Contracts.Plugins;

namespace Encoding.NodeAgent.Runtime;

/// <summary>Enforces the declared result contract inside the workspace; not a sandbox boundary.</summary>
public sealed class MediaEffectGuard
{
    private readonly string? effect;
    private readonly string workspace;
    private readonly string? currentName;
    private readonly string? current;
    private readonly MediaFileStamp? before;

    public MediaEffectGuard(string? effect, string workspace, string? currentName)
    {
        if(!MediaEffects.IsValid(effect)) throw new IOException("Le plugin déclare un effet média inconnu.");
        this.effect=effect; this.workspace=workspace; this.currentName=currentName;
        current=currentName is null?null:SafeMediaPaths.Resolve(workspace,currentName);
        before=current is not null && File.Exists(current)?MediaFileStamp.Read(current):null;
    }

    public string? Validate(string? candidate)
    {
        if(effect is MediaEffects.ReadOnly or MediaEffects.ProducesOutput && before is not null) before.Verify(current!);
        if(effect==MediaEffects.ReadOnly && candidate is not null)
            throw new IOException("Un composant readOnly ne peut pas renvoyer de média candidat.");
        if(effect==MediaEffects.ProducesOutput && candidate is not null
            && string.Equals(SafeMediaPaths.Resolve(workspace,candidate),current,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))
            throw new IOException("Un composant producesOutput doit renvoyer un nouveau fichier.");
        // Also covers same-size edits with a deliberately preserved timestamp.
        return candidate ?? (effect==MediaEffects.ModifiesCurrent?currentName:null);
    }
}
