---
domain: flows
updated: 2026-10-30
legacy_adr: [0003, 0004, 0006, 0007, 0011, 0012, 0013, 0014, 0015, 0022, 0023, 0024, 0025, 0026, 0027, 0060-api, 0079, 0080, 0081]
code:
  - backend/src/Encoding.Contracts/Flows/
  - backend/src/Encoding.Api/Flows/
  - backend/src/Encoding.Api/Libraries/
  - frontend/src/app/shared/models/ (flow-*.ts, media-tool-actions.ts)
  - frontend/src/app/features/flows/
tags: [flow, graphe, validation, version, brouillon, modèle, pack, script, profil, simulation, condition]
---

# Flows : définition, validation, stockage

L'exécution d'un flow (baux, workers, publication) est dans [runtime.md](runtime.md) ; les composants fournis par les plugins dans [plugins.md](plugins.md) ; l'éditeur visuel dans [ui.md](ui.md).

## Modèle
- Une **définition portable** (`FlowDefinition`, JSON, `formatVersion` 1 distinct de la version métier) : `id` (UUID), `version`, `name`, `mediaType` (`video|image|music|subtitles`), `nodes`, `connections`. Chaque bloc référence les versions **exactes** du plugin, du composant et du schéma, et porte seulement ses valeurs de configuration (jamais de code d'interface). Les positions de canevas sont de la présentation, pas de la sémantique.
- Graphe orienté acyclique, **une seule entrée** sans liaison entrante (Fichier générique ou entrée spécialisée du type de flow), une connexion maximum par sortie, plusieurs sorties pouvant rejoindre la même entrée (chemins alternatifs **séquentiels**, ni parallélisme ni barrière). Plusieurs blocs terminaux permis. Boucles : prévues comme nœuds conteneurs, **non livrées**. Cycles et auto-connexions interdits ; tous les blocs accessibles depuis l'entrée.
- Le type de média appartient au flow, pas à la bibliothèque. Les composants déclarent leurs types de flow compatibles (ex. opérations MKVToolNix : conteneur vidéo uniquement).
- Un résultat de plugin hors sorties déclarées = erreur de contrat ; une panne du processus = erreur d'infrastructure, jamais convertie en sortie métier `error`.

## Validation backend (`FlowDefinitionValidator`)
- Autorité unique : l'éditeur n'est pas une frontière de validation. Appliquée à `POST /api/v1/flows/validate` (422 avec `code`, chemin JSON Pointer, message), à la publication, à l'import et avant création d'exécution.
- Contrôles : format, UUID, identifiants uniques, entrée unique, références exactes plugin/composant/schéma (ou `compatiblePluginVersions` explicites), compatibilité média, sorties déclarées, acyclicité, accessibilité, types/bornes/options des champs, champs inconnus rejetés. Limites : 1000 blocs, 4000 connexions, 1 Mo. Un brouillon accepte champs obligatoires manquants et blocs non reliés ; il ne peut pas être exécuté.
- Catalogue : `GET /api/v1/flows/components` (manifestes installés via `FlowComponentRegistry`). Une référence disponible dans le registre ne prouve pas qu'un worker a le plugin ou l'outil. Un composant sans manifeste installé échoue explicitement.

## Versions, brouillons, sauvegardes
- Versions publiées **immuables** (clé + trigger). `PUT /api/v1/flows/{id}` avec `expectedVersion` ; version suivante = `expected + 1` ; conflit = 409 ; répétition identique idempotente ; publication inchangée (comparaison ignorant `version` et ordre des propriétés) renvoie la version existante sans nouveau numéro.
- Dans l'éditeur et les actions groupées du catalogue, comparer le brouillon à sa dernière version publiée avant l'appel de publication. Un contenu identique n'est pas publié ; les brouillons modifiés doivent passer la validation backend. Le backend reste la garde finale et n'ajoute jamais de version publiée identique, y compris en cas de requête répétée.
- Brouillon : révision propre avec contrôle de concurrence, enveloppe `{"kind":"draft","draftRevision":n,"definition":{}}`. Publier valide puis supprime le brouillon dont la révision est confirmée. Révisions de brouillon sauvegardées de façon immuable ([replication-storage.md](replication-storage.md)).
- Suppression : `DELETE /api/v1/flows/{id}` — 409 si exécution en file/en cours ; 409 si affecté à une bibliothèque, sauf `?detachLibraries=true` (détache dans la même transaction et incrémente `flow_assignment_revision`) ; flow jamais publié = suppression directe, sinon suppression logique (`deleted_at`, versions conservées). L'IA et MCP appellent sans `detachLibraries` (donc protégés). `DELETE …/draft`, `POST …/clone`, `POST /flows/from-template/{id}` existent.
- Import/export : JSON (une définition ou un brouillon) et ZIP (toutes les versions publiées + brouillon de chaque flow sélectionné). Import validé en bloc puis transaction unique, idempotent si identique, conflit sinon (jamais d'écrasement). Limites : 100 flows, 1000 documents, 20 Mio, 1 Mio/document, aucune extraction sur disque. La config globale des plugins et les dérogations ne sont **pas** exportées.
- Fiche du catalogue : aperçu lecture seule, versions, restauration d'une version ou sauvegarde de brouillon **dans le brouillon** (confirmation, jamais de publication), utilisation (`GET …/usage`, `GET …/draft-backups[/{rev}]`), preflight.

## Bibliothèques et flows
- Une bibliothèque a **zéro ou un** flow actif, référencé par `flowId` + version publiée précise ; un flow peut servir plusieurs bibliothèques. Mises à jour du flow sans effet silencieux. Sans flow : aucun traitement automatique.
- `PUT /api/v1/libraries/{id}/flow` (`expectedRevision`), `POST …/discover`, `POST …/executions` (`fileIds` + `assignmentRevision` ; le backend retrouve chemins et définition). Les anciens endpoints de création manuelle d'exécution répondent **410**. Aucun chemin média n'est saisi par l'utilisateur.
- Découverte, surveillance, plafonds : [runtime.md](runtime.md).

## Scan obligatoire et métadonnées
- Métadonnées de la source capturées **avant** toute modification. Entrée spécialisée : scan immédiat. Entrée Fichier générique : scripts préparatoires possibles, scan imposé avant la première opération qui exige des métadonnées ou transforme le média. Sortie temporaire analysée avant validation/publication. Instantanés avant/après conservés par tentative et immuables ; un échec de scan initial bloque, un échec de scan de sortie empêche la publication. Une information absente reste absente (jamais un zéro).

## Conditions, simulation
- Plugin `conditions` ([plugins.md](plugins.md)) : champ commun `condition-tree` (groupes ET/OU imbriqués, ≤ 100 éléments, ≤ 8 niveaux) évalué nativement, court-circuit, champ absent = sortie `error` sauf `exists/missing`. Catalogue de champs/opérateurs dans `Encoding.Contracts`.
- Testeur (`POST /api/v1/flows/simulate`, 1 Mio, 422/400) sans effet externe : parcourt une branche, **résultats supposés explicites** pour tout bloc externe (conversion, script, plugin, relais, attente), s'arrête en `waiting` au premier manquant, distingue hypothèse et décision. `POST /api/v1/flows/inspect-metadata` : MediaInfo sur une copie temporaire privée (512 Mio, 2 lectures simultanées, 2 min, 1 Mio de sortie ; 400/429/503). Profils/résultats restent dans la session de l'éditeur.

## Ressources réutilisables (toutes réconciliées entre serveurs)
- **Scripts** (section Librairies) : JS ou Python, brouillon à révision + versions immuables ; le flow **copie** le code et garde `scriptId@version` en provenance ; l'API ne les exécute pas. L'API REST et le MCP permettent aussi de supprimer une définition et ses versions ; les copies déjà intégrées aux flows restent indépendantes.
- **Modèles** ([ADR 0017](../adr/0017-parametres-de-configuration-des-modeles-de-flow.md)) : créés depuis une version publiée et modifiables en révisions optimistes. Chaque modèle garde une configuration guidée qui référence les blocs et champs configurables; les valeurs dans sa définition sont les valeurs par défaut. Les chemins, fichiers, secrets et références d’artefacts locaux ne peuvent pas être exposés et sont retirés du brouillon créé, puis renseignés dans l’éditeur. À la création d’un flow, seuls les champs autorisés peuvent être ajustés avec les options/validations du composant; le nouveau flow est une copie indépendante, donc les mises à jour du modèle n’altèrent pas les flows déjà créés.
- **Packs de composants** : fragment d'une version publiée (entrée = premier composant relié à `success`, sorties = sorties non utilisées en interne) ; inséré **aplati** dans le graphe avec métadonnées d'instance ; le runtime ne lit jamais la bibliothèque de packs.
- **Profils d'encodage** : un profil = une cible de qualité par encodeur (catalogue `VideoEncoderCatalog`), brouillon + versions immuables, profil intégré « Équilibré » en lecture seule ; le flow embarque l'instantané. Mode automatique : filtre par codec puis premier encodeur *vérifié utilisable* par priorité (NVENC, QSV, VAAPI, CPU). Les nombres de qualité ne sont pas comparables entre encodeurs.
- **Ressources** : l'API REST et le MCP listent, consultent, créent, renomment, versionnent et suppriment les ressources. Les outils MCP qui créent une ressource ou une version reçoivent des empreintes de fichiers déjà téléversés via l'API REST. Une ressource référencée par un flow ne peut pas être supprimée.

## Invariants
1. Le backend valide tout document de flow ; l'UI ne fait que guider.
2. Une version publiée ne change jamais ; une exécution référence une version précise et embarque sa définition.
3. Aucun mécanisme (script, plugin, IA, import) ne contourne scan, bail, réservation, validation de sortie.
4. Un flow historique reste exécutable : toute évolution de composant garde les références anciennes résolubles (`compatiblePluginVersions`).

## Non livré / limites
- Boucles, bloc de comparaison de qualité VMAF/PSNR/SSIM (proposition), import automatique de sauvegardes, rétention des sauvegardes, éditeur de profils de style ASS.
- Le preflight de la fiche n'atteste pas l'accès à une bibliothèque cible.

## Création guidée vidéo
- L'accueil et le catalogue proposent un assistant vidéo, en complément du flow vierge et des modèles réels, classés par type de média. Le catalogue standard fournit des modèles génériques vidéo, musique, image et sous-titres ainsi que des modèles d'actualisation Plex, Jellyfin, Radarr et Sonarr. Ils sont construits à partir des composants installés, validés au démarrage et enregistrés comme copies en lecture seule de versions publiées; les flows source sont masqués du catalogue normal. Un nouveau flow depuis un modèle reçoit son propre identifiant et s'ouvre comme brouillon.
- `POST /api/v1/flows/guided` génère et valide une définition vidéo ordinaire à références installées, sans écriture ni publication. L'UI l'enregistre en brouillon puis ouvre l'éditeur. Le flow vierge commence avec l'entrée spécialisée du type média choisi.
- MKV, HEVC/H.264/AV1, profil Équilibré embarqué, qualité adaptative, encodeur automatique ou CPU, conservation ou adaptation audio intégrée. Sous-titres conservés, HDR préservé ; secours CPU facultatif et désactivé par défaut. La politique de bibliothèque reste seule autorité de remplacement.
- Le graphe explicite préparation → réglages en plan → exécution groupée → publication. Revue complète, frontières et pistes différées : [revue du 24 octobre](../flow-review-2026-10-24.md).

## Builder FFmpeg explicite
L'assistant vidéo génère désormais **Début FFmpeg → réglages → Exécuter FFmpeg → publication** (base 1.10.0, compatible avec les références 1.9.0). Les anciens flows ne sont pas réécrits. La validation de publication vérifie chaque chemin : début obligatoire avant Executor, pas de portée imbriquée ou laissée ouverte, pas de script ni d'opération autonome dans la portée. Conditions, variables, lectures natives de métadonnées et blocs de nœud peuvent la traverser. Les brouillons restent enregistrables pendant la construction.

La simulation demande les métadonnées de sortie d'un Executor supposé réussi avant d'évaluer les conditions suivantes ; elle ne propage pas les métadonnées supposées en cas d'échec. **Assembler le résultat MKV** regroupe sous-titres/polices/noms après l'Executor. Sémantique média, secours et compatibilité : [runtime](runtime.md), ADR [0012](../adr/0012-builder-ffmpeg-et-assemblage.md).

### Contrôle d’intégrité facultatif (ADR 0027)
Le plugin `probe` 1.1.0 conserve les références 1.0.0 et ajoute `probe-check-integrity` (rapide/approfondi, automatique/CPU/GPU obligatoire). Seul ce bloc demande le décodage d’intégrité ; son absence laisse les scans structurels et vérifications d’empreinte obligatoires, sans décodage média complet, y compris pour les flows déjà publiés. Les flows guidés et modèles standards ne l’ajoutent pas par défaut ; il reste disponible dans le catalogue pour les flows qui le demandent explicitement. Les modèles système installés évoluent par une nouvelle version source, sans modifier les flows utilisateurs créés depuis ces modèles. Un flow avec ce bloc exige `media-integrity-policy/v1` sur tous ses segments. Le simulateur demande une hypothèse de résultat, sans lecture réelle. Détails et preuve par empreinte : [ADR 0027](../adr/0027-controle-integrite-optionnel.md).
