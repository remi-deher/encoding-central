---
id: 0008
title: Assistant de configuration Plex — connexion plex.tv et correspondance des bibliothèques
status: implemented
domain: plugins
date: 2026-10-23
decided_by: Rémi (2026-10-23) pour le besoin « faciliter la mise en place du plugin et la mise en correspondance des bibliothèques » (connexion SSO ou adresse + récupération des bibliothèques) ; mécanismes ci-dessous : choix de l'implémentation
supersedes: []
superseded_by: []
tags: [plex, plugin, configuration, plex.tv, pin, correspondance-des-chemins, garde-de-publication]
---

# 0008 — Assistant de configuration Plex

## Contexte
Le plugin Plex se configure aujourd'hui avec trois champs saisis à la main : adresse, jeton et `pathMappings`, une ligne `identifiant-bibliothèque=/chemin/vu/par/Plex` par bibliothèque, avec des GUID à recopier. Le jeton s'obtient en fouillant le XML de Plex. Résultat observé en production le 2026-10-23 : le plugin n'était pas configuré, donc **aucune garde de publication** n'existait avant d'activer le remplacement des sources. Le format `LibraryPathMapping` (une définition C#, vecteurs partagés avec Python) est lu par les plugins et par la garde : il ne doit pas changer.

## Décision
- **Fonctionnalité hôte dédiée à Plex**, sans changement du contrat des plugins.
  - Le schéma déclaratif du plugin reste la source des valeurs (`baseUrl`, `apiKey`, `pathMappings`).
  - L'assistant ne fait qu'écrire ces champs, via `PluginConfigurationRepository.SaveGlobalAsync`, avec la même validation qu'un enregistrement manuel.
  - L'interface est un éditeur métier typé dans l'onglet Configuration du plugin Plex (`UI_RULES` §3.3), pas un code d'interface fourni par le plugin.
- **Connexion plex.tv par code PIN** : flux officiel « Sign in with Plex ».
  1. `POST /api/v1/plugins/plex/setup/pin` crée un PIN (`strong=true`) avec un identifiant client propre à cette demande, et renvoie le lien `app.plex.tv/auth`.
  2. L'interface ouvre ce lien et interroge `POST …/setup/pin/check`.
  3. Une fois le PIN autorisé, le serveur liste les serveurs Plex du compte (`/api/v2/resources`) et renvoie une **session opaque** : le jeton du compte, chiffré avec la protection partagée du cluster (`PeerSecretProtection`, à défaut Data Protection) et daté. La session expire après 15 minutes.
- **Choix du serveur** : `POST …/setup/server` (session, serveur, adresse). Le serveur relit les ressources avec la session, prend le **jeton d'accès propre au serveur choisi**, vérifie `/identity` à l'adresse retenue, puis enregistre `baseUrl` et `apiKey`.
- **Le jeton du compte et le jeton du serveur ne quittent jamais le serveur Encoding.** Ils n'apparaissent ni dans les réponses, ni dans les journaux, ni dans les erreurs.
- **Récupération et correspondance des bibliothèques** : `POST …/setup/libraries`.
  - Il utilise les valeurs du formulaire, ou à défaut celles enregistrées, exactement comme le test de connexion. L'adresse et le jeton saisis à la main restent donc possibles, ce qui couvre le cas « on met l'IP ».
  - Il lit `/library/sections` et renvoie les sections Plex, les bibliothèques Encoding avec les chemins déclarés par chaque nœud en accès direct, la correspondance actuelle et une proposition.
- **Proposition** : règle unique et déterministe dans `LibraryMappingSuggester`, partagée et testée.
  - Un chemin de nœud identique à un emplacement Plex l'emporte.
  - À défaut, il faut au moins un segment final commun. Le score croît avec le nombre de segments communs, avec un bonus si le type de section (`movie` ou `show`) correspond au nom de la bibliothèque.
  - Une égalité de score n'est **pas** tranchée : aucune proposition, choix manuel.
  - Un emplacement Plex n'est proposé qu'à une seule bibliothèque.
- **Application** : `PUT …/setup/mappings` reçoit la liste choisie dans l'interface et réécrit `pathMappings` au format `LibraryPathMapping`. Les autres réglages sont conservés.

## Alternatives écartées
- **Nouveau type de champ déclaratif générique** (« compte externe », « correspondance de bibliothèques ») : une seule intégration réelle en a besoin aujourd'hui. Abstraction prématurée (`BACKEND_RULES` §2). Jellyfin, Sonarr et Radarr pourront réutiliser `LibraryMappingSuggester` et l'éditeur de correspondance.
- **Garder le jeton du compte en mémoire du serveur** entre deux requêtes : pas fiable derrière plusieurs serveurs (cluster sans serveur central). La session chiffrée sans état est valable sur tout serveur qui partage la clé du cluster.
- **Envoyer le jeton au navigateur** pour qu'il remplisse le champ secret : le jeton serait exposé côté client, ce qui est contraire à l'usage des champs `secret`.
- **Changer le format de `pathMappings`** (JSON structuré) : il faudrait migrer les plugins Python et la garde, et casser la compatibilité entre nœuds de versions mixtes.

## Conséquences et risques
- Pas de migration ni de changement du contrat des plugins. Les nœuds plus anciens lisent la même configuration.
- Le serveur Encoding doit joindre `plex.tv` en HTTPS pour la connexion par PIN. Sans accès Internet, la saisie manuelle de l'adresse et du jeton, puis la récupération des bibliothèques, restent possibles.
- Un appareil « Encoding » apparaît dans le compte Plex à chaque autorisation (identifiant client propre à la demande). Il est révocable depuis plex.tv.
- La proposition ne remplace pas une vérification : l'interface montre chaque correspondance et rien n'est enregistré sans action « Enregistrer ».
- Tests : règles de proposition (exact, suffixe, égalité, unicité, type), session (expiration, falsification), extraction des serveurs et des sections depuis des réponses Plex enregistrées. Non vérifié automatiquement : le dialogue réel avec plex.tv, vérifié à la main.
