# AGENTS.md

Règles transverses du projet. Le détail par domaine est dans `docs/architecture/` : commencer par [docs/architecture/README.md](docs/architecture/README.md) (table « quelle fiche lire ») et ne charger que la fiche concernée. Écarts constatés et décisions en attente : [docs/architecture/open-questions.md](docs/architecture/open-questions.md).

## Objectif du projet

Application auto-hébergée pour gérer et distribuer le traitement de bibliothèques multimédias. Premier cas d'usage : encoder des bibliothèques Plex avec FFmpeg, ffprobe et MKVToolNix. Les intégrations Plex, Jellyfin et *ARR sont des plugins. Le fonctionnement en cluster et la haute disponibilité sont au cœur de la conception.

## Stack technique (validée)

- Backend : **C# ASP.NET Core sur .NET 10 LTS**. Interface : **Angular** (TypeScript, Angular Material/CDK). Déploiement : **Docker**.
- Base de données : **SQLite local par serveur uniquement**, réconciliée de proche en proche entre serveurs d'un cluster. Ne jamais partager un fichier SQLite entre hôtes. Voir [replication-storage](docs/architecture/domains/replication-storage.md).
- Ne pas remplacer ces choix sans décision explicite du propriétaire du projet.

## Modèle de nœud

- Le système est un cluster de nœuds configurables ; **aucun serveur central permanent**. Chaque installation héberge les fonctions serveur, worker, ou les deux (une seule image `encoding`, mode changeable à chaud).
- « Nœud » est le terme unique ; serveur et worker sont des rôles. Un nœud seul est **autonome** ; un cluster n'existe que s'il est créé ou rejoint, avec approbation du cluster d'accueil. Détails : [cluster](docs/architecture/domains/cluster.md).
- Les serveurs gardent chacun leur stockage et réconcilient par le maillage ; cohérence **éventuelle, sans quorum**. Les instances ne doivent pas créer d'états divergents ni attribuer simultanément une même tâche. Les baux et réservations sont locaux au serveur qui attribue.

## Bibliothèques et accès aux médias

- Une bibliothèque est une définition logique commune au cluster (règles, profils, paramètres). Les flows sont créés indépendamment puis attribués à une bibliothèque via une **version publiée précise** ; la découverte des fichiers est faite par un nœud en accès direct, sans saisie de chemin média par l'utilisateur.
- Chaque nœud déclare son accès à chaque bibliothèque : **accès direct** (chemin local ou montage), **non pris en charge**, ou **transfert** (un nœud à accès direct transfère la source par WebDAV au worker, qui traite une copie locale puis renvoie le résultat). Les outils travaillent sur des fichiers locaux, jamais directement sur un média distant. Aucune tâche n'est attribuée à un nœud sans accès adapté.

## Tâches et intégrité des fichiers

- Les tâches sont durables et observables ; baux et réservations transactionnels ; une seule tâche à la fois modifie un média.
- La perte d'un worker fait échouer sa tentative après expiration du bail ; **jamais de réattribution silencieuse** : reprise par nouvelle exécution demandée par un opérateur ou autorisée par une règle bornée du flow.
- Une sortie est écrite dans un emplacement temporaire, **validée**, puis publiée sans écraser la source ; la source est préservée jusqu'à validation et publication réussies. Le remplacement de la source n'existe que par la politique explicite de la bibliothèque (désactivée par défaut).
- Les transferts détectent les fichiers incomplets/corrompus ; les opérations sont idempotentes ou détectent la répétition.
- Les métadonnées d'origine sont capturées avant toute modification ; scan obligatoire avant la première opération qui en dépend ou transforme le média, scan de la sortie avant publication. Voir [flows](docs/architecture/domains/flows.md) et [runtime](docs/architecture/domains/runtime.md).
- **Ne jamais lancer de suppression, remplacement ou migration destructive sur des médias réels** sans protection explicite et validation.

## Flows, composants et plugins

- Les flows sont des graphes acycliques versionnés : une entrée, une connexion par sortie, chemins alternatifs **séquentiels** (jamais de parallélisme implicite). Le backend valide tout flow ; l'éditeur n'est pas une frontière de validation. Une exécution référence une version précise.
- Le catalogue de composants vient des plugins (manifestes déclaratifs versionnés, descriptions de chaque sortie et champ). Aucun plugin ne fournit de code d'interface ; aucun ne contourne attribution, accès bibliothèque, baux, scan, validation et publication.
- Les plugins tiers s'exécutent dans un **processus séparé sous Landlock** (pas de repli non confiné, pas de conteneur par plugin). La disponibilité d'un descripteur n'est pas la capacité d'un worker à l'exécuter. Voir [plugins](docs/architecture/domains/plugins.md).
- Par défaut un worker traite le fichier entier ; la cession à un autre worker n'existe que par le bloc explicite `nodes-handoff`.
- Distinguer dans le code composants de flow, composants UI Angular et nœuds de cluster.

## Outils média

- Adaptateurs organisés par fonction média (vidéo, image, audio…), derrière des interfaces remplaçables et testables. Les workers détectent et déclarent outils, versions, encodeurs vérifiés ; le répartiteur en tient compte.
- Les binaires tiers sont installés dans l'image Docker, jamais versionnés dans le dépôt.

## Authentification

Toute route de gestion exige une session ou un jeton API Bearer ; rôles `admin` et `viewer`. **Une nouvelle route est fermée par défaut** : ne l'ouvrir (`AllowAnonymous`, `AllowViewer`, `AllowNodeCredentials`) qu'avec une décision explicite et la mise à jour du test qui énumère les routes anonymes. Aucun secret dans les journaux, erreurs, exports ni réponses. Voir [auth-ops](docs/architecture/domains/auth-ops.md) (⚠ l'authentification est aujourd'hui optionnelle : `open-questions.md` point 1).

## Règles absolues de programmation

- **Interface** : [UI_RULES.md](UI_RULES.md) obligatoire, composants communs Angular Material/CDK, jetons de design, aucun contournement propre à une page ; en cas de doute métier, demander. Fiche : [ui](docs/architecture/domains/ui.md).
- **Backend** : [BACKEND_RULES.md](BACKEND_RULES.md) obligatoire ; réutiliser les capacités communes, pas d'implémentation parallèle ; toute évolution d'un contrat ou composant commun examine ses usages.
- **Tests et CI** : [TEST_RULES.md](TEST_RULES.md) obligatoire ; lint local et GitHub Actions ; E2E légers en local, scénarios lourds en CI.
- Toute nouvelle table est déclarée « réconciliée » ou « locale » ; les migrations sont additives, à numéro unique, jamais modifiées une fois appliquées.

## Consignes de travail

- Avant une décision structurante, consulter ce fichier, la fiche du domaine et les instructions plus proches des fichiers concernés.
- Les changements sont poussés directement sur `dev`, sans lancer la CI. La CI de validation s'exécute sur la PR de promotion `dev` vers `main` (et ses mises à jour) ; `main` n'est fusionnée qu'après réussite du contrôle `promotion-ci`, qui vérifie le commit exact et les domaines non modifiés. Vérifier compatibilité de cluster, scénarios d'échec et effets sur les modules existants.
- Décision structurante : ADR court dans `docs/architecture/adr/`. Changement de comportement : mettre à jour la fiche du domaine dans la même PR.
- Distinguer clairement vérifié, échoué et non exécuté ; ne jamais présenter une implémentation comme validée en conditions réelles sans l'avoir exercée (voir la liste « non vérifié » de `open-questions.md`).
