---
domain: ai
updated: 2026-10-27
legacy_adr: [0062, 0065, 0071, 0072, 0074, 0077]
code:
  - backend/src/Encoding.Api/Tools/
  - backend/src/Encoding.Api/Flows/FlowAi*.cs
  - backend/src/Encoding.Api/Flows/FlowCatalogPromptBuilder.cs
  - backend/src/Encoding.Core/Runtime/
  - frontend/src/app/features/ai/
tags: [ia, codex, claude, ollama, session, checkpoint, assistant, générateur de flows, quotas, modèles]
---

# IA : fournisseurs, sessions, assistant et génération de flows

## Fournisseurs et connexion
- CLI officielles lancées par le serveur : **Codex** (`codex exec --json --ephemeral`, connexion `codex login --device-auth`) et **Claude** (`claude -p --output-format stream-json …`, jeton `claude setup-token` protégé par Data Protection et passé par `CLAUDE_CODE_OAUTH_TOKEN`), plus API compatibles OpenAI/Ollama (non configurées sur une nouvelle installation). Le lien/code de connexion émis par la CLI reste en mémoire (15 min max), jamais journalisé ni persisté ; le succès n'est confirmé qu'après `codex login status` / `claude auth status`, même si le processus interactif est encore en attente de fermeture.
- Profils par fournisseur (`CODEX_HOME`, `CLAUDE_CONFIG_DIR`) sous `AiTools:CredentialsDirectory` (défaut `Node:DataDirectory/credentials/ai-tools`) : copiés localement, mais **répliqués chiffrés entre nœuds** (Codex `auth.json`, jeton Claude, clé OpenAI) par `AiCredentialSyncService` et la table `ai_credentials` ([ADR 0001](../adr/0001-replication-connexions-ia.md)) ; la modification la plus récente l’emporte et une déconnexion se propage. CLI installées sous `AiTools:RuntimeDirectory` (`/opt/encoding/ai-cli`).
- CLI **confinées par Landlock** (code partagé dans `Encoding.Core.Runtime`) ; sans Landlock elles refusent de démarrer. Claude sans outils/MCP/skills/réglages hérités ; Codex en lecture seule sans approbation interactive. Aucune réponse simulée si la CLI est absente ; plus de flow synthétique en cas d'échec.
- La page « IA & fournisseurs » distingue moteur par défaut et fiche sélectionnée ; les secrets restent masqués ; `hasOpenAiApiKey` seulement.
- Les connexions des CLI et leur réplication sont relues en arrière-plan toutes les 15 s ; quotas toutes les 60 s et historique des sessions toutes les 30 s. La fenêtre d’association vérifie son état automatiquement.
- Fermer ou recharger l’interface n’annule pas le processus d’association, qui continue côté serveur jusqu’à sa confirmation ou son expiration (15 min maximum).

## Accès depuis tous les nœuds
- Les autres serveurs routent vers un serveur qui héberge la connexion via `/api/v1/cluster/ai/exchange` (requêtes signées et chiffrées AES-GCM avec les identités admises du maillage ; aucune opération d'ajout/récupération de secrets). Les workers passent par `/api/v1/runtime/nodes/{nodeId}/ai/generate` (jeton de nœud). Héberger une connexion CLI exige la fonction serveur sur ce nœud.

## Sessions, contexte, reprise
- Le contexte appartient à Encoding (consignes, demandes identifiées, tours validés), **chiffré et écrit dans des registres réconciliés avant de confirmer** ; checkpoint ≤ 512 Kio ; les CLI n'ont pas de conversation native persistante.
- Chaque session a un **coordinateur stable** (`peerId.sessionId`) qui sérialise les tours et l'annulation ; une réponse déjà validée pour un `requestId` est rejouée sans inférence ; un `requestId` réutilisé avec un autre prompt est refusé. Perte d'un exécuteur : jusqu'à 3 nœuds essayés après expiration de la tentative précédente. **Perte du coordinateur : aucune reprise silencieuse** (une partition ne prouve pas sa disparition) ; l'admin crée une session depuis le dernier checkpoint (`/api/v1/tools/ai/sessions/{id}/fork`). Pas de garantie « exactement une fois » de facturation côté fournisseur.
- Périmètre d'une session (opération, bibliothèque, flow, média, exécution) immuable ; modèle fixe sauf l'assistant, qui peut changer fournisseur/modèle/effort **entre deux messages**.

- **Titre de conversation** : créé à partir du début du premier message (`titleSource: fallback`), puis remplacé après la première réponse réussie par un titre court généré par le fournisseur de la conversation (`ai`, 20 s max, session masquée dédiée, deux tentatives au plus sur les deux premiers tours). Tout échec (fournisseur absent ou en erreur, délai, sortie vide) garde le titre de repli sans faire échouer le tour. Renommage manuel : `PUT /api/v1/tools/ai/assistant/conversations/{id}/title` (`user`, jamais écrasé ensuite, même par un tour en cours). Le titre est lu à la volée pour la liste des conversations et pour les demandes de la page Activité (`conversationTitle`) ; aucune migration SQL (registres existants).

## Catalogue de modèles et quotas
- Aucun catalogue de modèles côté frontend : chaque serveur interroge ses fournisseurs (Codex `app-server model/list`, Claude contrôle stream-json, `GET /models` pour les API compatibles), met en cache (états 1 min, catalogues 5 min, instantané servi pendant le rafraîchissement, `loading`/`stale`) et expose via le transport signé. Capacités inconnues = inconnues, jamais déduites du nom. Désactivation par couple fournisseur/modèle (registre chiffré réconcilié) ; `PUT /api/v1/tools/ai/models/policy`, `…/settings`, `GET …/assistant/options`.
- Quotas des comptes CLI : sonde locale bornée, observation en mémoire par connexion, jamais sommée entre nœuds ; la lecture Claude est expérimentale et peut être indisponible.
- Détails d'exploitation : `docs/operations/ai-providers.md`, `ai-metadata-enrichment.md`.

## Assistant et générateur de flows
- `FlowCatalogPromptBuilder` injecte un catalogue compact (ids, sorties, champs) filtré par type de média ; boucle **générer → valider (`FlowDefinitionValidator`) → auto-réparer** bornée à 2 tentatives, sinon 422 avec motifs. `POST /api/v1/flows/ai/{generate|refactor|explain}`. **Ne publie ni ne persiste rien** : un brouillon validé à relire.
- Interface : panneau d'assistant unique (widget et menus d'étape), différences de nœuds/connexions, simulation, **Appliquer / Rejeter** ; une proposition obsolète est refusée ([ui.md](ui.md)).
- Recherche des conversations : `GET /api/v1/tools/ai/assistant/conversations/search?q=…` accepte les requêtes de 2 à 120 caractères, parcourt les 100 conversations les plus récentes du propriétaire authentifié, leurs titres et tours, puis renvoie au plus huit correspondances avec un extrait. La recherche globale ouvre la conversation et, pour une correspondance dans un message, défile jusqu’au tour correspondant.
- Pour que l'IA se branche correctement, les plugins décrivent sorties et champs ([plugins.md](plugins.md)) ; MCP : [auth-ops.md](auth-ops.md).

## Invariants
1. Aucune inférence simulée ; les secrets IA ne transitent que chiffrés (journal du maillage) et ne sont jamais journalisés.
2. Pas de repli non confiné pour lancer une CLI.
3. Pas de reprise silencieuse d'un coordinateur de session.
4. Toute sortie IA passe par la validation backend avant d'atteindre l'éditeur.
