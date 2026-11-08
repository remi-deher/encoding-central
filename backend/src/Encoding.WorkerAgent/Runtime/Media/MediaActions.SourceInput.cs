namespace Encoding.WorkerAgent.Runtime.Media;

public sealed partial class MediaActions
{
    /// <summary>
    /// Entrée source d'une commande FFmpeg qui réécrit le média (copie de pistes, remux, transcodage) : <c>-fflags +genpts -i &lt;source&gt;</c>.
    /// Certaines sources courantes n'ont pas de PTS sur une partie des paquets vidéo alors que le DTS est connu : MPEG-4 Part 2
    /// (Xvid/DivX) avec images B, en AVI ou dans un MKV issu d'un AVI. Le muxer Matroska refuse alors d'écrire
    /// (« Can't write packet with unknown timestamp », code 234), l'erreur étant attribuée à la piste en cours d'écriture
    /// (#25, banc NAS #18). <c>+genpts</c> calcule le PTS manquant à partir des DTS ; sans effet sur les paquets qui ont déjà un PTS
    /// (paquets identiques au bit près sur des sources H.264 MKV/MP4 normales).
    /// </summary>
    public static string[] SourceInput(string path) => ["-fflags", "+genpts", "-i", path];
}
