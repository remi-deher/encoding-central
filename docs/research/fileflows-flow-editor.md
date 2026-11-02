# Inspiration FileFlows pour les flows

## Portée

Étude de la documentation officielle de FileFlows, comparée au code actuel du projet. Aucune instance FileFlows n’a été installée ni testée. Les éléments ci-dessous sont des propositions de conception, pas des capacités déjà implémentées ou des décisions modifiant `AGENTS.md`.

## Observations vérifiées

- Catalogue latéral, ajout par glisser-déposer, configuration au double-clic, sorties nommées et traitement distinct des erreurs. FileFlows autorise plusieurs connexions vers une entrée, contrairement à notre première version. [Éditeur](https://fileflows.com/docs/webconsole/flows/editor).
- Les champs peuvent proposer les variables disponibles dans le contexte lors de la saisie. [Variables](https://fileflows.com/docs/variables/).
- Les modèles peuvent exposer des champs avec valeurs par défaut, obligations et conditions de visibilité. [Propriétés](https://fileflows.com/docs/webconsole/flows/properties).
- Le FFmpeg Builder accumule les intentions de traitement ; son exécuteur construit et lance la commande. [Builder](https://fileflows.com/docs/plugins/video-nodes/ffmpeg-builder/).
- Les sélecteurs audio utilisent notamment langue, codec et titre. Les actions peuvent indiquer qu’aucune piste ne correspond. [Audio Converter](https://fileflows.com/docs/plugins/video-nodes/ffmpeg-builder/audio-converter).
- Une entrée Video File réalise une analyse et fournit des variables média. [Entrée vidéo](https://fileflows.com/docs/plugins/video-nodes/video-file).
- Un sous-flow devient un composant réutilisable avec paramètres et sorties. [Sous-flows](https://fileflows.com/docs/webconsole/flows/sub-flow).

## Propositions pour notre application

### Éditeur partagé

Prévoir un catalogue qui reste accessible sur ordinateur, un canevas principal et un inspecteur de configuration de l’instance sélectionnée. Sur téléphone, les panneaux deviennent des tiroirs. La disposition appartient au composant/gabarit partagé, pas à la page métier.

Le double-clic ouvre l’inspecteur ; une action clavier et un bouton restent disponibles. Ajouter nom personnalisé, duplication, annuler/rétablir, zoom, cadrage, insertion sur une connexion et disposition automatique explicite. Préserver les positions manuelles lors d’une simple modification du graphe : aujourd’hui, `setGraph` relance Dagre à chaque ajout ou suppression.

Un bouton **Appliquer au flow** valide la configuration dans le brouillon en mémoire. Un bouton global **Enregistrer** persiste une version cohérente du document. Protéger les modifications non enregistrées au changement de page et afficher les erreurs de validation sur les blocs concernés.

### Configuration commune

Faire évoluer le schéma déclaratif et le formulaire partagé avec sections, aide, champs conditionnels, sélection multiple, unités et valeurs issues de variables typées. Introduire un sélecteur de pistes réutilisable : type, langue, codec, nom, canaux, indicateurs et choix première/toutes. Les identifiants spécifiques aux outils restent dans les adaptateurs.

Le choix d’un codec ou d’un mode change les champs proposés. Exemples : masquer le débit pour une sortie FLAC, adapter preset et qualité à l’encodeur, afficher les coordonnées seulement en mode recadrage par zone. Les dépendances et contraintes sont validées côté backend ; les champs cachés n’ajoutent pas de paramètres actifs incompatibles.

Le modèle actuel n’accepte que des valeurs scalaires (`string`, `number`, `boolean`, `null`). Les sélections de pistes et listes de règles nécessiteront un contrat structuré versionné, puis sa prise en charge par les formulaires. Ne pas accumuler des chaînes séparées par des virgules comme format métier définitif.

### Composants et traitement

Conserver des opérations simples, mais déclarer leur effet : lecture de métadonnées, préparation d’un plan, transformation de fichier ou production d’un résultat annexe. Les composants de préparation ne lancent pas automatiquement un nouveau processus média.

Pour FFmpeg, proposer **Préparer le traitement**, les actions de configuration vidéo/audio/pistes, puis **Exécuter le traitement**. Le worker traduit le plan structuré en arguments adaptés à ses capacités. Une extraction autonome ou une miniature peut rester une opération directe. MKVToolNix bénéficie également du regroupement des modifications compatibles dans une opération de remux ; les commandes incompatibles restent des opérations explicites.

Séparer les données source immuables, les métadonnées observées du fichier courant et les intentions du plan. Configurer un codec ne change pas les métadonnées observées avant l’exécution effective. Le scan initial et le scan de la sortie restent des garanties du moteur décrites par la décision 0006.

Déclarer les sorties pertinentes par composant : **Traité**, **Ignoré**, **Erreur**, ou **Oui/Non** pour une condition. Une erreur sans chemin de traitement échoue explicitement. Distinguer fin du chemin, validation du résultat et publication : une sortie non reliée ne publie pas automatiquement un fichier de bibliothèque.

### Réutilisation et contraintes

Envisager ensuite des sous-flows versionnés avec paramètres et sorties déclarés. Les modèles de création peuvent exposer seulement les réglages utiles, puis générer une définition éditable. Les exécutions et dépendances doivent référencer des versions précises.

Conserver les DAG et les boucles conteneurs avec sous-graphe. La décision 0007, prise après cette étude et l’analyse de l’exemple personnel, autorise désormais la reconvergence de chemins alternatifs vers un même nœud. Elle définit une sémantique séquentielle distincte du parallélisme.

Les plugins continuent de fournir un schéma de configuration, jamais du code Angular/HTML/JavaScript d’interface. L’exécution des plugins tiers reste dans un processus séparé et une tâche média reste attribuée à un worker compatible.

## Ordre proposé

1. Schéma de configuration structuré : sections, visibilité conditionnelle, aide et sélecteur de pistes ; formulaire partagé associé.
2. Inspecteur, nom d’instance et résumé des réglages sur les blocs ; positions préservées et disposition automatique à la demande.
3. Contrat de plan média et exécuteur, articulés avec scans et historique.
4. Persistance JSON réelle, versions et sauvegardes, protection du brouillon.
5. Variables contextuelles, sous-flows et modèles réutilisables.

La définition JSON, les sauvegardes et les scans sont actuellement documentés mais non implémentés dans le moteur. L’éditeur contient le catalogue déclaratif des actions ; son état reste en mémoire.

## Exemple personnel fourni : HEVC + AAC/AC3 + ASS Crunchyroll

Analyse statique de l’export `V3 - HEVC + AAC_AC3 + ASS Crunchyroll.json`, révision 41 : 30 instances de composants, dont 13 fonctions JavaScript. Les scripts sont examinés comme données de référence ; ils n’ont pas été exécutés et leurs dépendances Python externes n’ont pas été chargées.

L’entrée `FileFlows.VideoNodes.VideoFile` ne possède aucun port entrant et une sortie. Elle mène à **0. Pause lecture Plex (analyse sans verrou)**, puis **0b. Relevé des pistes d’origine**, **1. Sous-titres (préparation)** et **2. Audio (préparation)**. Le scan d’entrée précède donc les scripts préparatoires et l’encodage vidéo.

Le graphe prévoit une décision de réencodage, un chemin HEVC QSV et une branche CPU. L’exécuteur QSV contient une `ErrorConnection` explicite vers la branche CPU ; les connexions normales sont séparées dans `OutputConnections`. L’assemblage MKVMerge et les contrôles reçoivent plusieurs chemins alternatifs. Il existe aussi un terminal de report de deux minutes lorsque le disque est occupé, des relectures de métadonnées et des mises à jour Plex/Radarr/Sonarr.

Cet exemple suggère les évolutions suivantes pour notre application :

- Un composant d’entrée visible **Fichier vidéo** (puis équivalents image/audio/sous-titres), qui expose le média et ses métadonnées initiales. Son scan est obligatoire et assuré par le moteur ; supprimer un bloc d’analyse facultatif ne peut pas le désactiver.
- Des actions de script après l’entrée pour préparer variables, plans et règles avant les transformations. Elles utilisent le contexte média et passent par les services du moteur pour les accès aux fichiers, les outils et la publication.
- Des scripts réellement antérieurs au scan, si ce besoin apparaît, relèvent d’une phase d’admission distincte : ils disposent des informations de fichier et de tâche, pas encore des caractéristiques des pistes. Une modification de la source doit toujours préserver la référence initiale et les garanties d’intégrité.
- Le relevé des pistes et la comparaison avant/après deviennent des capacités de l’historique commun plutôt que des fichiers et lignes de journal produits séparément par chaque script.
- Le report libère le worker et reste durable. Les verrous/baux, la validation et la publication appartiennent au moteur partagé, même lorsqu’un script les demande.

La reconvergence visible dans cet export a motivé l’évolution de notre invariant initial d’une seule connexion entrante par nœud. La décision 0007 autorise plusieurs connexions vers le même port avec une sémantique de chemins alternatifs séquentiels, sans traitement parallèle ni barrière de synchronisation.

L’export illustre aussi une séparation utile : le type (`FlowElementUid`), l’identité d’instance (`Uid`), le nom personnalisé (`Name`), la configuration (`Model`) et les coordonnées du canevas. Notre format JSON pourra conserver cette séparation avec ses propres contrats et versions ; les scripts, chemins locaux et verrous de cet export ne seront pas importés automatiquement.
