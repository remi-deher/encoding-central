# Flow « HEVC + AAC/AC3 + ASS (Crunchyroll) » : équivalent du flow FileFlows V3

Ce guide décrit comment reconstruire dans l'application le flow FileFlows « V3 – HEVC + AAC/AC3 + ASS Crunchyroll » avec les blocs du catalogue. Aucun fichier de flow n'est livré avec le projet : on le construit dans l'éditeur. Le graphe active le repli d'un encodeur matériel vers CPU (QSV dans cet exemple) et ajoute un bloc de validation configurable après l'assemblage.

## Mettre le flow en service

1. Page **Flows** : créer le flow dans l'éditeur avec les blocs du déroulé ci-dessous (voir [flows](../architecture/domains/flows.md)).
2. Ouvrir le flow dans l'éditeur : régler le nom du disque du bloc **Verrou de disque** (`disque-1` par défaut ; le même nom pour toutes les bibliothèques d'un même disque physique), le jeu de polices et les contrôles de sortie. Cet exemple choisit QSV et active le repli CPU; le même réglage fonctionne aussi avec NVENC et VAAPI. Si une branche conserve des sources HDR/10-bit dans leur codec d'origine, choisir **HEVC ou codec source conservé** dans le validateur. Publier.
3. Attribuer la version publiée à une bibliothèque. Si besoin, créer un modèle depuis cette version ([flows](../architecture/domains/flows.md)) pour en dériver d'autres flows.
4. Prérequis sur les nœuds : FFmpeg, MKVToolNix, plugin `base` 1.9.0 ou plus, plugin `plex` 1.1.0 (déclarés dans `Runtime:Plugins`, voir `docker-compose.yml`), et le dossier de polices du nœud (`ENCODING_FONT_DIR/Crunchyroll/…`). Un nœud qui ne satisfait pas ces conditions ne reçoit pas le flow.
5. Plex : renseigner l'adresse, le jeton et la correspondance de chemins du plugin ([plugins](../architecture/domains/plugins.md)). Pour protéger le remplacement de la source, activer la politique de remplacement de la bibliothèque ([runtime](../architecture/domains/runtime.md), désactivée par défaut) ; la garde de Plex s'applique alors ([runtime](../architecture/domains/runtime.md)).

## Déroulement

```
Fichier vidéo → Lecture Plex ──lecture──→ Reporter 10 min
                    │ libre / erreur Plex
                    ▼
              Verrou de disque ──occupé──→ Reporter 2 min
                    │ pris
                    ▼
        Noms de pistes (instantané) → Décisions codec/HDR
                    → Début FFmpeg → Vidéo HEVC → Audio par règles → Remux MKV
                    → Exécuter FFmpeg → Décisions sur le résultat réel
                    → Assembler le résultat MKV (ASS + polices + noms)
                    → Contrôles après assemblage → Publier → Actualisations → Terminé

N'importe quelle étape en erreur → Échec (la source reste intacte)
```

## Correspondance FileFlows → encoding

| Bloc FileFlows | Bloc encoding | Ancien ADR (voir [table](../architecture/legacy-adr-map.md)) | Écart |
| --- | --- | --- | --- |
| Entrée VideoFile | `base-input-video` | — | Scan imposé avant toute transformation (ancien 0006) |
| 0. Pause lecture Plex | `plex-check-playback` (fichier + épisode précédent) → « Reporter » | 0059 | Report de 10 minutes sans occuper de worker ; pas de Watchdeck (disques en pause) |
| 3. Verrou par disque, report si occupé | `base-disk-lock` → « Reporter » (2 min) | 0058 | Nom de disque saisi dans le bloc |
| 0b. Relevé des pistes d'origine | `base-track-names` (instantané) | 0056 | Conserve les noms d'origine (VFF, VFQ…) |
| 3. Vidéo | `video-transcode` (HEVC, QSV demandé, CPU en repli) | 0069, 0075 | Si le repli est activé, une erreur FFmpeg sur NVENC, QSV ou VAAPI déclenche un seul nouvel essai CPU ; profondeur de couleur et métadonnées HDR conservées |
| Déjà HEVC / HDR / 10 bits protégés | `conditions-video-codec`, `conditions-video-hdr`, `conditions-video-bitdepth` | 0067, 0068 | Blocs déjà disponibles : les relier à une branche qui copie la vidéo et saute `video-transcode` selon les règles du flow |
| 2. Audio : stéréo→AAC, 2.1+→AC3 | `base-audio-rules` (ajouté au plan) | 0055 | Disposition ambiguë : sortie « Erreur » |
| 1. Sous-titres | `base-assemble-media` (gestion des sous-titres activée) | 0057 | Profil Crunchyroll ou style personnalisé versionné |
| Polices | `base-assemble-media` (ajout des polices activé) | 0057 | Jeu déclaré par le nœud |
| 4. Assemblage mkvmerge en une écriture | `base-ffmpeg-builder-execute` puis `base-assemble-media` | 0015, 0055–0057 | FFmpeg vidéo/audio en une commande ; assemblage MKV distinct, validation et checkpoint après chaque résultat adopté |
| 4b. Noms de pistes | `base-assemble-media` (noms activés) | 0056 | Modèle de nom modifiable |
| 29, 13. Garde Plex puis remplacement atomique | politique de remplacement de la bibliothèque + garde Plex | 0030, 0059 | Remplacement désactivé par défaut ; garde pour tous les remplacements du cluster |
| Contrôles post-assemblage | `probe-validate-output` | 0075 | Vérifie au choix HEVC, pistes vidéo/audio, codecs audio et sous-titres, canaux/disposition/LFE, chapitres, pièces jointes, durée et événements ASS |
| 14–15, 26–27. Actualisation Plex / Radarr / Sonarr | `base-publish-result` puis `plex-refresh-library`, `radarr-refresh-movie`, `sonarr-refresh-series` | 0003 | Le bloc de publication fait passer le fichier en place pendant le flow ; les actualisations le suivent. |
| Scripts avec `Flow.Execute` | `base-script` avec l'aide `tools` | 0054 | Bac à sable Landlock conservé |

## Comportement dans les cas d'échec

| Situation | Résultat |
| --- | --- |
| Lecture en cours dans Plex (fichier ou épisode précédent) | Exécution reportée de 10 min, aucun worker occupé |
| Disque occupé par un autre traitement | Reportée de 2 min ; le verrou de l'autre exécution est intact |
| Plex injoignable pendant la vérification | Le flow continue ; la garde de publication refuse le remplacement de la source (le résultat distinct est publié) |
| Disposition audio ambiguë | Sortie « Erreur » → Échec, source intacte |
| Outil ou plugin absent sur le nœud | Le nœud ne reçoit pas le flow |
| Perte du worker en cours de traitement | Le bail expire ; l’exécution est reprise au dernier checkpoint par un autre worker éligible (2 reprises au plus, ADR 0004). Un bloc à effet incertain (script, modification sur place, recherche Sonarr/Radarr) échoue et se relance explicitement ; un encodage interrompu est refait depuis le début du bloc |
| Perte du nœud d'accès pendant la publication | La publication est reprise sans double bascule ([runtime](../architecture/domains/runtime.md)) |

## Limites connues

- **Actualisation Plex / Radarr / Sonarr** : placer `base-publish-result` après « Exécuter le traitement », puis les blocs d'actualisation (ADR 0003).
- **Contrôles ASS** : le bloc peut comparer les événements texte source/sortie lorsqu'on active l'option. La comparaison du résultat aux fichiers ASS préparés et au plan de sous-titres FileFlows reste propre à ce script.
- **QSV n'a jamais été vérifié sur un vrai GPU** (hôtes TrueNAS) : à tester avant de compter sur l'encodage matériel.
- **Modification sur place** : le bloc `mkv-edit-in-place` (ADR [0002](../architecture/adr/0002-modification-mkv-sur-place.md)) modifie langue, nom et drapeaux d'une piste directement dans la source, avec copie intégrale et restauration. Il n'est pas dans le flow importable : un flow « métadonnées seules » peut l'utiliser à la place du remux. Il ne gère ni chapitres ni pièces jointes (ajout de polices sur place non couvert).
- Non exercés dans le conteneur de développement : Landlock, `ProcessTests`, un vrai serveur Plex, les hôtes TrueNAS.

## Choisir et régler les blocs

Pour une seule opération, préférer les blocs ciblés de sous-titres (conversion ASS, style, doublons, forcés, noms ou piste audio par défaut). Le bloc complet conserve ses options historiques pour les flows existants. Un style « aucun » sur la conversion SRT → ASS convertit le format sans remplacer le style fourni par la conversion.

Les règles audio sont évaluées dans l'ordre : première correspondance, puis conservation des pistes sans correspondance. Les critères avancés actifs restent visibles dans le résumé replié. Une fréquence explicite est appliquée même si le codec est déjà conforme ; zéro conserve celle de la source. Dans un même plan, une normalisation conserve le codec de conversion demandé. Les normalisations multiples composent une chaîne de filtres.

Les sélecteurs proposent les ressources observées et conservent une référence enregistrée qui n'est plus disponible. Le nom du worker est affiché, son identifiant reste stocké. Un jeu de polices doit être annoncé par le worker choisi ; un ancien worker sans inventaire doit être mis à jour. Une liste de fichiers de polices précise reste vérifiée au traitement.

Pour réutiliser une extraction, choisir un fichier produit par une étape précédente : `artifact:<identifiant-encodé>/<indice>`, avec indice commençant à zéro. La référence survit au relais entre workers grâce au stockage durable. Si la branche suivie saute l'extraction, l'exécution échoue explicitement. Les fichiers de bibliothèque utilisent leurs chemins relatifs habituels.

Les nouvelles opérations et options exigent les versions minimales déclarées par leur schéma (notamment les opérations ciblées Subtitles 1.1.0). Les références de fichiers produits exigent la capacité worker `artifact-references`. L'aperçu et l'attribution appliquent ces contrats ; la publication d'un flow ne garantit pas qu'un worker compatible soit actuellement connecté.

## Frontières de préparation et métadonnées
Ouvrir **Début FFmpeg** après les décisions initiales. Vidéo, règles audio et remux préparent la même commande ; les conditions à l'intérieur lisent encore l'entrée. Les sorties `success` et `skipped` de **Exécuter FFmpeg** rejoignent la suite ; `error` peut ouvrir un nouveau builder CPU explicite. Après succès, les conditions lisent les métadonnées rescannées du résultat, et un second builder utilise ce fichier. Après erreur, elles lisent le dernier fichier valide, jamais une sortie partielle.

Placer **Assembler le résultat MKV** après l'Executor, activer les options ASS/polices/noms voulues et relier `success` et `unchanged`. Ce bloc évite des copies et checkpoints intermédiaires entre ces trois opérations, sans supprimer la conversion des sous-titres elle-même. Les scripts FileFlows ne sont pas importés ou exécutés automatiquement ; leurs opérations autonomes se placent hors du builder. Les anciens blocs restent disponibles pour les flows existants.
