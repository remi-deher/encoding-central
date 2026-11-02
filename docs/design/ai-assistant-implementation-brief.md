# Encoding — Espace IA, assistant contextuel et clarification des paramètres

Statut : proposition validée sur le principe, à mettre en œuvre.
Date : 19 octobre 2026.
Public : IA ou développeur chargé de l’implémentation.

## 1. Mission

Faire évoluer l’IA déjà présente dans Encoding en une capacité cohérente de l’application :

- une entrée « Intelligence artificielle » dans le menu principal ;
- une vue d’ensemble de son état et de son activité ;
- un assistant conversationnel accessible depuis cet espace et depuis une bulle discrète ;
- des paramètres IA explicites et regroupés ;
- des outils métier partagés avec l’API et le serveur MCP existants ;
- une assistance aux flows plus fiable, avec propositions examinables avant application.

Ce document constitue le brief de réalisation. Implémenter les parcours fonctionnels, les contrats et leur validation ; ne pas se limiter à une maquette. Les fonctionnalités décrites comme ultérieures doivent rester identifiées comme telles si la livraison est découpée.

## 2. Contraintes et état existant

Avant toute modification, lire `AGENTS.md`, `UI_RULES.md`, `BACKEND_RULES.md`, `TEST_RULES.md` et les instructions plus proches des fichiers modifiés. Examiner le code actuel : les constats ci-dessous sont un point de départ, pas une garantie que le dépôt n’a pas évolué.

Préserver Angular, Angular Material/CDK, les Signals et OnPush, ASP.NET Core/.NET 10, SQLite local par serveur et le modèle distribué du cluster. Respecter les jetons de design et les thèmes clair/sombre. Ne pas introduire un serveur central permanent, un stockage SQLite partagé ou une nouvelle pile technique.

Références à consulter :

- [0071 — Fournisseurs IA, sessions et reprise](../architecture/domains/ai.md).
- [0072 — Génération de flows](../architecture/domains/ai.md).
- [0061 — Serveur MCP](../architecture/domains/auth-ops.md).
- [0062 — Descriptions pour les agents](../architecture/domains/plugins.md).
- [0023 — Simulation de flows](../architecture/domains/flows.md).
- [Exploitation des fournisseurs IA](../operations/ai-providers.md).
- [Présentation des ressources](interface-resource-layout.md).

Points d’entrée actuels :

- `frontend/src/app/shared/services/ai-tools.service.ts` et les modèles associés.
- `frontend/src/app/features/settings/tabs/ai-tools-tab.component.*`.
- `frontend/src/app/features/flows/editor/flow-ai-assistant-dialog.component.ts`.
- `frontend/src/app/features/flows/flow-editor-page.component.ts`.
- `backend/src/Encoding.Api/Tools/` : clients HTTP/CLI, configuration, sessions et transport de cluster.
- `backend/src/Encoding.Api/Flows/FlowAiGeneratorService.cs`.
- `backend/src/Encoding.Api/Flows/FlowCatalogPromptBuilder.cs`.
- Le serveur MCP, ses outils, leurs autorisations et les services métier appelés.

Les fournisseurs existants incluent Codex CLI, Claude CLI, Ollama et une API compatible OpenAI. Réutiliser les connexions, le confinement, le transport signé/chiffré et les mécanismes de reprise existants. Les secrets restent locaux aux nœuds qui hébergent les connexions.

Les sessions IA existantes sont réservées notamment aux administrateurs. L’ouverture d’un assistant aux viewers est une évolution explicite d’autorisation : analyser les contrats, isoler les conversations par utilisateur et ajouter les tests correspondants. Ne pas ouvrir globalement les endpoints de sessions existants.

## 3. Navigation et répartition des responsabilités

### Menu principal : Intelligence artificielle

Créer un espace dédié avec trois vues navigables :

| Vue | Objectif | Contenu |
| --- | --- | --- |
| Vue d’ensemble | Comprendre si l’IA fonctionne et ce qu’elle fait | Disponibilité observée, demandes en cours, activité récente, erreurs et accès rapides |
| Assistant | Poser des questions et reprendre les échanges | Conversations, ressources associées, réponses sourcées et propositions |
| Activité | Comprendre les opérations IA | Demandes, outils appelés, durées, résultats et actions appliquées |

Utiliser le mécanisme de navigation existant. Prévoir des liens directs vers une conversation et une opération. Conserver ou rediriger les URL existantes lorsqu’une page est déplacée.

### Paramètres : IA

Regrouper les réglages en quatre rubriques :

| Rubrique | Contenu |
| --- | --- |
| Fournisseurs et connexions | Comptes, URL, modèles, nœud hébergeur, connexion/déconnexion et test réel |
| Usages et modèles | Affectation d’un fournisseur/modèle à l’assistant, aux flows et aux traductions |
| Données et autorisations | Données transmissibles, outils accessibles et politique de confirmation |
| Limites et conservation | Délais, concurrence, budgets applicables et rétention des conversations/activités |

Le menu IA sert à utiliser et suivre l’IA ; les paramètres servent à la configurer. Ne pas dupliquer les formulaires de connexion dans les deux espaces. La vue d’ensemble peut pointer vers la configuration pertinente.

## 4. Vue d’ensemble

Répondre immédiatement aux questions : « Est-ce disponible ? », « Qu’est-ce qui tourne ? », « Qu’est-ce qui a échoué ? ».

Afficher uniquement des données observées :

- fournisseurs configurés et nœuds qui les hébergent ;
- dernier appel réussi et dernier échec connu ;
- demandes actives ou en attente, avec leur état réel ;
- activité récente avec accès à son détail ;
- tokens, coût ou quota uniquement lorsque ces informations sont réellement disponibles.

Distinguer configuration enregistrée, identifiants reconnus, disponibilité testée, nœud hors ligne et état inconnu. Une connexion CLI reconnue ne prouve ni un appel distant réussi ni un quota disponible. Ne pas déduire un coût nul de l’absence de données. Identifier les estimations et leur source si un calcul de coût est proposé.

Prévoir les états chargement, vide, indisponible et erreur récupérable. L’absence de fournisseur doit expliquer comment en configurer un. Un pair inaccessible ne doit pas disparaître comme si sa configuration avait été supprimée.

## 5. Assistant global et contextuel

### Présentation

Ajouter une bulle discrète dans le shell authentifié. Elle ouvre un panneau latéral redimensionnable sur ordinateur et une vue adaptée au mobile. Respecter les règles du projet sur les tiroirs : ne pas empiler l’assistant et les panneaux métier. Définir leur coexistence ou leur remplacement sans masquer des modifications non enregistrées.

La page Assistant et le panneau utilisent les mêmes conversations, le même état métier et les mêmes services. Le passage du panneau à la page conserve conversation, messages, contexte et demande en cours. La fermeture du panneau ne doit pas perdre un échange ou provoquer une nouvelle inférence à sa réouverture.

Prévoir focus clavier, nom accessible, fermeture, restitution du focus, adaptation mobile et protection des saisies. Éviter les ouvertures automatiques et les notifications sans événement utile.

### Contexte explicite

Afficher des ressources attachées à la conversation, par exemple :

- « Flow Films — version publiée 3 » ;
- « Brouillon du flow Films — révision 8 » ;
- « Exécution … » ;
- « Bibliothèque Séries » ;
- « Nœud Salon ».

Permettre leur retrait et leur ajout. Naviguer vers une autre page ne remplace pas silencieusement le contexte d’une conversation. Proposer explicitement d’ajouter la ressource ouverte.

Pour un flow en cours d’édition, distinguer l’état non enregistré de la version en base. Capturer la révision ou l’empreinte utilisée pour la proposition. Ne jamais appliquer un résultat sur un éditeur qui a changé depuis sans traiter le conflit.

### Cas d’usage initiaux

- Expliquer un flow et ses branches.
- Diagnostiquer une exécution échouée à partir des informations et journaux accessibles.
- Expliquer l’éligibilité des workers pour un traitement.
- Comprendre pourquoi une bibliothèque ne traite pas ses fichiers.
- Proposer une modification ciblée d’un flow.

Les réponses doivent citer les ressources consultées et proposer des liens internes. Si des informations manquent, le dire et demander une précision utile. Ne pas inventer un diagnostic, un outil installé ou une action exécutée.

## 6. Architecture des outils : API, MCP et assistant

Architecture cible :

```text
Assistant Angular → Orchestration IA backend → Outils autorisés → Services métier
                                                      ↑
                                              Adaptateur MCP existant
```

Partager les capacités et leurs règles entre l’assistant, MCP et l’API. L’assistant interne n’a pas besoin d’une connexion HTTP vers son propre serveur MCP. Examiner les outils existants et extraire un registre partagé seulement lorsque cela évite réellement une duplication.

Prévoir pour chaque outil : contrat d’entrée typé, résultat structuré, rôle requis, catégorie lecture/proposition/écriture, règles de confirmation, bornes de résultat et comportement en cas de répétition.

Le backend contrôle l’appel proposé par le modèle : outil autorisé, arguments valides, droits, politique de données et confirmation éventuelle. Ne pas lui donner un accès arbitraire aux endpoints, à SQL, au shell ou aux médias.

Les fournisseurs n’offrent pas tous les mêmes capacités d’appels d’outils ou de sorties structurées. Étendre les contrats communs avec une déclaration de capacités et une orchestration adaptée. Pour un fournisseur sans appels natifs, un format d’appel structuré reste possible, mais doit être analysé et validé côté backend. Ne pas annoncer une capacité comme disponible si elle n’est pas implémentée.

Borner le nombre d’appels d’outils par tour, la durée totale, la taille du contexte et des résultats. Les sorties d’outils, journaux, métadonnées et descriptions de plugins sont des données non fiables, pas des instructions autorisant une action.

## 7. Autorisations et actions

- Viewer : questions et outils de lecture autorisés, sans mutation métier.
- Admin : mêmes lectures et propositions de modification, puis actions autorisées selon leur catégorie.
- Conversations : privées à leur propriétaire par défaut ; aucun accès aux échanges d’autres utilisateurs sans règle explicite et testée.
- Recontrôler les droits lors de chaque appel et lors de l’application d’une proposition.

Les lectures peuvent s’exécuter directement. Une modification de flow devient une proposition examinable. La publication, le lancement d’un traitement, l’annulation d’une exécution et la suppression nécessitent une confirmation explicite avant leur exécution dans ce parcours assistant.

Lier toute confirmation à une action concrète, ses paramètres et la version de la ressource. Une confirmation ancienne ne doit pas autoriser des arguments modifiés. Réutiliser les contrôles de concurrence et mécanismes d’idempotence du métier ; une reprise de conversation ne doit pas rejouer une écriture déjà confirmée.

L’assistant ne contourne jamais les protections des médias et ne publie pas automatiquement un flow généré.

## 8. Fiabilisation de l’assistance aux flows

### Contexte de génération

Enrichir le catalogue transmis avec descriptions, valeurs autorisées, valeurs par défaut, champs obligatoires, contraintes, schémas imbriqués pertinents et descriptions des sorties. Filtrer selon le média et les plugins disponibles. Distinguer le catalogue déclaratif des capacités d’exécution observées sur les workers.

Réduire le contexte de façon déterministe lorsque nécessaire, sans retirer silencieusement les contraintes indispensables. Ajouter des exemples valides issus des contrats actuels, sans imposer les identifiants d’un exemple aux nouveaux flows.

### Génération et modification

Réunir les mécanismes communs : génération structurée, analyse JSON, validation et réparation bornée. Traiter aussi les erreurs de syntaxe JSON. Préserver l’identité du flow existant, les versions de schéma et les identifiants des blocs inchangés lors d’une modification ciblée.

Relier les tentatives de réparation à l’opération et à la session initiales, avec des identifiants de tentative distincts et stables. Réutiliser la reprise de 0071 ; ne pas laisser une réparation devenir un appel anonyme non suivi.

Un échec conserve le flow courant et retourne les derniers diagnostics utiles. Une proposition valide n’est pas enregistrée ni publiée automatiquement.

### Examen et application

Présenter le graphe proposé, les ajouts/modifications/suppressions et une explication des hypothèses. Réutiliser les capacités de comparaison existantes quand elles conviennent. Prévoir « Appliquer », « Rejeter » et une restauration de l’état précédent après application dans l’éditeur.

Le transfert doit préserver toute la définition utile, notamment les packs de composants. Vérifier l’application atomique du graphe et la détection des modifications non enregistrées.

### Vérification du comportement

Réutiliser le simulateur pour des cas représentatifs, par exemple vérifier qu’un média déjà HEVC contourne le transcodage demandé seulement pour H.264. Identifier les entrées fictives et les résultats supposés. Une simulation ne prouve pas une exécution réelle ni la disponibilité d’un encodeur.

## 9. Configuration, données et exploitation

Expliquer dans l’interface les notions de fournisseur, modèle, compte connecté, nœud hébergeur et usage. Conserver la protection des secrets et des modifications non enregistrées.

Le test d’un fournisseur doit effectuer un appel minimal réel, montrer son horodatage et son résultat, et indiquer qu’il peut consommer du quota. Il ne doit ni publier un flow ni traiter un média.

Permettre une affectation par usage avec héritage explicite du défaut. Toute politique de repli doit respecter les restrictions de données : une tâche déclarée locale à Ollama ne bascule pas vers un fournisseur externe. L’interface doit permettre de comprendre quelles catégories de données sont transmises, notamment prompts, définitions de flows, chemins, journaux ou sous-titres.

Réutiliser les sessions chiffrées et le maillage existants sans répliquer les secrets des fournisseurs. Séparer les contenus de conversation de la trace technique d’activité. Appliquer les autorisations et la rétention à chacun. Ne pas enregistrer prompts complets, secrets ou sous-titres dans les journaux techniques par défaut.

Définir précisément l’annulation : arrêter l’opération lorsqu’elle est contrôlable et indiquer les limites d’un appel déjà accepté par le fournisseur. Ne pas présenter la fermeture du panneau comme une annulation.

## 10. Activité et observabilité

Tracer une opération logique avec ses tentatives : utilisateur, usage, fournisseur/modèle observé, nœud exécuteur, identifiants de corrélation, horaires, état, durée, outils utilisés et résultat d’application.

Distinguer attente, exécution, validation, réparation, proposition prête, succès, échec, annulation et résultat distant incertain lorsque pertinent. Les indicateurs de progression doivent correspondre à des événements backend réels.

Afficher des erreurs actionnables : fournisseur indisponible, quota, authentification, délai, résultat invalide, refus de droits ou conflit de révision. Préserver les détails techniques utiles aux administrateurs sans exposer de secrets.

## 11. Découpage recommandé

1. **Socle et organisation** : audit du code, décision d’architecture, navigation IA, clarification des paramètres, vue d’ensemble alimentée par des données réelles.
2. **Assistant en lecture seule** : conversations persistantes, contexte explicite, panneau/page synchronisés, outils de lecture, réponses sourcées et activité.
3. **Propositions de flows** : contexte enrichi, chaîne commune de réparation, aperçu des différences, application explicite et gestion des conflits.
4. **Actions et vérification** : confirmations liées aux paramètres, exécution contrôlée, simulation, limites et rétention complètes.

Chaque étape doit livrer un parcours utilisable et testé. Ne pas afficher comme disponibles les opérations prévues pour une étape suivante. La première version peut se concentrer sur Flows, Exécutions et Nœuds, puis étendre le contexte aux bibliothèques.

## 12. Critères d’acceptation

- Un utilisateur trouve l’espace IA depuis le menu et les réglages depuis Paramètres → IA.
- Un cluster sans fournisseur affiche un état vide utile ; un fournisseur configuré mais inaccessible n’apparaît pas disponible.
- Une conversation commencée sur une exécution se poursuit dans la page Assistant sans perte ni appel dupliqué.
- Le contexte attaché est visible et reste stable lors d’une navigation.
- Une réponse de diagnostic cite les ressources réellement consultées.
- Un viewer ne peut ni appeler un outil d’écriture ni consulter la conversation d’un autre utilisateur, même par appel API direct.
- Un outil inventé, des arguments invalides ou une instruction injectée dans un journal ne déclenchent aucune action non autorisée.
- Une proposition IA ne modifie pas le flow avant application ; son rejet laisse l’éditeur intact.
- Une application conserve les données utiles du flow et refuse ou résout explicitement une révision devenue obsolète.
- Une action confirmée n’est pas rejouée lors d’une reconnexion ou d’une nouvelle tentative.
- Les réparations sont bornées et traçables ; l’échec n’efface pas le travail de l’utilisateur.
- Une politique « local uniquement » interdit tout repli externe.
- Les interruptions de fournisseur ou de nœud respectent les garanties de reprise existantes.
- Les coûts et quotas inconnus restent indiqués comme inconnus.
- Les parcours fonctionnent au clavier, sur mobile et dans les deux thèmes.

## 13. Tests et livrables attendus

Ajouter des tests ciblés sur les droits, l’isolation des conversations, les appels d’outils, les confirmations, les conflits, la reprise, les politiques de données et les sorties IA invalides. Utiliser des fournisseurs simulés déterministes en CI ; aucun secret ou abonnement réel ne doit être nécessaire.

Préparer un corpus d’évaluation de demandes : HEVC/HDR, règles audio, sous-titres, garde Plex et modifications ciblées. Mesurer validité au premier essai, réparations, respect de l’intention, changements indésirables et latence. Séparer les évaluations avec modèles réels des tests déterministes.

Vérifier compilation, types, lint, format, tests backend/frontend pertinents et parcours E2E légers. Contrôler l’accessibilité et les formats ordinateur/mobile. Signaler les vérifications non exécutées et leur raison.

Livrer le code, les migrations non destructives nécessaires, la documentation d’exploitation et une décision d’architecture mise à jour. Mettre à jour `AGENTS.md` uniquement si les principes du projet évoluent. Préserver les contrats et URL existants ou documenter leur migration.

En fin de réalisation, fournir un bilan des capacités livrées, des tests exécutés, des limites restantes et du périmètre des étapes suivantes. Ne pas présenter une simple connexion de fournisseur comme un assistant complet.
