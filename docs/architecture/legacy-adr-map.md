# Correspondance des anciens ADR

Les ADR 0001 à 0081 ne sont plus dans l'arbre (texte intégral : `git show 8da4ac5:docs/architecture/<fichier>`). Cette table sert à résoudre les références du code (« architecture 0049 ») vers la fiche qui porte désormais le contenu vivant.

Statuts : **actuel** (repris dans la fiche), **partiel** (une partie remplacée), **remplacé**, **historique** (contexte seulement), **non implémenté**, **contradictoire** (entre en conflit avec une autre décision : voir [open-questions.md](open-questions.md)).

| Ancien ADR | Fiche(s) | Statut | Sujet |
| --- | --- | --- | --- |
| 0001 | [cluster](domains/cluster.md), [replication-storage](domains/replication-storage.md) | historique | Présence et premier contrat de heartbeat ; stockage remplacé par 0049 |
| 0002 | [cluster](domains/cluster.md), [auth-ops](domains/auth-ops.md) | partiel | Découverte mDNS et enrôlement valides ; console locale du worker remplacée par 0044/0048 |
| 0003 | [flows](domains/flows.md) | actuel | Bibliothèque sans type de média ; un flow par bibliothèque |
| 0004 | [flows](domains/flows.md), [replication-storage](domains/replication-storage.md) | actuel | Définition portable JSON, sauvegardes séparées |
| 0005 | [plugins](domains/plugins.md) | actuel | Catalogue éditorial des outils média (n'atteste pas l'installation) |
| 0006 | [flows](domains/flows.md) | actuel | Scan obligatoire, instantanés avant/après |
| 0007 | [flows](domains/flows.md) | actuel | Entrée unique, chemins alternatifs séquentiels |
| 0008 | [ui](domains/ui.md) | remplacé | Éditeur CDK/SVG, remplacé par 0010 puis 0077 |
| 0009 | [runtime](domains/runtime.md) | actuel | Registre durable des exécutions |
| 0010 | [ui](domains/ui.md) | partiel | Éditeur structuré ; contraintes de largeur remplacées par 0077 |
| 0011 | [plugins](domains/plugins.md), [ui](domains/ui.md) | actuel | Gabarit de bloc, schéma de paramètres, inline |
| 0012 | [flows](domains/flows.md), [runtime](domains/runtime.md) | historique | Plan de préparation du moteur, depuis livré |
| 0013 | [flows](domains/flows.md) | actuel | Catalogue installé et validateur backend |
| 0014 | [flows](domains/flows.md), [replication-storage](domains/replication-storage.md) | actuel | Versions immuables et sauvegardes JSON (SQL et jsonb d'origine : voir SQLite) |
| 0015 | [runtime](domains/runtime.md) | actuel | Runtime, baux, transferts, publication, Landlock |
| 0016 | [runtime](domains/runtime.md), [flows](domains/flows.md) | actuel | Découverte et lancement depuis la bibliothèque |
| 0017 | [cluster](domains/cluster.md) | actuel | Retrait d'un nœud, activité, codes 409/410 |
| 0018 | [runtime](domains/runtime.md) | actuel | Plafonds et surveillance |
| 0019 | [plugins](domains/plugins.md) | actuel | Config globale et dérogations par flow |
| 0020 | [runtime](domains/runtime.md), [plugins](domains/plugins.md) | actuel | Plugin Nœuds (contraintes) |
| 0021 | [runtime](domains/runtime.md) | actuel | Relais explicite entre workers |
| 0022 | [flows](domains/flows.md) | actuel | Conditions structurées |
| 0023 | [flows](domains/flows.md) | actuel | Testeur de flow |
| 0024 | [flows](domains/flows.md) | actuel | Bibliothèque de scripts |
| 0025 | [flows](domains/flows.md) | actuel | Modèles de flow |
| 0026 | [flows](domains/flows.md) | actuel | Packs de composants |
| 0027 | [flows](domains/flows.md) | actuel | Profils d'encodage |
| 0028 | [runtime](domains/runtime.md) | actuel | Journal des commandes du pipeline |
| 0029 | [auth-ops](domains/auth-ops.md), [replication-storage](domains/replication-storage.md) | actuel | Santé, métriques, rétention, migrations |
| 0030 | [runtime](domains/runtime.md) | actuel | Remplacement de la source |
| 0031 | [auth-ops](domains/auth-ops.md) | actuel | Auth, rôles, jumelage (⚠ défaut Auth:Enabled=false : open-questions) |
| 0032 | [plugins](domains/plugins.md) | actuel | Intégrations ; section Landlock/.NET mise à jour le 2026-10-21 |
| 0033 | [runtime](domains/runtime.md) | actuel | Attentes durables |
| 0034 | [cluster](domains/cluster.md) | actuel | Groupes et étiquettes |
| 0035 | [ui](domains/ui.md) | actuel | Jetons de design, thème, cartes |
| 0036 | [cluster](domains/cluster.md) | actuel | Image unique et mode à chaud |
| 0037 | [cluster](domains/cluster.md) | actuel | Nœud local enrôlé d'office |
| 0038 | [cluster](domains/cluster.md) | partiel | Modèle cluster ; point 1 remplacé par 0045 ; relations entre clusters (§9) non implémentées |
| 0039 | [cluster](domains/cluster.md) | actuel | Rejoindre et quitter |
| 0040 | [cluster](domains/cluster.md) | actuel | Deux phases, code de vérification |
| 0041 | [cluster](domains/cluster.md) | actuel | États de nœud, compatibilité, doublons |
| 0042 | — | jamais écrit | Numéro réservé au périmètre répliqué entre clusters |
| 0043 | [replication-storage](domains/replication-storage.md) | actuel | SQLite seul |
| 0044 | [cluster](domains/cluster.md), [ui](domains/ui.md) | partiel | Gestion depuis la page Nœuds ; panneau remplacé par 0048 |
| 0045 | [cluster](domains/cluster.md) | actuel | Nœud autonome et cluster explicite |
| 0046-nodes-page-redesign | [ui](domains/ui.md), [cluster](domains/cluster.md) | actuel | Refonte de la page Nœuds |
| 0046-pending-join | [cluster](domains/cluster.md) | actuel | Abandon d'un rattachement en attente |
| 0047 | [ui](domains/ui.md) | actuel | Boutons, tuiles, bandeau, tiroir |
| 0048 | [cluster](domains/cluster.md), [ui](domains/ui.md) | actuel | Onglets Ce nœud / Cluster |
| 0049 | [replication-storage](domains/replication-storage.md) | actuel | Maillage de réconciliation |
| 0050 | [cluster](domains/cluster.md), [ui](domains/ui.md) | actuel | Fiche Ce nœud, inventaire matériel |
| 0051 | [cluster](domains/cluster.md) | actuel | Encodage matériel dans l'image |
| 0052 | [ui](domains/ui.md), [cluster](domains/cluster.md) | remplacé | Colonnes matériel, remplacées par 0053 (cartes) |
| 0053 | [ui](domains/ui.md), [cluster](domains/cluster.md) | actuel | Cartes de membres, noms de GPU |
| 0054 | [runtime](domains/runtime.md) | actuel | Aide tools des scripts |
| 0055 | [plugins](domains/plugins.md) | actuel | Audio par règles |
| 0056 | [plugins](domains/plugins.md) | actuel | Noms de pistes |
| 0057 | [plugins](domains/plugins.md) | actuel | Sous-titres et polices |
| 0058 | [runtime](domains/runtime.md) | actuel | Verrou de disque |
| 0059 | [runtime](domains/runtime.md), [plugins](domains/plugins.md) | actuel | Plex : lecture, garde de publication |
| 0060-api-tokens | [auth-ops](domains/auth-ops.md) | actuel | Jetons API |
| 0060-peer-replicated | [replication-storage](domains/replication-storage.md) | contradictoire | Mode Raft optionnel (aperçu) : voir open-questions |
| 0061-mcp | [auth-ops](domains/auth-ops.md) | actuel | Serveur MCP |
| 0061-peer-sql | [replication-storage](domains/replication-storage.md) | contradictoire | Mode Raft optionnel (transactions SQL répliquées) |
| 0062 | [plugins](domains/plugins.md), [ai](domains/ai.md) | actuel | Guide de conception pour IA/MCP |
| 0063 | [plugins](domains/plugins.md) | actuel | Store de plugins (implémenté par la PR #205 ; réplication entre serveurs non faite) |
| 0064 | [plugins](domains/plugins.md) | non implémenté | Outils médias custom/managed |
| 0065 | [auth-ops](domains/auth-ops.md), [plugins](domains/plugins.md) | actuel | Outils MCP médias et validation |
| 0066 | [runtime](domains/runtime.md) | actuel | nodes-require-direct-access |
| 0067 | [plugins](domains/plugins.md) | actuel | Blocs de condition médias |
| 0068 | [plugins](domains/plugins.md) | actuel | HDR et profondeur |
| 0069 | [plugins](domains/plugins.md) | actuel | Transcodage adaptatif |
| 0070 | [plugins](domains/plugins.md) | actuel | Blocs inspirés de FileFlows |
| 0071 | [ai](domains/ai.md) | actuel | CLI IA, sessions, reprise |
| 0072 | [ai](domains/ai.md) | actuel | Générateur de flows |
| 0073 | [auth-ops](domains/auth-ops.md) | actuel | Journaux système |
| 0074 | [ai](domains/ai.md) | actuel | Catalogue de modèles |
| 0075 | [runtime](domains/runtime.md), [plugins](domains/plugins.md) | actuel | Repli CPU, validation de sortie, media.published |
| 0076 | [plugins](domains/plugins.md) | actuel | Classement des blocs vidéo |
| 0077 | [ui](domains/ui.md) | actuel | Guidé / Canvas |
| 0078 | [ui](domains/ui.md) | actuel | Blocs compacts |
| 0079 | [ui](domains/ui.md), [plugins](domains/plugins.md) | actuel | Configuration des composants |
| 0080 | [flows](domains/flows.md), [ui](domains/ui.md) | actuel | Fiche d'un flow |
| 0081 | [flows](domains/flows.md) | actuel | Suppression avec détachement |
