# Règles de programmation du backend

Ces règles sont obligatoires pour toute conception ou modification du backend. Leur objectif est d’éviter les implémentations dispersées, les comportements implicites et les changements difficiles à prévoir.

## 1. Partir du besoin métier

- Avant de coder, identifier le besoin métier, les acteurs concernés, les données de référence et les effets attendus.
- Définir les invariants et les états possibles avant d’implémenter un traitement.
- Une fonctionnalité doit avoir une responsabilité claire. Si elle couvre plusieurs besoins indépendants, la découper en responsabilités explicites.
- En cas d’ambiguïté métier, demander plutôt que choisir silencieusement un comportement.

## 2. Réutiliser les capacités communes

- Avant d’ajouter du code, rechercher les services, composants, bibliothèques, frameworks et mécanismes déjà présents dans le projet.
- Réutiliser la capacité existante ou l’étendre si elle couvre le même besoin.
- Ne pas réimplémenter une fonctionnalité déjà fournie par le projet ou une dépendance approuvée.
- Ne pas ajouter de code spécifique à un seul appelant lorsqu’un comportement commun est nécessaire.
- Toute capacité partagée doit avoir une responsabilité cohérente, une interface explicite et un emplacement commun documenté par les conventions du projet.
- N’introduire une nouvelle dépendance qu’après avoir vérifié qu’aucune capacité existante ne répond au besoin et que la dépendance convient aux contraintes du projet.
- Éviter les abstractions génériques prématurées : une abstraction commune doit répondre à plusieurs usages réels ou à un point d’extension explicitement prévu.

## 3. Séparer les responsabilités

- Les interfaces réseau valident le format des requêtes, l’authentification et les contrats d’entrée/sortie ; elles ne portent pas les règles métier.
- Les services métier appliquent les règles et coordonnent les cas d’usage.
- Le domaine porte les invariants et les transitions d’état métier.
- Les adaptateurs d’infrastructure encapsulent le stockage, les files de tâches, les systèmes de fichiers, les outils média et les services externes.
- Les plugins s’intègrent par des contrats et points d’extension communs ; ils ne contournent pas les règles centrales.
- Suivre les conventions et les frontières déjà établies dans le code environnant.
- Ces frontières sont vérifiées par `backend/tests/Encoding.Runtime.Tests/ArchitectureTests.cs` : références de projets, aucun type de stockage (`DbException`, SQL, `DbDataSource`) dans les endpoints, lancement de processus limité à une liste fermée, aucun accès fichier direct dans les endpoints (`FileResults`), plafond de 600 lignes par fichier : scinder en classes partielles ou en types distincts plutôt que d’ajouter une exception.
- Un endpoint traduit HTTP vers un service ; les conflits d’unicité passent par `UniqueConflict.GuardAsync`, la logique de pair maillé par `ClusterTaskPeerHandler`.

## 4. Centraliser les règles et les états

- Une règle métier a une seule définition faisant autorité.
- Ne pas dupliquer les décisions métier dans les contrôleurs, les workers, les plugins ou les interfaces.
- Définir explicitement les états, transitions, erreurs et conditions de reprise.
- Distinguer l’état observé, l’état souhaité et l’absence d’information.
- Ne pas déduire un état certain depuis une mesure ou un indicateur indirect.
- Les valeurs par défaut doivent être explicites, documentées et cohérentes entre les nœuds.

## 5. Définir des contrats explicites

- Définir des contrats métier stables pour l’API, les événements, les tâches et les plugins.
- Transmettre les objets métier selon un modèle partagé ; ne pas reconstruire des versions partielles et divergentes dans chaque couche.
- Valider les données aux frontières du système et retourner des erreurs structurées, cohérentes et exploitables.
- Versionner ou faire évoluer les contrats de façon compatible lorsque plusieurs nœuds peuvent être mis à jour à des moments différents.
- Ne pas exposer les détails internes de stockage ou d’exécution comme contrats publics par accident.

## 6. Rendre les opérations prévisibles et récupérables

- Définir le comportement en cas de répétition, d’expiration, de délai dépassé, d’annulation et de panne partielle.
- Les opérations distribuées doivent être idempotentes ou détecter explicitement les doublons.
- Les verrous, baux, nouvelles tentatives et délais doivent avoir des limites et des règles de libération explicites.
- Ne pas masquer une erreur, transformer un échec en succès ou appliquer une reprise silencieuse qui change le résultat métier.
- Les opérations sur les fichiers média doivent respecter les garanties d’intégrité définies dans `AGENTS.md`.
- Les secrets et jetons ne doivent pas apparaître dans les journaux ou les erreurs retournées aux clients.

## 7. Préserver les frontières du cluster

- Aucun nœud serveur ne doit être traité implicitement comme l’autorité permanente ou l’unique point de coordination.
- Les composants serveur doivent lire et modifier l’état partagé selon les mécanismes de cohérence définis par le projet.
- Les workers annoncent leurs capacités ; le répartiteur attribue uniquement les tâches compatibles avec ces capacités et les accès aux bibliothèques.
- Un plugin ou un worker ne peut pas déclarer une tâche terminée avant la validation attendue de son résultat.
- Les changements de configuration partagée doivent être cohérents et identifiables entre nœuds.

## 8. Maintenir le code modifiable

- Examiner les usages avant de modifier une interface ou un composant commun.
- Favoriser des fonctions et modules aux entrées, sorties et effets secondaires explicites.
- Garder les effets externes derrière des interfaces remplaçables afin que les règles métier restent indépendantes de l’outil concret.
- Supprimer le code devenu inutile après avoir vérifié qu’il n’est plus utilisé.
- Signaler les changements de comportement, les migrations nécessaires et les limites de validation.
- Documenter toute nouvelle décision structurante et mettre à jour ces règles si un principe évolue.
