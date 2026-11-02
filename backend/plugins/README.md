# Écrire et empaqueter des plugins

Un plugin ajoute des composants de flow (actions, conditions) fournis par un processus séparé. Le code source des plugins maintenus par le projet, leur SDK, leurs exemples et leurs tests vivent dans le dépôt public [remi-deher/encoding-store](https://github.com/remi-deher/encoding-store). Le moteur ne les copie plus dans l'image. Le contrat d'exécution est décrit dans [la fiche plugins](../../docs/architecture/domains/plugins.md) et [le guide du Store](https://github.com/remi-deher/encoding-store).

```
plugin.json                  manifeste + runtime (`python3` ou `node`)
main.py                      point d'entrée isolé
encoding_plugin_sdk.py       SDK copié dans l'archive, aucune dépendance installée
```

## Modèle d’exécution

- Un plugin est un programme lancé **à chaque appel** par le worker, qui lui transmet une requête JSON d’une ligne sur stdin et lit une réponse JSON d’une ligne sur stdout.
- Il s’exécute sous Linux Landlock : écriture seulement dans son espace temporaire, lecture des exécutables et dépendances déclarés, pas d’accès au jeton du nœud ni aux montages média. Le réseau reste disponible (HTTP/HTTPS, DNS).
- **Langages** : Python 3 (bibliothèque standard) et Node.js sont installés dans l'image et ont un SDK. Un exécutable .NET n'a pas été essayé depuis que `/proc` est lisible dans le bac à sable (voir 0032) : à valider sur un hôte Landlock avant de le promettre. Le protocole est du JSON d'une ligne, n'importe quel langage qui sait lire stdin peut l'implémenter.
- **Environnement** : le processus ne reçoit pas l'environnement du worker, seulement `PATH`, `LANG`, `LC_ALL`, `LC_CTYPE`, `TZ`, plus `HOME` et `TMPDIR` pointant vers son espace de travail. Toute configuration passe par la configuration du plugin (champs `secret` compris), jamais par des variables d'environnement.
- **Limites** : requête de 1 Mo au plus ; réponse de 1 Mo au plus ; 64 variables au plus (nom de 128 caractères, 256 Ko au total) ; `detail` tronqué à 4 096 caractères ; échéance de l'invocation 6 h au plus, que le composant peut raccourcir avec `timeoutSeconds` (1 à 21 600) dans son descripteur. Au-delà, le processus est arrêté avec son arbre et l'effet est considéré incertain.
- **Diagnostic** : en cas d'échec, le code de sortie et la fin de stderr (400 caractères) apparaissent dans le motif de l'étape ; le journal de commandes de l'étape garde stdout/stderr bornés. Écrivez des messages d'erreur utiles et jamais de secret.
- Un plugin ne choisit jamais de chemin de publication et ne déclare pas une tâche terminée : l’hôte analyse et valide les sorties.

## Écrire un plugin

Outils d'auteur, sans cluster ni Landlock :

```
python3 sdk/encoding_plugin_sdk.py validate manifest.json          # mêmes règles que le serveur
python3 plugin.py --simulate mon-composant '{"cle":"valeur"}' --media 'Dossier/e1.mkv'
node sdk/encoding-plugin-sdk.mjs validate manifest.json             # équivalent JavaScript
node plugin.mjs --simulate mon-composant '{"cle":"valeur"}'
```

`validate` liste chaque problème (identifiant, version, sorties sans description, type de champ inconnu, `timeoutSeconds`…) ; un manifeste que le serveur refuse est écarté et signalé dans la page Plugins (bandeau « manifeste(s) ignoré(s) ») sans empêcher le démarrage. `--simulate` construit une invocation et affiche la réponse du contrat. Les identifiants de plugin sont en minuscules (`mon-plugin` ou `espace.mon-plugin`), la version est `X.Y.Z`.

1. **Manifeste** (`manifest.json`) : identité, version, contrat hôte `[1]`, composants (id, version, catégorie fonctionnelle, type `action`/`condition`, médias compatibles, sorties, schéma de configuration déclaratif). `category` utilise un identifiant stable du catalogue (`inputs`, `files`, `discovery`, `metadata`, `encoding`, `video`, `audio`, `images`, `text`, `containers`, `media-rules`, `validation`, `conditions`, `variables`, `scripts`, `arr`, `media-servers`, `communications`, `cluster`, `orchestration`, `reporting` ou `utilities`). Le champ est optionnel pour les anciens plugins ; sans lui, l’éditeur classe le bloc dans « Utilitaires ». Une catégorie inconnue est refusée par le serveur et les SDK. Ce champ est éditorial et n’entre pas dans l’empreinte d’exécution du worker. Voir `example/manifest.json`. Un champ de la configuration du plugin peut porter `"scope": "server"` : sa valeur reste sur le serveur et n'est jamais envoyée au worker (utile pour un secret de webhook). Le plugin ne fournit aucun code d’interface : le formulaire est rendu à partir du schéma (`text`, `number`, `boolean`, `select`, et `secret` pour la configuration du plugin).
2. **Code** (`plugin.py`) :

```python
from encoding_plugin_sdk import Plugin, Outcome, InfrastructureError

plugin = Plugin(manifest)            # dict chargé depuis manifest.json

@plugin.component("example-tag-file")
def tag_file(ctx):
    nom = ctx.text("name")                       # paramètre de l'instance du composant
    adresse = ctx.plugin_text("baseUrl")         # configuration effective du plugin
    # ctx.media_path, ctx.library_id, ctx.variables : fichier traité et variables du flow
    return Outcome.output("success", "terminé", variables={"nom": "valeur"})

if __name__ == "__main__":
    raise SystemExit(plugin.run())
```

   - Retournez une **sortie déclarée** pour un résultat métier attendu (« introuvable » → sortie `notFound`/`error`).
   - Levez `InfrastructureError` pour une panne technique (service injoignable, authentification refusée) : le processus se termine en code 1, l’hôte considère l’effet incertain et n’en fait jamais une sortie métier.
   - N’écrivez aucun secret sur stdout/stderr. Les clés ne passent que par la configuration du plugin (`secret`, chiffrée en base).
3. **Idempotence** : une invocation peut être relancée après une perte de bail ; préférez des effets idempotents (une commande de relecture l’est, un ajout ne l’est pas).

## Protocole (contrat 1)

### Déclarer les effets sur le média

Chaque composant peut ajouter `"mediaEffect": "readOnly"`, `"producesOutput"` ou `"modifiesCurrent"` à son descripteur :

| Valeur | Contrat du composant | Contrôles du moteur |
| --- | --- | --- |
| `readOnly` | Ne modifie pas le fichier courant, ne retourne aucun candidat | Vérification légère taille/dernière modification si un fichier est fourni |
| `producesOutput` | Préserve le fichier courant, retourne un nouveau fichier si nécessaire | Vérification légère de l'entrée, scan et SHA-256 du candidat |
| `modifiesCurrent` | Peut modifier la copie de travail courante | Fichier courant traité comme candidat même si taille/date identiques |
| Absent | Plugin historique | Détection taille/date, ou candidat explicite |

Un composant `readOnly` avec `requiresMedia: false` et `producesFiles: false` peut travailler directement sur les métadonnées : `ctx.current_file` / `ctx.currentFile` est alors absent (`None` / `null`). Les métadonnées sont dans `ctx.current_metadata` / `ctx.currentMetadata` et `ctx.before_metadata` / `ctx.beforeMetadata`. Le plugin n'obtient aucun accès au montage média. S'il a besoin des octets, déclarer `requiresMedia: true` : l'hôte prépare une copie de travail isolée.

Pour produire un fichier, utiliser `Outcome.output("success", candidate="result.mkv")` en Python ou `Outcome.output("success", "terminé", {}, "result.mkv")` en JavaScript. Le candidat est relatif à l'espace de travail ; le moteur vérifie le chemin, scanne le fichier, contrôle sa durée et le stocke. Un composant `producesOutput` sans travail peut retourner une sortie `unchanged` sans candidat. Pour une mutation sur place de la copie, déclarer `modifiesCurrent` ; un script de flow retourne explicitement `{"candidate": context["currentFile"]}` après écriture.

Taille/date sont une détection rapide, pas une preuve cryptographique : une modification qui conserve ces deux valeurs doit être déclarée. La date de création n'est pas utilisée. Les contrôles SHA-256 des transferts et de publication restent imposés par le moteur ; aucune déclaration ne les désactive. Ces effets ne changent pas les règles d'idempotence ou de reprise ni l'interdiction de modifier la bibliothèque depuis un plugin. Incrémenter la version d'un plugin qui ajoute ces déclarations ; elles entrent dans l'empreinte de contrat vérifiée à l'attribution, pour exclure un ancien worker qui les ignorerait.

```json
{"type":"describe","contractVersion":1}
→ le manifeste (PluginManifest)

{"type":"execute","contractVersion":1,"invocation":{ "invocationId":"…","leaseEpoch":3,
  "component":{"pluginId":"example","pluginVersion":"1.0.0","componentId":"example-tag-file","componentVersion":"1.0.0","configurationSchemaVersion":1},
  "configuration":{…},"pluginConfiguration":{…},
  "context":{"mediaPath":"Show/S01/e1.mkv","libraryId":"…","variables":{…},"currentArtifactId":"current.mkv"}}}
→ {"contractVersion":1,"invocationId":"…","leaseEpoch":3,"outputId":"success","terminalResult":null,
   "variableChanges":{…},"candidateArtifactId":null,"detail":"…"}
```

Exactement un de `outputId` ou `terminalResult` (`unchanged`, `success`, `failed`, `deferred`). Le SDK vérifie la version du contrat, l’identité du plugin, le composant et la sortie déclarée ; l’hôte contrôle de nouveau l’identité de l’invocation et l’époque du bail.

## Installer un plugin tiers

**Par le Store (recommandé)** : empaquetez le plugin en `.zip` avec un `plugin.json` à la racine (manifeste habituel plus `"runtime": { "type": "python3", "entrypoint": "main.py" }`, identifiant `espace.nom`), copiez-y le SDK à côté du point d'entrée, puis utilisez *Plugins & Outils → Store & Dépôts*. Les plugins d'intégration du projet (Plex, Sonarr, Radarr et Jellyfin) sont publiés dans le dépôt public et installables depuis ce Store. Le serveur contrôle l'archive ; chaque worker la télécharge et vérifie son empreinte. Le code plugin et ses tests peuvent ainsi évoluer dans le dépôt Store sans compiler l'application.

### Paquets d'outils du Store

Les binaires sont publiés comme paquets indépendants, jamais copiés dans chaque archive plugin. Un plugin déclare ses dépendances au niveau du manifeste :

```json
"requiredToolPackages": [
  {
    "toolPackageId": "ffmpeg",
    "minimumVersion": "7.1.0",
    "maximumVersionExclusive": "8.0.0",
    "requiredCapabilities": ["libx264"]
  }
]
```

Le dépôt doit inclure une version compatible pour la plateforme du serveur. Le Store installe aussi les variantes compatibles publiées pour les autres plateformes ; chaque nœud ne télécharge que son architecture. L'administrateur peut installer un autre paquet fournisseur global pour le rôle après avoir retiré celui en conflit. Il n'y a qu'un fournisseur d'un rôle par plateforme, donc deux plugins qui demandent `ffmpeg` utilisent le même binaire installé.

Chaque archive outil contient `tool.json` à sa racine. Exemple de manifeste :

```json
{
  "contractVersion": 1,
  "id": "ffmpeg",
  "version": "7.1.0",
  "name": "FFmpeg",
  "platform": "linux-x64",
  "executables": [
    { "toolId": "ffmpeg", "path": "bin/ffmpeg", "sha256": "<sha256 du fichier ffmpeg>" },
    { "toolId": "ffprobe", "path": "bin/ffprobe", "sha256": "<sha256 du fichier ffprobe>" }
  ],
  "capabilities": ["libx264", "libx265"]
}
```

Dans `repository.json`, chaque paquet `tools[]` donne `id`, `name`, `summary`, `latestVersion` et `versions[]`; chaque version donne la plateforme, l'URL d'archive et son SHA-256. Le serveur vérifie le hash de l'archive puis ceux des exécutables dans `tool.json`. Le worker répète ces contrôles avant extraction. Les hashes garantissent que l'archive et ses binaires n'ont pas changé depuis publication du manifeste ; ils ne remplacent pas une signature du dépôt.

**À la main** (plugin fourni avec l'image, ou besoin d'un lancement particulier) :

- **Serveur** : copier le manifeste dans le dossier `FlowPlugins:ManifestDirectory` (variable `FlowPlugins__ManifestDirectory`) et redémarrer. Le catalogue valide le manifeste ; les composants apparaissent dans l’éditeur.
- **Chaque worker** qui doit exécuter le plugin : installer les fichiers hors du dossier de données du nœud (par exemple `/opt/encoding/plugins/mon-plugin`) et déclarer :

```
Runtime__Plugins__0__Id=mon-plugin
Runtime__Plugins__0__Version=1.0.0
Runtime__Plugins__0__Executable=/usr/bin/python3
Runtime__Plugins__0__Arguments__0=-B
Runtime__Plugins__0__Arguments__1=/opt/encoding/plugins/mon-plugin/plugin.py
Runtime__Plugins__0__ReadOnlyPaths__0=/opt/encoding/plugins/mon-plugin
```

  La version déclarée doit être celle du manifeste. Le worker n’annonce la capacité `plugin/mon-plugin` que si la poignée de main réussit ; un flow qui utilise le plugin n’est attribué qu’aux workers qui l’ont. Copiez `encoding_plugin_sdk.py` à côté de `plugin.py` (ou dans un dossier ajouté à `ReadOnlyPaths`).

## Sonarr et Radarr

Dans **Plugins**, configurer le plugin (adresse, clé API, test de connexion), puis :

- **Correspondance des chemins** : une ligne par bibliothèque Encoding, `identifiant-bibliothèque=/chemin/vu/par/Sonarr` (par exemple `3f2c…=/tv`). Elle sert au déclencheur et aux composants à retrouver le fichier traité.
- **Secret du webhook** : une valeur aléatoire longue.
- Dans Sonarr/Radarr, **Paramètres → Connexions → Webhook**, événement **À l’import** (et **Mise à niveau**) : URL `http://<serveur-encoding>:8080/api/v1/integrations/sonarr/webhook` (ou `radarr`), méthode POST, mot de passe = secret du webhook (nom d’utilisateur libre) ; le bouton Tester doit répondre OK.
- Attribuer à la bibliothèque un flow publié : à chaque import, le fichier est découvert puis traité par ce flow.

Composants : Sonarr — *Actualiser une série*, *Rechercher les épisodes manquants*, *Vérifier l’épisode* ; Radarr — *Actualiser un film*, *Rechercher le film*, *Vérifier le film*. Sans identifiant dans le composant, l’élément est celui qui contient le fichier traité.
