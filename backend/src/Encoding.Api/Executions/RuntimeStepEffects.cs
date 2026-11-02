namespace Encoding.Api.Executions;

/// <summary>
/// Classe les blocs selon l'effet d'une exécution interrompue (ADR 0004). Un bloc rejouable ne laisse, s'il est interrompu, aucun effet
/// hors du dossier de travail du worker ou un effet identique à son rejeu : une autre exécution depuis le dernier checkpoint le refait
/// sans risque. Tout autre bloc, y compris d'un plugin inconnu, est supposé avoir un effet incertain.
/// </summary>
public static class RuntimeStepEffects
{
    private static readonly HashSet<string> Transforming=["audio","video","mkv","subtitles","probe","images","conditions","nodes","ai"];
    private static readonly HashSet<string> Integrations=["plex","jellyfin","radarr","sonarr"];

    public static bool IsReplayable(string pluginId,string componentId)
    {
        if(pluginId=="base") return componentId!="base-script";
        if(pluginId=="mkv") return componentId!="mkv-edit-in-place";
        if(Transforming.Contains(pluginId)) return true;
        // Actualiser, vérifier ou analyser rend le même état ; une recherche lance des téléchargements, donc un effet qu'on ne rejoue pas.
        return Integrations.Contains(pluginId) && !componentId.Contains("-search-",StringComparison.Ordinal);
    }
}
