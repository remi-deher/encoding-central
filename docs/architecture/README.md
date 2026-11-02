# Architecture : mode d'emploi

Cette documentation est organisée pour qu'un humain ou un agent IA charge **seulement ce qui concerne sa tâche**. Lire dans cet ordre, et s'arrêter dès que le besoin est couvert :

1. [AGENTS.md](../../AGENTS.md) : principes transverses et règles absolues (toujours chargé, court).
2. **Ce fichier**, pour choisir la fiche de domaine.
3. **Une fiche de domaine** (`domains/*.md`, 60 à 120 lignes) : l'état courant, les invariants, les limites connues.
4. Au besoin, le code cité dans l'en-tête de la fiche ou un document d'exploitation (`docs/operations/`).

Les fiches décrivent ce qui est **vrai aujourd'hui sur la branche `dev`**. Le code reste l'autorité finale ; une fiche contredite par le code est un défaut à corriger, pas une raison de suivre la fiche.

## Quelle fiche lire ?

| Je travaille sur… | Fiche | Compléments |
| --- | --- | --- |
| Nœuds, rattachement, jumelage, groupes, modes serveur/worker, image Docker, GPU | [cluster](domains/cluster.md) | `docs/operations/release.md` |
| SQLite, migrations, réconciliation entre serveurs, sauvegardes de flows | [replication-storage](domains/replication-storage.md) | `docs/operations/backup-restore.md` |
| Définition de flow, validation, versions/brouillons, import/export, scripts, modèles, packs, profils d'encodage, simulation | [flows](domains/flows.md) | `BACKEND_RULES.md` |
| Exécution, baux, reprise, relais, attentes, verrous de disque, découverte et surveillance des bibliothèques, publication, remplacement de la source | [runtime](domains/runtime.md) | `TEST_RULES.md` |
| Plugins, composants, manifestes, Store, intégrations Plex/Sonarr/Radarr/Jellyfin, outils média, blocs audio/vidéo/sous-titres | [plugins](domains/plugins.md) | `backend/plugins/README.md`, `docs/operations/fileflows-v3-flow.md` |
| Authentification, rôles, jetons API, MCP, santé, métriques, journaux système, rétention | [auth-ops](domains/auth-ops.md) | `docs/operations/monitoring.md` |
| Fournisseurs IA, sessions, assistant, génération de flows | [ai](domains/ai.md) | `docs/operations/ai-providers.md` |
| Angular, composants communs, éditeur de flow, page Nœuds | [ui](domains/ui.md) | `UI_RULES.md`, `docs/design/` |

Un changement qui franchit plusieurs domaines (ex. nouvelle table partagée : stockage + domaine métier) lit les fiches concernées, pas toutes.

Avant de proposer ou de changer quoi que ce soit de structurant, consulter aussi [open-questions.md](open-questions.md) : écarts constatés et décisions en attente.

## Conventions

- **En-tête de chaque fiche** (YAML) : `domain`, `updated`, `legacy_adr` (anciens numéros d'ADR couverts), `code` (chemins de référence), `tags`. Un agent peut filtrer sur les tags et les chemins sans lire le corps.
- **Sections stables** : état courant, invariants, non livré / limites. Ce qui est « non livré » ou « non vérifié en conditions réelles » est écrit comme tel : ne jamais le présenter comme garanti.
- **Mise à jour** : toute PR qui change un comportement décrit met à jour la fiche du domaine dans la même PR (et la date `updated`). Pas de nouvelle fiche par fonctionnalité : on complète le domaine.
- **Décision structurante** (nouveau principe, changement d'un invariant, choix entre alternatives) : écrire un ADR court dans [adr/](adr/README.md), puis refléter son effet dans la fiche. Un ADR n'est jamais une documentation d'état : une fois appliqué, la fiche est la référence.
- **Statuts d'ADR** : `proposed`, `accepted`, `implemented`, `superseded` (avec `superseded_by`), `abandoned`. Aucun autre mot.
- Les décisions du propriétaire du projet ne sont pas modifiées sans son accord ; marquer le statut plutôt que réécrire.

## Anciens ADR

Les 83 ADR historiques (0001 à 0081, avec quelques numéros en double) ont été retirés de l'arbre : leur contenu vivant est dans les fiches, leur texte intégral reste dans l'historique Git (dernier état : commit `8da4ac5`, `git show 8da4ac5:docs/architecture/<fichier>`). Les commentaires du code qui citent « 0049 », « 0015 » etc. restent lisibles grâce à la table de correspondance [legacy-adr-map.md](legacy-adr-map.md).
