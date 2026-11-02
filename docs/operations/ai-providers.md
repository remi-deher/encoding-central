# Fournisseurs IA dans le cluster

## Installation

L'image embarque Codex CLI `0.163.0` et Claude Code `2.1.302`. Les arguments de construction `CODEX_CLI_VERSION` et `CLAUDE_CODE_VERSION` permettent une mise à jour explicite. L'installation utilise les paquets officiels npm ; aucun binaire tiers n'est versionné dans le dépôt.

Conserver le volume `Node:DataDirectory`, notamment les clés Data Protection et `credentials/ai-tools`. Ne pas monter le même profil de credentials en écriture sur plusieurs serveurs : la réplication entre nœuds s’en charge. Tous les serveurs doivent être approuvés dans le même cluster et joignables par les adresses du maillage. Les horloges doivent être synchronisées. Le [maillage de réconciliation](../architecture/domains/replication-storage.md) est le chemin vérifié.

Ouvrir **Paramètres → Intégrations → IA & fournisseurs** sur le serveur qui doit héberger une connexion :

- Codex : **Associer mon compte**, ouvrir le lien fourni par la CLI et valider son code. Le compte doit autoriser ce mode de connexion.
- Claude : **Se connecter**, ouvrir le lien fourni par `claude auth login` dans votre navigateur et terminer la connexion. Si la CLI demande un code de retour, le coller dans le dialogue pour le transmettre au nœud. Le contrôle de statut vérifie l’état local de la CLI, pas une requête au modèle.
- Ollama ou API compatible : enregistrer l'adresse et le modèle sur ce serveur. La clé d'API est protégée localement ; laisser le champ vide la conserve.

Choisir le moteur par défaut puis enregistrer. Les nœuds répondants et leurs fournisseurs sont listés sous la configuration. Les connexions déclarées ne garantissent ni le quota disponible ni la réponse du modèle.

Dans **Usages et modèles**, l’assistant et les opérations sur les flows peuvent hériter du moteur par défaut ou choisir un fournisseur distinct. Les opérations sur les flows utilisent le modèle configuré pour leur connexion ; l’assistant choisit modèle et effort par tour dans le catalogue observé du backend ([ai](../architecture/domains/ai.md)). La règle **local uniquement** impose Ollama sur une adresse de boucle locale et interdit le routage vers un autre nœud ou fournisseur. Sans cette règle, les questions et ressources de l’assistant peuvent transmettre des définitions de flows, des métadonnées de nœuds et bibliothèques, ainsi que des événements et journaux d’exécution pouvant contenir des chemins. Les sous-titres ne sont pas joints comme source dédiée par ce parcours. Vérifiez le fournisseur effectif avant d’y joindre des données sensibles.

L’utilisateur peut sélectionner jusqu’à trois outils MCP de lecture : liste des flows, détail d’un flow, nœuds, exécutions récentes et vue des bibliothèques. Le modèle peut aussi demander un seul outil supplémentaire dans un format structuré. Le backend accepte uniquement cette liste blanche, valide les arguments et réutilise les handlers MCP en lecture seule. Une réponse de préparation invalide ne déclenche aucun outil. La préparation puis la réponse finale peuvent chacune consommer un appel fournisseur. La taille du contexte reste bornée. Une génération ou modification de flow reste une proposition : **Appliquer à l’éditeur** et **Rejeter** enregistrent la décision dans l’activité. Les administrateurs peuvent publier le brouillon attaché, supprimer un flow attaché, demander l’annulation d’une exécution attachée ou mettre en file les fichiers actuellement détectés d’une bibliothèque attachée. Chaque action est préparée par le serveur et confirmée dans l’interface avec un résumé signé, expirant après cinq minutes. Publication vérifie la révision du brouillon ; suppression et annulation vérifient la version/révision métier ; le lancement vérifie l’affectation et l’empreinte de la liste de fichiers. Les identifiants d’exécution déterministes rendent le lancement confirmé sans doublon lors d’une reprise.

Sur Linux, Landlock doit être disponible. Le lancement refuse explicitement de s'exécuter sans confinement. Pour une installation CLI hors image, configurer au besoin `AiTools:RuntimeDirectory` pour accorder en lecture le répertoire d'installation ; la fonction serveur doit pouvoir exécuter l'outil. Les binaires peuvent être choisis par `AiTools:Executables:codex-cli` et `AiTools:Executables:claude-cli`.

## Assistant dans l’interface

Le lanceur rond reste ancré à la fenêtre du navigateur, en dehors du conteneur Material qui défile. Il est disponible sur les pages authentifiées et dans l’éditeur de flow, hors page Assistant déjà ouverte. Il peut être déplacé par glisser-déposer ou avec Alt + flèches ; sa position est conservée et ramenée dans la fenêtre après redimensionnement.

La petite fenêtre de 380 × 520 px utilise le même compositeur que la page Assistant. La réduire conserve messages et brouillon ; elle ne modifie pas l’adresse de la page consultée. Le bouton d’agrandissement ouvre la conversation dans Assistant. La page complète dispose d’un historique latéral (tiroir sur mobile), d’un fil défilant et d’une saisie toujours accessible. Le modèle confirmé et le raisonnement demandé d’un ancien tour sont consultables dans « Modèle et raisonnement ».

Les contrôles sont séparés : **Type → Modèle → Effort**. Les types proposés sont déduits des fournisseurs présents dans les options du backend ; le libellé ChatGPT désigne la connexion Codex, Claude la connexion Claude Code, OpenAI · API la connexion HTTP compatible. Aucun type non déclaré, modèle désactivé ni effort supposé n’est ajouté par le frontend. Choisir un type filtre les modèles ; choisir un modèle remplace les efforts proposés par ses seules capacités déclarées. Si le fournisseur ne les communique pas, le contrôle indique « Inconnu ». Le choix est verrouillé pendant une réponse et sa reprise.

Le bouton **+** joint un flow publié, un nœud, une bibliothèque ou une exécution. Une première ressource reste nécessaire pour démarrer un échange. Les ressources jointes donnent accès à leur menu d’actions existantes ; les actions sensibles gardent leurs confirmations serveur. Le menu **…** donne accès aux outils de lecture, à l’actualisation du catalogue et à une nouvelle conversation. Entrée envoie le message, Maj + Entrée ajoute une ligne ; les saisies en cours de composition (IME) ne déclenchent pas l’envoi.

## Demande et reprise

L'API administrateur `POST /api/v1/tools/ai/generate` accepte :

```json
{
  "provider": "codex-cli",
  "prompt": {
    "systemPrompt": "Réponds en français.",
    "userPrompt": "Résume ce texte…",
    "requestId": "11111111-1111-4111-8111-111111111111"
  }
}
```

La réponse contient `sessionId` et `requestId`, y compris quand aucun exécuteur n'est disponible. Conserver les deux pour réessayer exactement la même demande. Pour le tour suivant, garder le `sessionId` et choisir un nouveau `requestId`. Les consignes système et le fournisseur restent fixes dans une session. Une première requête avec le même `requestId` sur le même coordinateur retrouve la même session ; si aucun premier accusé n'est reçu, éviter de lancer simultanément cette requête sur plusieurs coordinateurs.

`GET /api/v1/tools/ai/sessions/{id}` lit le checkpoint présent sur ce serveur. S'il n'est pas encore réconcilié, la réponse est 404. Si le coordinateur est perdu, `POST /api/v1/tools/ai/sessions/{id}/fork` crée une nouvelle session avec les tours validés reçus. Le client doit conserver le nouvel identifiant. Les tours non confirmés ne sont pas copiés. L'assistant de flows présente cette reprise après un échec.

**Reprise automatique par un autre nœud.** Quand une demande vise une session dont le coordinateur ne répond à aucune sonde, le nœud qui la reçoit en devient le coordinateur (registre répliqué `session-coordinator/{id}`, le plus récent gagne) et poursuit la conversation avec ses tours validés : l'identifiant de session et de conversation ne change pas, l'assistant et les flows continuent donc sans action. Les autres nœuds, y compris l'ancien coordinateur à son retour, redirigent ensuite vers le nouveau. Garde-fous : aucune reprise si le coordinateur a répondu à la sonde (une demande acceptée reste ambiguë), ni si le checkpoint validé n'est pas encore présent sur ce nœud (jamais de contexte vide), ni pendant le bail d'une tentative en cours. Pour l'assistant, la reprise se fait dans `POST /api/v1/tools/ai/assistant/messages` : si le nœud coordinateur de la conversation ne répond à aucune sonde et que l'historique validé est présent, le nœud qui reçoit le message traite la conversation lui-même (les sessions auxiliaires de titre et de planification suivent le coordinateur courant). L'annulation d'une demande suit le coordinateur courant. Chaque conversation affiche « Discussion du nœud X » (liste et en-tête), avec « injoignable » quand ce nœud ne répond plus ; `coordinatorId` est renseigné à la lecture, jamais stocké. Les jetons de confirmation d'une action déjà préparée restent liés à l'ancien coordinateur : après une reprise, préparer l'action à nouveau. Limite connue : pendant une partition réseau où l'ancien coordinateur reste actif de son côté, les deux côtés peuvent écrire ; à la réconciliation la version la plus récente de la session l'emporte et les tours de l'autre branche ne sont pas fusionnés.

Une tentative est bornée par le délai configuré, entre 10 et 600 secondes. Un remplacement ambigu attend le délai précédent, avec une marge de cinq secondes ; au maximum trois exécuteurs sont essayés par appel. Si le coordinateur redémarre, attendre l'expiration du bail affiché avant de réessayer. Un échec peut avoir consommé du quota chez le fournisseur.

Les marqueurs locaux d'essai ne sont pas supprimés automatiquement. Les checkpoints transitent chiffrés dans le journal du maillage ; les connexions IA (Codex, Claude, clé OpenAI) y transitent aussi chiffrées : une connexion faite sur un nœud est reprise par les autres en quelques dizaines de secondes, y compris ses renouvellements et sa déconnexion ([ADR 0001](../architecture/adr/0001-replication-connexions-ia.md)). Si deux nœuds renouvellent Codex en même temps, reconnecter un nœud suffit.

Les conversations terminées et l’activité IA expirent automatiquement. Les valeurs par défaut sont 90 jours pour les conversations (instantanés de contexte compris) et 30 jours pour l’activité. Les administrateurs peuvent régler ces durées dans **Paramètres → IA → Paramètres avancés du cluster** ou via `AiTools:AssistantConversationRetentionDays` et `AiTools:AssistantActivityRetentionDays` (1 à 3 650 jours) au démarrage. Le réglage enregistré est répliqué au cluster. Chaque nœud publie un tombstone dans le registre répliqué ; la passe s’exécute toutes les heures et ignore une conversation qui possède encore une demande en attente.

Après 24 heures, un nœud purge les anciennes versions chiffrées de son journal local seulement quand tous les pairs connus ont confirmé la séquence du tombstone. Un pair inaccessible retarde cette compaction, afin qu’il puisse d’abord recevoir la suppression. Une sauvegarde antérieure de la base ou un pair retiré hors du protocole de cluster peut toujours contenir l’ancien ciphertext : cette rétention ne constitue pas une purge cryptographique des sauvegardes.

L’interruption d’une demande arrête l’attente locale lorsqu’elle est contrôlable ; un fournisseur distant peut avoir déjà accepté le travail. L’activité indique alors que le résultat distant reste incertain et la reprise attend l’expiration du bail.

Le corpus de scénarios de référence se trouve dans docs/testing/ai-assistant-eval-cases.json. Il couvre l’ancrage aux sources, les injections dans les descriptions et journaux, les appels et arguments d’outils, les conflits de version, l’annulation et l’incertitude sur les coûts. Il sert de base à l’évaluation des fournisseurs ; la suite automatisée vérifie actuellement sa structure et sa couverture, pas la qualité sémantique des réponses d’un modèle.

## Vérifications de la branche

- Tests .NET des commandes via exécutables contrôlés, statuts, absence de simulation, protection des secrets et refus sans Landlock.
- Deux bases SQLite indépendantes : admission signée, routage vers un fournisseur distant, continuation depuis un autre serveur, idempotence, redémarrage, expiration de bail et réconciliation de contexte chiffré.
- Tests Angular de la page et de la fenêtre d'association ; Playwright sur ordinateur et mobile, thèmes clair/sombre et audit axe WCAG AA.

Aucun compte personnel n'est associé par les tests. L'environnement cloud de développement ne fournit pas Landlock : les scénarios d'authentification utilisent un lanceur de test contrôlé ; un test distinct vérifie que le lanceur de production refuse ce cas. L'exécution réelle confinée des deux CLI et la construction complète de l'image restent à vérifier sur un hôte Linux compatible avant déploiement. Les capacités de traitement IA des blocs média au-delà de l'API de génération sont hors de cette modification.

### Diagnostiquer une connexion CLI

Dans **Paramètres → Système & Apparence → Journaux système**, filtrer la catégorie
`Encoding.Api.Tools.DeviceCodeAuthService` sur le nœud où la connexion a été démarrée.
Les tentatives Codex possèdent un identifiant de corrélation et indiquent le démarrage,
la durée, le code de sortie, les tailles stdout/stderr et la reconnaissance de l’URL
et du code. Les sorties brutes, codes de connexion et jetons ne sont pas journalisés.
Les avertissements et erreurs restent dans l’historique local persistant.

Le délai d’attente du premier code reste de 30 secondes. L’interface distingue une
CLI silencieuse, une sortie sans invitation reconnue et une sortie prématurée du
processus. Une sortie sans code avec un statut 0 ne valide pas une connexion.
Pour Claude, un contrôle local qui échoue ou dépasse 10 secondes est affiché
séparément d’un jeton non reconnu. Le contrôle local ne prouve pas l’accès au modèle
ni la disponibilité du quota chez le fournisseur.

Le lecteur de journaux accepte les réponses HTTP historiques sous forme de tableau
ainsi que l’enveloppe `entries` pour les mises à jour progressives des nœuds. Le flux
SSE écoute les événements nommés `log`, se reconnecte après interruption et déduplique
les événements rejoués. Les journaux restent propres au nœud consulté.

## Sessions et historique applicatif

Les inférences utilisent désormais `claude -p --output-format stream-json --verbose --no-session-persistence` et `codex exec --json --ephemeral`. Les autres restrictions d’outils et de confinement sont conservées. Une version CLI ne reconnaissant pas ces options échoue explicitement ; aucun repli ne réactive la persistance native. Ces options désactivent l’historique local natif, sans définir la politique de conservation du fournisseur.

Encoding conserve son propre contexte chiffré, reprend exclusivement par identifiants de session et de requête, et ne crée ni ne classe de conversations dans les Projets web de Claude.ai ou ChatGPT. Une reprise peut changer de nœud d’inférence sans déplacer les identifiants CLI. Le modèle demandé et le périmètre applicatif sont immuables dans une session.

Dans **Paramètres → IA & fournisseurs → Sessions et traitements IA**, un administrateur peut charger les checkpoints connus du nœud, paginés par 50, puis consulter les consignes et échanges d’une session. L’actualisation est explicite et la vue indique que les données des pairs sont éventuellement cohérentes. Un bail actif n’est pas une preuve que le modèle génère encore du texte ; son expiration laisse la demande en attente de reprise, jamais terminée artificiellement.

API administrateur : `GET /api/v1/tools/ai/sessions?offset=0` retourne les résumés sans prompts ; `GET /api/v1/tools/ai/sessions/{id}` fournit le checkpoint complet. Le champ optionnel `scope` d’un `AiPrompt` associe une opération (`operation`, 80 caractères maximum) à `libraryId`, `flowId`, `mediaId` et/ou `executionId`. Ce classement n’ajoute aucun contexte partagé ni accès au système de fichiers. Les appels de création, modification et explication de flow renseignent leur opération et le flow existant. Un changement de périmètre exige une nouvelle session.

Les compteurs d’entrée, sortie et cache proviennent des événements structurés du fournisseur. Le coût estimé Claude est enregistré lorsqu’il est fourni. Une réponse n’est acceptée qu’après l’événement terminal de succès, avec un contenu final non vide ; les sorties partielles, erreurs et diagnostics ne deviennent pas une réponse. Les consommations des tours validés sont conservées dans le checkpoint et restituées à l’identique lors d’un rejeu, sans nouvelle inférence. Les sommes incomplètes restent indisponibles. Les tentatives échouées ou interrompues peuvent être facturées sans figurer dans ce total.

La consommation des tours est distincte des quotas d’abonnement, présentés dans le panneau dédié décrit ci-dessous. Aucun quota ou coût Codex n’est extrapolé à partir des jetons consommés. La traduction automatique de sous-titres demeure un composant déclaré au catalogue ; cet historique ne constitue pas son implémentation runtime.


## Quotas des abonnements

Le panneau administrateur **Quotas des abonnements** lit les limites réelles des comptes CLI de chaque serveur. `GET /api/v1/tools/ai/quotas` retourne les observations par nœud ; les pairs sont consultés avec l’opération `quotas` du transport IA signé et chiffré. Aucun identifiant CLI, courriel de compte ou secret n’est renvoyé. Les quotas sont ceux du compte fournisseur, incluant son utilisation sur d’autres appareils ; plusieurs nœuds peuvent employer le même compte et leurs valeurs ne doivent jamais être additionnées.

- **Codex** : lancement borné de `codex app-server`, handshake `initialize` / `initialized`, puis `account/rateLimits/read`. Aucun thread, prompt ou tour de modèle n’est créé. Toutes les fenêtres `primary` / `secondary` des compartiments retournés sont conservées (jusqu’à 32 compartiments), avec pourcentage et réinitialisation lorsqu’ils sont fournis.
- **Claude** : adaptateur expérimental du protocole de contrôle du SDK publié `@anthropic-ai/claude-agent-sdk` 0.3.302, compatible avec la CLI épinglée 2.1.302. Une session `-p --input-format stream-json --output-format stream-json --no-session-persistence` reçoit uniquement `initialize`, puis `get_usage` avec `skip_behaviors: true`. Ce dernier correspond à `usage_EXPERIMENTAL_MAY_CHANGE_DO_NOT_RELY_ON_THIS_API_YET()` ; il ne génère pas de réponse du modèle et ne parcourt pas les transcriptions pour attribuer les usages. Les fenêtres 5 h, 7 jours, Opus/Sonnet, OAuth et par modèle sont affichées lorsqu’elles existent. Les achats de crédits et l’activation d’usage supplémentaire ne sont jamais demandés.

La connexion par `setup-token` n’assure pas les droits de lecture du profil : Claude peut retourner `rate_limits_available: false`, notamment si le scope de profil manque. Les connexions par clé API, les comptes sans limite d’abonnement applicable, les versions incompatibles et les réponses sans pourcentage exploitable restent **Indisponibles**. Aucune connexion web de substitution ni API privée avec extraction de jeton n’est utilisée.

Le lancement réutilise le profil et le confinement Landlock communs aux inférences. Chaque lecture est plafonnée à 15 secondes, avec sortie totale bornée à 1 Mio et trame à 256 Kio. Aucun prompt utilisateur n’est envoyé. Les fenêtres sont validées (pourcentages entre 0 et 100, dates valides), sans conversion en nombre de jetons disponibles.

Le cache local et l’actualisation de l’interface sont d’une minute. Une lecture échouée conserve la dernière observation dans le même profil, marquée **Donnée ancienne**, et la jauge actuelle disparaît. Une fenêtre expirée n’est jamais remise artificiellement à 100 %. Un nœud devenu injoignable reste visible dans la vue ouverte avec ses dernières observations marquées anciennes. Les changements de connexion invalident le cache local ; les observations ne sont pas répliquées ni conservées après redémarrage.

Les alertes visuelles apparaissent à 20 % restants et deviennent critiques à 5 %. Elles ne déclenchent aucune notification externe, aucun achat ni aucune pause automatique de flow. Les quotas sont informatifs et n’autorisent pas l’application à dépasser une limite fournisseur.

### Réplication par nœud et consommation de tokens

Le panneau **Réplication des connexions** (Paramètres → IA) indique, pour Codex, Claude Code et la clé OpenAI, l'état de chaque nœud : **à jour**, **en attente** (version plus récente en cours de réception ou synchronisation en échec), **à reconnecter**, **aucune connexion**, **injoignable** ou **état inconnu** (nœud d'une version antérieure qui ne déclare pas cet état). L'état vient de chaque nœud (`credentials` et `reconnect` dans `GET /api/v1/tools/ai/cluster`) : révision de la connexion appliquée et date de dernière application, jamais de secret ni de condensat. « À reconnecter » signifie que le cluster détient la connexion mais que la CLI de ce nœud se déclare non connectée. Ollama reste local à chaque nœud et n'a pas d'état de réplication. Un nœud injoignable n'est jamais présenté comme en erreur : son état est inconnu.

`GET /api/v1/tools/ai/usage?days=30` (administrateur, 1 à 365 jours) agrège les tokens rapportés par les fournisseurs sur les sessions répliquées (assistant et flows), par nœud ayant exécuté la demande puis par fournisseur, avec un total global. Les échanges sans consommation rapportée sont comptés à part et jamais estimés ; les anciens échanges sans nœud exécutant apparaissent sous « Nœud non enregistré ». Au plus 5 000 sessions récentes sont lues (`truncated` l'indique). La conservation suit celle des conversations (90 jours par défaut) : une période plus longue ne remonte pas plus loin.

Le tiroir d'un fournisseur (**Configurer**) affiche la réplication par nœud de ce seul fournisseur. **Déconnecter le cluster** demande confirmation : la suppression est publiée au cluster, y compris pour les nœuds injoignables, qui la reçoivent à leur retour.

### Jetons API et MCP : propagation par nœud

`GET /api/v1/tokens/replication` (administrateur) demande à chaque nœud la liste des jetons qu'il connaît (identifiant et état de révocation seulement, jamais la valeur ni son condensat) par le transport IA signé et chiffré. L'onglet **Jetons d'API** affiche « Présent sur N nœuds sur M » ou, pour un jeton révoqué, « Révocation reçue par N nœuds sur M ». Un nœud qui ne répond pas est compté « injoignable » : il n'est jamais présumé à jour, et une révocation n'est pas garantie effective sur ce nœud tant qu'il n'a pas répondu. Sur un nœud seul, rien n'est affiché. La liste étant celle des nœuds joignables par ce transport, un nœud retiré du cluster n'y figure pas.

Références : [Codex App Server](https://developers.openai.com/codex/app-server), [changelog officiel du SDK Claude](https://github.com/anthropics/claude-agent-sdk-typescript/blob/main/CHANGELOG.md), déclarations `SDKControlGetUsageRequest` / `SDKControlGetUsageResponse` du paquet npm officiel 0.3.302.
