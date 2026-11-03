# Sauvegarde et restauration

## Une seule base

Le central utilise exclusivement SQLite local ; il n'y a ni réplication ni maillage. Avant la mise à jour d’une ancienne installation qui utilisait le maillage ou le mode de consensus retirés, sauvegarder à froid sa base, ses identités, ses clés et ses journaux : aucune conversion automatique n’est prise en charge ([ADR 0036](../architecture/adr/0036-separation-stricte-central-worker.md)).

## Ce qui doit être sauvegardé

| Donnée | Emplacement (Compose) | Criticité |
| --- | --- | --- |
| Base SQLite du central : configuration, flows, bibliothèques, workers et historique | `/var/lib/encoding-node/cluster.db`, volume `encoding-node-data` | **Indispensable** (c'est la seule copie) |
| Clés Data Protection des plugins | volume `encoding-plugin-keys` (`PluginSettings__KeyDirectory`) | **Indispensable.** À sauvegarder avec la base : elles servent à déchiffrer les secrets de plugins. |
| Sauvegardes JSON des flows | volume `encoding-flow-backups` (`FlowBackups__Directory`) | Recommandé. Une copie indépendante de la base. |
| Artefacts d’exécution | volume `encoding-runtime-artifacts` (`Runtime__ArtifactDirectory`) | Facultatif pour les tâches terminées ; requis pour préserver les reprises d’étapes qui en dépendent. |
| Identité locale du worker | volume `encoding-node-data` | À conserver pour éviter un nouvel enrôlement. |
| Fichiers média publiés | bibliothèques média | À sauvegarder selon la politique du stockage média. |

Seul le central possède une base : c’est elle qu’il faut sauvegarder. Les workers n’en ont pas ; ils ne sont ni une sauvegarde de la base ni une copie des fichiers médias et artefacts.

## Sauvegarde à froid

Arrêter le conteneur pour vider le WAL SQLite, puis archiver le volume de données et les volumes séparés utiles :

```bash
docker compose stop encoding
for volume in encoding-node-data encoding-plugin-keys encoding-flow-backups encoding-runtime-artifacts; do
  docker run --rm -v "backend_${volume}:/data:ro" -v "$PWD:/out" alpine \
    tar czf "/out/${volume}-$(date -u +%Y%m%dT%H%M%SZ).tgz" -C /data .
done
docker compose start encoding
```

Le préfixe `backend_` correspond au nom de projet Compose de cet exemple ; vérifier le nom réel avec `docker volume ls`. Conserver une copie chiffrée hors de l’hôte, surtout pour les clés de plugins.

Pour une sauvegarde à chaud, utiliser `sqlite3` sur le fichier monté dans un chemin local et `.backup destination.db`, qui inclut les changements du WAL. Ne jamais copier uniquement `cluster.db` pendant que le serveur tourne.

## Vérifier et restaurer

Restaurer périodiquement une sauvegarde dans un volume de test puis démarrer une instance isolée avec ce volume. Contrôler `/health/details`, les flows, les utilisateurs, les bibliothèques et les événements d’exécution avant de déclarer la sauvegarde exploitable.

Pour une restauration réelle :

1. Arrêter le serveur concerné et conserver une copie de ses volumes actuels.
2. Restaurer ensemble `encoding-node-data` et `encoding-plugin-keys`. Restaurer également les volumes de sauvegarde de flows et d’artefacts si nécessaire.
3. Démarrer le serveur et contrôler `GET /health/details` : `database`, `migrations` et les contrôles de stockage doivent être `healthy`.
4. Vérifier la page Workers. Un worker enrôlé après la sauvegarde n’y figure plus : il doit être enrôlé à nouveau. Les baux actifs et les fichiers ne sont pas restaurés par la base.

Si un média a été remplacé, sa sauvegarde de remplacement doit également être restaurée depuis le stockage configuré pour cette bibliothèque.

## Installation fraîche

Au premier démarrage, le serveur crée `/var/lib/encoding-node/cluster.db`, applique les migrations et établit l’identité locale du central. Pour installer sans reprendre les données précédentes, démarrer avec un volume `encoding-node-data` neuf. Cela réinitialise aussi l’identité du worker ; les volumes de sauvegardes, de clés de plugins et d’artefacts sont distincts et doivent être conservés ou remplacés selon le besoin.

## Perte d’un worker ou d’un serveur

Retirer une identité de worker perdue depuis l’interface, puis enrôler la nouvelle installation et redéclarer ses accès aux bibliothèques. Les tentatives actives d’un worker perdu échouent après expiration du bail ; elles ne sont pas reprises automatiquement. Une relance vient d’une règle explicite du flow ou d’une action manuelle.

À la perte du central, il n’existe aucune autre copie de la base : restaurer sa base et ses clés depuis la sauvegarde. Restaurer le volume d’un worker perdu si son identité locale doit être récupérée ; sinon l’enrôler à nouveau.

## Mise à jour

Avant une mise à jour qui apporte des migrations, sauvegarder chaque fichier SQLite et les clés de plugins. Les migrations ne sont pas réversibles : un retour arrière exige de restaurer la base et les volumes correspondants. Les migrations sont appliquées dans une transaction SQLite d’écriture immédiate.
