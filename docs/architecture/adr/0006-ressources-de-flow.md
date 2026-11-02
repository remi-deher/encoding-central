---
id: 0006
title: Ressources de flow : fichiers partagés du cluster (polices, images, audio)
status: accepted
domain: flows
date: 2026-10-22
decided_by: Rémi (2026-10-22 : nom « Ressources » et magasin adressé par empreinte ; portée et types par défaut ci-dessous)
supersedes: []
superseded_by: []
tags: [ressource, police, image, audio, catalogue, version, sha-256, distribution, éligibilité]
---

# 0006 — Ressources de flow : fichiers partagés du cluster

## Contexte
- Les polices d'un flow sont un dossier **local à chaque nœud** (`ENCODING_FONT_DIR`, un sous-dossier par jeu). Le worker l'annonce dans `inventory.fontSets` et l'éligibilité du bloc `subtitles-add-fonts` en dépend ([flows](../domains/flows.md), [runtime](../domains/runtime.md)).
- Constaté le 2026-10-22 sur les deux hôtes : sans jeu « Crunchyroll » sur un nœud, tout segment qui contient ce bloc n'est éligible nulle part et le flow reste en file ; la mise en place a demandé une copie manuelle par hôte, un montage de volume et un redéploiement de chaque application.
- Les fichiers binaires ne sont **jamais réconciliés** entre serveurs ([replication-storage](../domains/replication-storage.md)) ; seuls les scripts, modèles, profils et packs le sont, car ce sont des métadonnées.
- Rémi veut une catégorie de fichiers réutilisables (polices, images, audio…) dans la section Librairies et un sélecteur correspondant dans les blocs.

## Décision
1. **Une ressource n'est pas une bibliothèque média.** Une bibliothèque est un dossier surveillé qui déclenche des traitements ; une ressource est un fichier que des flows consomment. Les règles d'accès par nœud, de surveillance, de plafonds et de remplacement ne s'appliquent pas aux ressources. Section distincte dans « Librairies », à côté des scripts partagés.
2. **Modèle** : ressource = identifiant, type (`font`, `image`, `audio`, `other`), nom, description. Chaque ressource a des **versions immuables** (comme les scripts et profils) : numéro, nom de fichier, taille, type MIME détecté, **SHA-256**. Un flow référence `resourceId` + version (+ empreinte) ; il ne suit jamais « la dernière ». Le jeu de polices est une ressource de type `font` regroupant plusieurs fichiers.
3. **Distribution** : un worker ne lit plus un inventaire disque ; le serveur du nœud lui sert la version par empreinte, avec contrôle d'intégrité et reprise, par le même canal que les artefacts d'exécution. Copie dans l'espace temporaire de l'exécution, jamais de chemin libre. Le bloc ne dépend plus de `fontSets` pour une ressource référencée ; `fontSets` reste le repli pour un bloc qui nomme un jeu local.
4. **Blocs** : `subtitles-add-fonts` (et les blocs d'image ou d'audio à venir) acceptent une référence de ressource. Le champ est un sélecteur du catalogue, validé par le backend (existence, type, version).
5. **Sécurité** : envoi réservé au rôle `admin` ; liste blanche de types et d'extensions (`.ttf`, `.otf`, `.ttc`, images, audio usuels), taille maximale par fichier et par ressource, contenu vérifié par signature et non par extension, nom de fichier normalisé, aucun chemin issu de l'utilisateur.
6. **Compatibilité de versions** : un composant qui accepte une ressource est livré dans une version mineure du plugin ; un agent plus ancien ne peut pas réclamer un segment qui en contient (même mécanisme que `nodes-library-access`). Les références historiques (`fontSet`) restent résolubles ; les flows existants ne changent pas.

## Choix arrêtés
- **Nom** : « Ressources » (choisi par Rémi le 2026-10-22).
- **Stockage** : métadonnées réconciliées par le journal ; octets dans un **magasin adressé par empreinte (SHA-256)** sur chaque serveur, récupérés d'un pair à la demande et vérifiés à l'arrivée. L'invariant « binaires jamais réconciliés » est modifié explicitement pour ce seul magasin (l'implémentation met à jour [replication-storage](../domains/replication-storage.md)).
- **Portée** (par défaut, à rouvrir si besoin) : toutes les ressources sont partagées par tout le cluster ; pas de ressource propre à un flow.
- **Types livrés d'emblée** : polices seules. Images et audio s'ajoutent quand un bloc les consomme.

## Choix complémentaires (Rémi, 2026-10-22)
- **Tailles maximales** : 20 Mio par fichier, 100 Mio par ressource.
- **Version supprimée alors qu'un flow la référence** : suppression refusée ; le message indique le flow et le bloc qui la contiennent.
- **Jeu « Crunchyroll » déjà copié sur les nœuds** : le dossier local est supprimé ; Rémi importe les polices à la main dans le catalogue. Le repli `fontSet` reste pour les flows existants qui nomment un jeu local.
- **Rôles** : envoi et suppression pour `admin`, lecture pour `viewer`.
- **Licence** : aucun avertissement ni champ à l'envoi.
- **Récupération par un pair** : livrée dans une PR séparée (protocole à concevoir après lecture du maillage).

## Récupération d'un octet par un pair (conception proposée, à valider dans sa PR)
- L'échange de réconciliation existant ne convient pas : sa signature couvre le corps de la requête (≤ 16 Mio, `ClusterReconciliationSignature.MaximumBodyBytes`) et ses lots sont comptés en événements (32). Il faut un point d'accès dédié, authentifié par les mêmes identités de pairs admis : `GET /api/v1/cluster/resources/blobs/{sha256}`.
- Signature versionnée sur la méthode, le chemin, l'horodatage et l'identité du pair (pas sur un corps) ; mêmes en-têtes et même fenêtre d'horloge que la réconciliation, limiteur `cluster-mesh`, pairs admis et actifs uniquement, jamais d'accès anonyme.
- Réponse en flux avec `Range` (reprise). Le demandeur vérifie taille et SHA-256 **avant** de publier le fichier dans son magasin (écriture temporaire, puis déplacement sans écrasement).
- Qui possède l'octet n'est pas répliqué : le demandeur interroge les pairs connus, dans l'ordre, jusqu'à trouver l'empreinte (les adresses des pairs sont déjà mémorisées par la réconciliation).
- Préchargement : quand une version arrive par réconciliation, le serveur récupère l'octet en arrière-plan. Un worker qui le demande avant la fin déclenche la récupération et attend avec un délai borné ; l'éligibilité d'un segment ne reste jamais bloquée indéfiniment.
- Tests : empreinte falsifiée, flux tronqué, reprise, pair non admis, signature rejouée, perte d'un pair, deux demandes simultanées de la même empreinte.

## Déduplication des polices attachées (subtitles-add-fonts)

Constaté sur des médias réels (ex. *Overgeared* S01E01) : l'ajout d'un jeu de polices (comme la ressource « Crunchyroll ») peut dupliquer des polices déjà présentes dans le conteneur MKV sous une nomenclature légèrement différente (`Arial Bold.ttf` vs `ArialBold.ttf`, `Trebuchet MS Bold.ttf` vs `TrebuchetMSBold.otf`), augmentant inutilement la taille du média final (+1,7 Mo).

### Règles de déduplication retenues
1. **Priorité 1 : Analyse des métadonnées SFNT (`name` table)**.
   - Les fichiers candidats et les pièces jointes existantes du MKV sont inspectés en extrayant leur table `name` standard OpenType / TrueType (`0x00010000`, `'true'`, `'OTTO'`) ou TrueType Collections (`'ttcf'`).
   - Sont extraits les identifiants : `FontFamily` (nameID 1), `FontSubfamily` (nameID 2) et `FullFontName` (nameID 4) décodés en UTF-16BE (plateforme Unicode/Windows) ou Latin1 (Macintosh).
   - Les clés d'équivalence normalisées combinent nom complet, famille + style, et famille seule (pour les styles Regular/standard). Deux polices partageant au moins une clé de correspondance sont considérées équivalentes.
   - Les styles distincts (ex. *Bold* vs *Bold Italic*) génèrent des clés différentes et ne sont jamais confondus.
2. **Priorité 2 : Repli sur le nom de fichier normalisé**.
   - En l'absence de table `name` lisible ou en cas d'impossibilité d'extraction, la comparaison retombe sur le nom de fichier normalisé (extension supprimée, suppression de tous les caractères non alphanumériques : espaces, tirets, traits d'union, points, casse ignorée). Exemple : `Arial Bold.ttf` ≡ `ArialBold.otf` ≡ `arial-bold`.
3. **Garantie de robustesse (non-plantage)**.
   - Une police corrompue, tronquée ou au format non reconnu ne lève jamais d'exception et ne fait jamais échouer l'exécution du flow. L'inspecteur retombe immédiatement sur le descripteur dérivé du nom de fichier normalisé.
4. **Visibilité et traçabilité**.
   - Chaque police ajoutée est journalisée avec sa taille (`Police « Arial Bold.ttf » ajoutée (751104 octets).`).
   - Chaque police ignorée car déjà présente est consignée avec le nom de la pièce jointe existante correspondante (`Police « Arial Bold.ttf » ignorée : déjà présente (« ArialBold.ttf »).`).
   - Ces notes sont renvoyées dans le résultat de l'action (`outcome.Value`) et archivées dans les journaux d'exécution du runner, même lorsque toutes les polices étaient déjà présentes.

### Limites connues
- Polices aux tables de noms corrompues ou utilisant des encodages non conventionnels (hors Unicode/Macintosh) : retombent sur la comparaison par nom normalisé.
- Synonymes de style non déclarés comme sous-famille standard (ex. « SemiBold » comparé à « DemiBold » ou dénominations typographiques atypiques ne figurant pas dans la table de nom SFNT) : traités comme distincts si leurs noms normalisés divergent.

## Alternatives écartées
- **Octets dans l'entité réconciliée ou volume partagé** : le premier alourdit le journal, le second ajoute un prérequis de déploiement. Le lot du journal est borné en nombre d'événements (32), pas en octets : des fichiers de plusieurs mégaoctets alourdiraient chaque échange et chaque reprise.
- **Tout mettre dans une bibliothèque média** : mélange deux concepts (déclencheur de traitement et ressource consommée), impose des chemins locaux par nœud et expose la ressource à la surveillance et au remplacement.
- **Garder des dossiers locaux et automatiser la copie** (script ou outil d'administration) : ne résout ni la dérive entre nœuds, ni l'éligibilité, ni la traçabilité par version.
- **Embarquer les octets dans la définition du flow** : alourdit chaque version, duplique les fichiers, dépasse vite la limite de taille d'import et de document.

## Conséquences et risques
- Invariants touchés : « fichiers binaires jamais réconciliés » ([replication-storage](../domains/replication-storage.md)) ; toute nouvelle table décide « réconciliée » ou « locale » et inscrit les suppressions en tombstone.
- Compatibilité du cluster : versions mixtes gérées par la version mineure du plugin ; un nœud qui n'a pas encore la ressource la télécharge, il n'est donc plus exclu de l'éligibilité.
- Tests à écrire : empreinte et taille, fichier corrompu ou tronqué, reprise du téléchargement, ancien agent, version supprimée ou inconnue, perte d'un serveur pendant le transfert, non-contournement de la liste blanche, flow existant avec `fontSet`.
- Non vérifié : la limite réelle de taille d'un lot du journal de réconciliation, le coût de réplication de dizaines de mégaoctets, la cohérence avec les sauvegardes JSON de flows.
- Aucun code n'est livré par cette PR : décision et plan seulement.
