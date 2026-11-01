# Reconstruction d'Encoding

Encoding reprend sur une base étroite. Le dépôt historique `remi-deher/encoding` reste en place, ainsi que la production qui l'exécute. Ce dépôt, `encoding-central`, est la ligne nouvelle. Il ne reprend pas le cluster.

## Décision

Un seul serveur est l'autorité. Il détient la base SQLite, l'interface et, plus tard, l'ordonnancement. Des workers viendront s'y enregistrer. Ils ne deviennent pas serveurs. Il n'y a pas de second écrivain, pas de maillage, pas de bascule de maîtrise, pas de promotion d'urgence.

Le mot « nœud » ne désigne plus une machine. Le paquet `nodes` du Store, lui, reste un composant de flow : ce catalogue est déjà publié et ses archives sont immuables.

## Ce qui est conservé

- La pile : C# ASP.NET Core sur .NET 10, Angular 22, Material 3, Docker, SQLite.
- Le shell : navigation de 248 px, rail de 88 px entre 761 et 1 100 px, barre basse sous 761 px, palette teal et ses variantes, thèmes clair et sombre, en-tête fixe, lien d'évitement.
- Le contrat du Store [remi-deher/encoding-store](https://github.com/remi-deher/encoding-store) : `repository.json`, archives immuables, empreintes SHA-256, plugins de processus en Landlock, manifestes natifs dont le code reste dans le moteur. Le dépôt du Store n'est pas réécrit.
- Les règles d'exécution, quand elles reviendront : bail et époque, une réservation par fichier, pas de réattribution silencieuse, travail sur une copie locale, validation puis publication.

## Ce qui ne revient pas

La réconciliation entre serveurs, les pairs, le mode persisté qui peut relancer une API, la redirection vers un autre maître, la copie de la base promue en serveur. La haute disponibilité n'était pas tenue. Elle n'est plus promise. Si ce serveur s'arrête, le travail s'arrête avec lui jusqu'à ce qu'un opérateur le rallume ou restaure la base.

## Ordre

1. **Serveur central.** C'est l'état de ce dépôt. Une image, une base dont ce processus est le seul écrivain, une mise en service qui crée l'administrateur, une session, la santé, et le shell avec la vue d'ensemble et les paramètres. Le modèle connaît déjà un serveur et un registre de workers, vide.
2. **Store.** Le serveur lira `repository.json`, vérifiera les empreintes et montrera les paquets installés. Aucune exécution.
3. **Worker.** Une seconde image, sans le binaire du serveur. Elle ne connaîtra que l'adresse publique de ce serveur, s'enregistrera, et déclarera les outils réellement présents.
4. **Bibliothèques, flows, attribution.** Seulement ensuite. Les poser avant le worker reconstruirait l'ancien produit dans le processus du serveur.

## Première partie, livrée ici

L'image `encoding-central` publie l'API et l'interface sur le port 8080. La base est le fichier `Database:Path` (`/var/lib/encoding/server.db` dans l'image). Les migrations sont additives et numérotées. `0001_server` crée :

| Table | Rôle |
| --- | --- |
| `server` | Une seule ligne. Un déclencheur refuse la seconde. Elle porte le nom et l'adresse publique, sans chemin. |
| `users` | Compte `admin`. Le mot de passe est une empreinte PBKDF2, jamais journalisée. |
| `sessions` | Jeton aléatoire, seule son empreinte SHA-256 est stockée. Durée de douze heures. Cookie `encoding_session`, HttpOnly. |
| `workers` | Registre vide. Aucune route ne crée encore de worker. |

Les routes de gestion exigent une session. Les seules routes anonymes sont la santé, l'état de mise en service, la mise en service tant qu'aucun compte n'existe, l'ouverture de session et sa fermeture. Une route nouvelle est fermée par défaut. Le test `Anonymous_routes_are_exactly_the_declared_set` fige cette liste.

`GET /health/live` et `GET /health/ready` décrivent le processus et la base. `GET /api/v1/setup` dit si la mise en service est encore nécessaire. `PUT /api/v1/server` met à jour le nom et l'adresse publique.

L'interface reprend le shell et deux destinations : Vue d'ensemble, Paramètres. La recherche globale, les tiroirs et les pages Flows, Activité, Bibliothèques, Nœuds et Plugins ne sont pas recopiés. Le bandeau de recherche reviendra avec les catalogues. La barre basse a deux places ; le menu « Plus » reviendra quand les destinations dépasseront le dock.

## Lancer

Développement, deux processus :

```sh
dotnet run --project backend/src/Encoding.Server
```

```sh
npm start --prefix frontend
```

L'interface de développement est sur le port 4200 et proxifie `/api` et `/health` vers le port 8080.

Image :

```sh
docker compose up --build
```

L'image construite se nomme `encoding-central:dev`. Le conteneur écoute sur le port 8080. Le volume `server-data` conserve la base. Rien dans cette image n'encode : FFmpeg et les autres outils arriveront par le Store.

Les tests du serveur :

```sh
dotnet test backend/Encoding.Central.slnx -c Release
```
