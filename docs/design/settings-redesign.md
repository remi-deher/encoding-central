# Paramètres : quatre domaines et deux lectures des journaux

La page Paramètres conserve le shell de l’application et utilise quatre domaines de premier niveau : Interface, Accès, Connexions et Exploitation. Les anciens liens `?tab=tokens`, `users`, `ai`, `mcp`, `webhooks`, `system`, `overview`, `logs` et `system-logs` ouvrent le domaine correspondant. Les changements de paramètres d’URL passent par la protection des modifications non enregistrées, y compris avec le retour arrière du navigateur.

## Configuration

`ConfigurationSectionComponent` est le conteneur commun : titre, description courte, actions de section et contenu. Les styles consomment les jetons de thème. La préférence d’apparence est distincte des diagnostics. Les métriques Prometheus sont regroupées avec l’exploitation, tandis que les webhooks restent dans Connexions.

Les comptes, jetons, fournisseurs IA et exemples de configuration MCP utilisent un seul `DetailDrawerComponent`. `SettingsEditor` synchronise les ouvertures avec la fin de la fermeture du panneau précédent ; un ancien événement d’animation ne peut pas fermer un nouvel éditeur. Les formulaires communs exposent leur état modifié au contrôleur de page. Une fermeture de formulaire modifié demande confirmation ; la fermeture extérieure est désactivée pendant une saisie modifiée ou un enregistrement. Les jetons complets et les secrets de configuration MCP sont effacés de l’éditeur à la fermeture.

Les listes des comptes et jetons activent `DataTableComponent.compactResources`. Cette option ajoute des conteneurs connectés, masque la barre de tri pour ces petites listes et regroupe les actions secondaires dans leur menu existant ; les autres catalogues conservent leur présentation par défaut et leurs confirmations. Les créations, modifications, changements de rôle, réinitialisations de mot de passe, révocations et suppressions restent raccordés aux services authentifiés existants. Le périmètre administrateur de la route n’a pas changé.

## Résumé et console

`SystemLogViewerComponent` reste partagé entre Paramètres et les fiches des nœuds. Il interroge les endpoints existants d’historique et de flux SSE, avec le même filtre de nœud. Aucune API ni persistance nouvelle n’est créée.

- **Résumé par défaut** : messages originaux, regroupement des répétitions exactes, nombre d’occurrences et dernière occurrence. La priorité est donnée aux erreurs et critiques, puis aux avertissements. Le bilan et le regroupement portent uniquement sur les événements effectivement chargés.
- **Console détaillée** : mêmes événements et filtres, dans l’ordre chronologique, avec messages et exceptions multilignes. Un groupe du résumé ouvre ses occurrences dans la console avec un repère visuel, sans retirer les événements de contexte.

Le regroupement utilise le niveau, la catégorie, le message, l’exception, le nœud et le code d’événement. Aucune reformulation, extraction heuristique ou traduction de texte libre n’est utilisée. Des incidents aux messages proches ou aux exceptions différentes restent séparés. Le contrat existant transmet les messages de journal et leurs exceptions ; il ne représente pas une capture octet pour octet des sorties stdout/stderr d’un processus.

Les filtres de service et niveau s’appliquent à l’historique et au flux. La recherche locale examine aussi les exceptions et identifiants. Les deux vues, la copie et les exports utilisent un unique ensemble filtré. TXT et copie conservent le format console ; JSON contient les entrées complètes et CSV échappe les cellules. La console permet une sélection de texte native et la copie d’un extrait. « Effacer l’affichage » ne supprime aucun journal côté serveur.

Les identifiants d’événement dédupliquent l’historique et les relectures SSE. Le tampon est borné ; la pause accumule un tampon lui aussi borné et le fusionne dans l’ordre chronologique à la reprise. Le changement de nœud ferme les abonnements précédents. Le défilement automatique s’interrompt lorsque l’utilisateur remonte ; une sélection de texte ne déclenche pas de défilement forcé. Une interruption du flux et une erreur d’historique sont présentées séparément des données déjà reçues.

## Validation

Les tests de modèles protègent le regroupement exact, les dates absolues, la déduplication, les bornes mémoire et la sérialisation console/CSV. Les tests Angular couvrent la navigation, les anciens liens, les modifications non enregistrées, les événements SSE et la fermeture des éditeurs. Les E2E utilisent les vrais composants avec des API contrôlées : quatre domaines, thèmes clair/sombre, mobile, accessibilité, édition, jetons à affichage unique, configuration des fournisseurs, résumé/console et fichiers exportés.
