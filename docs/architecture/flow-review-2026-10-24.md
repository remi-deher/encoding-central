# Revue des flows et création guidée — 24 octobre 2026

Cette revue porte sur le code du dépôt. Les captures FileFlows ne sont pas présentes dans la session ; leur fonctionnement est décrit par la demande du propriétaire. Ce document distingue les évolutions réalisées des pistes qui nécessitent une décision ultérieure.

## Parcours observé et responsabilités

| Phase | Contrat actuel et effets |
| --- | --- |
| Construction | Catalogue des plugins → définition portable avec références exactes → validation backend → brouillon → publication immuable. Une bibliothèque référence une version précise. |
| Attribution | `RuntimeEligibility` vérifie outils, plugins, encodeurs utilisables, accès et contraintes de segment. Le serveur attributaire conserve baux et réservations locaux. Aucun rôle central permanent. |
| Entrée et analyse | `RuntimeFlowRunner` capture les métadonnées d'origine. L'entrée spécialisée analyse immédiatement ; l'entrée générique autorise des préparatifs puis impose l'analyse avant toute dépendance ou transformation. L'inspection directe peut éviter la copie jusqu'au premier effet. |
| Préparation | `base-prepare-processing` initialise le plan et sa politique de conservation. Les actions en mode `plan` ajoutent des `RuntimePlanStep` au contexte durable. Elles ne produisent pas nécessairement un fichier. Crop, détection d'entrelacement et règles audio peuvent analyser pour déterminer quoi ajouter. Les conditions lisent le média courant, pas un résultat futur du plan. |
| Exécution | `base-execute-processing` regroupe les opérations FFmpeg consécutives via `FfmpegPlanAsync`. Vidéo, filtres, audio et conteneur compatibles alimentent une commande et une seule sortie temporaire. Les autres outils constituent des frontières de lots. Normalisation en deux passes et détection de crop peuvent nécessiter des commandes d'analyse supplémentaires. |
| Résultat | `ApplyOutcomeAsync` analyse et valide les candidats, conserve leurs métadonnées et artefacts ; l'origine reste distincte. Le plan est vidé après succès de l'exécuteur. Un plan restant en fin de flow est une erreur. |
| Publication | `base-publish-result` rend la frontière visible ; sans lui, publication en fin de flow. Le fichier exact est contrôlé et décodé avant publication, avec contrôle du bail, de l'époque et de la source. Le nœud d'accès publie ; le flow ne décide pas du remplacement. |

Le principe montré par FileFlows existe donc déjà. Introduire un second builder ou fusionner automatiquement les blocs autonomes créerait deux sémantiques concurrentes. L'évolution retenue utilise le plan existant et rend son emploi accessible depuis la création guidée.

## Évolutions réalisées

- Générateur backend sans effet externe (`POST /api/v1/flows/guided`, accès de gestion par défaut). Il vérifie les choix, assemble les versions installées puis applique `FlowDefinitionValidator`. Il ne publie ni flow ni média.
- Assistant depuis l'accueil et le catalogue ; flow vierge, démarrages existants et modèles restent disponibles. Étapes : général, vidéo, pistes, résultat, récapitulatif. Un brouillon est enregistré puis ouvert dans l'éditeur commun. Un refus conserve les choix. Les options de codec et de qualité sont lues dans le catalogue backend installé ; un échec de chargement propose une nouvelle tentative.
- Graphe généré : entrée vidéo → préparation FFmpeg → vidéo → audio facultatif → conteneur MKV → exécution → publication. `audio.unchanged` rejoint la suite ; `execute.skipped` rejoint la publication ; les erreurs non connectées échouent. Aucun faux chemin de succès après erreur.
- Le flow généré encode la vidéo choisie ; il ne déduit pas une conformité de qualité du seul codec source. Une condition de saut peut être ajoutée dans l’éditeur.
- Défauts : HEVC, profil Équilibré embarqué, encodeur automatique vérifié, qualité adaptative équilibrée, profondeur automatique, HDR préservé, audio et toutes les pistes de sous-titres d’origine copiés, secours CPU désactivé. Aucun sous-titre externe ni aucune police ne sont ajoutés automatiquement. CPU explicite et adaptation audio intégrée disponibles. Une disposition audio inconnue est refusée plutôt que devinée. Le mode automatique choisit selon les priorités du profil, sans garantir du matériel sur un cluster CPU seul.
- Le contrôle de décodage d’intégrité n’est pas ajouté au flow guidé ni aux modèles standards par défaut. Les contrôles structurels, empreintes et validation de sortie avant publication restent actifs ; le bloc `probe-check-integrity` peut être ajouté explicitement dans l’éditeur.
- Une analyse locale partagée pendant la composition d'une commande pour sélection de pistes, décision de resize et politique vidéo. Aucun cache inter-fichier ou inter-étape : un candidat et une publication restent analysés séparément. L'échec d'analyse vidéo n'est plus ignoré. Le secours CPU recompose sur la même entrée et refait son analyse.

## Erreurs, temporaires et reprise

Le secours CPU existant supprime la sortie matérielle partielle et réessaie une seule fois depuis l'entrée du lot ; les autres opérations du lot sont rejouées avec la vidéo. Ce choix reste explicite. Une erreur d'infrastructure de plugin ne devient jamais une sortie métier. Aucun ajout de relance automatique ni de migration de version de flow. Le plugin vidéo passe en 1.3.0 pour identifier le contrôle d’analyse renforcé ; les références historiques restent résolubles.

Chaque préparation reste observable comme étape ; la commande réelle appartient à l'exécuteur (`RuntimeCommandCapture`, début/fin, code de sortie, erreurs bornées), les résolutions d'encodeurs gardent le bloc vidéo d'origine. Le suivi existant conserve l'étape active ; cette évolution n'ajoute pas de pourcentage FFmpeg. Les lots n'ont pas de checkpoint à l'intérieur d'un encodage. La reprise conserve les règles existantes : checkpoint durable, artefact courant et plan, bail/époque, vérifications d'accès et de capacités ; une étape à effet incertain ne devient pas rejouable du fait du regroupement. Les sorties partielles ne sont pas des candidats publiables.

L'attribution, le transfert WebDAV avec empreintes, la validation des candidats, la garde de publication, la réservation du média et la politique de remplacement ne sont pas modifiés. Les flows existants ne sont pas réécrits. Aucun nouveau contrat worker ni stockage partagé n'est introduit.

## Pistes évaluées et non retenues dans cette évolution

| Piste | Motif / suite nécessaire |
| --- | --- |
| Fusion automatique des blocs autonomes | Changerait les points d'erreur, métadonnées intermédiaires et checkpoints. Demande une décision d'architecture et une analyse de dépendances par chemin. |
| Filtrage de langues et transformation de sous-titres dans le même lot | Les opérations MKVToolNix et les conversions/extractions ont leurs propres effets et contraintes. Ne pas les présenter comme une commande FFmpeg unique avant de définir un modèle commun de sélection/mapping. L'assistant conserve toutes les pistes. |
| Choix MP4 dans le parcours initial | Les sous-titres, pièces jointes et codecs copiés ne sont pas tous compatibles. MKV est le conteneur initial ; l'éditeur conserve les options avancées existantes. |
| Distinction film/série | Aucun effet média natif distinct à encoder ici ; les intégrations et bibliothèques portent ces besoins. Ne pas inventer un paramètre sans effet. |
| Supprimer les contrôles après traitement ou avant publication | Incompatible avec les garanties du projet. Seules les inspections redondantes de la même entrée pendant la composition sont mutualisées. |
| Réduire les copies et empreintes de checkpoint | Les artefacts durables assurent transfert et reprise ; mesurer avant de modifier leur durée de vie ou leur persistance. Les chemins d'inspection directe existants évitent déjà la matérialisation des plans vides. |
| Progresser en pourcentage dans un lot | Piste utile : protocole FFmpeg `-progress`, durée connue et rapport borné, sans confondre 100 % et succès. À concevoir dans le suivi commun, avec annulation et erreurs ; pas une estimation inventée dans l'assistant. |

## Vérification

Les tests ajoutés vérifient les références installées, la validation complète des graphes, les choix invalides, les chemins inchangé/erreur et le caractère optionnel du secours CPU. Le scénario média synthétique vérifie une seule sonde d'entrée pour sélection + resize, une seule commande FFmpeg, les pistes résultantes et la préservation de la source. Le parcours navigateur (API simulée) vérifie création du brouillon, navigation et conservation des choix après refus, aux tailles ordinateur et mobile. Les résultats d'exécution de ces vérifications sont rapportés avec la livraison ; ils ne constituent pas une validation GPU ou multi-hôtes réelle.
