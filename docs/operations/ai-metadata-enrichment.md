# Enrichissement IA des métadonnées

Le bloc `ai-metadata-enricher` analyse les métadonnées techniques et le nom du fichier, puis crée un artefact JSON annexe contenant un titre, un résumé, des mots-clés, les données techniques retenues et la provenance des éventuelles données externes. Il ne modifie pas le média d’origine.

La recherche externe est désactivée par défaut. Quand elle est activée, seul le nom du fichier (sans chemin) est envoyé au catalogue sélectionné. Les appels utilisent des adresses de service fixes; aucun chemin local ni contenu média n’est transmis. La recherche doit obtenir une correspondance de titre exacte et unique avant d’utiliser le résultat. Un résultat absent ou ambigu est laissé sans correspondance. Le mode IA local uniquement interdit les recherches externes.

## Sources prises en charge

- **TMDb** pour les films et séries. Configurer le jeton d’accès API en lecture sur chaque serveur API avec la variable d’environnement `MetadataSources__TmdbAccessToken`. Le jeton n’est pas enregistré dans le flow ni ajouté aux URL. Respecter les conditions TMDb, notamment l’attribution de la source.
- **MusicBrainz** pour les albums. Le service espace les recherches à au moins 1,1 seconde et ajoute l’identifiant applicatif attendu par MusicBrainz.

L’artefact expose `externalLookup` (état de la recherche) et `externalMatch` (source, URL, identifiant, titre, année et attribution) afin que les consommateurs puissent conserver la provenance.

## Limites actuelles

Le résumé et les mots-clés sont générés à partir du nom, des caractéristiques techniques sélectionnées et de la correspondance externe. Sans transcription ni autre analyse du contenu, le bloc ne décrit pas les scènes ou l’intrigue. Il ne génère pas encore les titres de chapitres. Les artefacts restent attachés au résultat d’exécution; leur publication automatique à côté du média est à ajouter séparément.
