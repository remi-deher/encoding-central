# Vue d’ensemble

La page met les traitements au premier plan : quatre indicateurs groupés, trois exécutions actives au maximum, les nœuds, les points à examiner puis les cinq derniers traitements réussis. L’accès aux bibliothèques et la création de flows restent disponibles.

Les compteurs globaux viennent de `ExecutionTracking`. Les aperçus utilisent des requêtes filtrées sur `/api/v1/executions` (`running`, `cancelling`, `succeeded`) pour ne pas dépendre des 200 dernières entrées du journal global. Les listes sont actualisées toutes les dix secondes et par le bouton Actualiser ; une requête dépassant huit secondes est traitée comme un échec. Les dernières valeurs sont conservées et signalées comme potentiellement obsolètes en cas d’erreur. Le nombre restant à voir utilise le compteur backend, pas la longueur de la liste reçue.

Les nœuds affichent leur état de cycle de vie, leur matériel déclaré, la concurrence configurée et leur dernier signal. La concurrence configurée n’est pas une mesure de capacité disponible ; le matériel déclaré n’est pas de la télémétrie CPU/mémoire. Les nœuds hors ligne sont hachurés et portent un libellé explicite. Seuls les points demandant attention affichent une synthèse de santé.

Les statistiques de la maquette sur une période donnée et les économies d’espace ne sont pas exposées par l’API actuelle. La version intégrée présente donc les totaux serveur et les bibliothèques actives, sans estimer ces valeurs à partir d’une page partielle du journal. Aucun modèle backend ni migration n’est modifié.

L’état serveur reste la référence : une progression de 100 % n’indique pas une réussite. Les parcours Playwright couvrent les requêtes filtrées, les compteurs globaux, cet invariant, les liens, les pannes initiales et les actualisations en échec, ainsi que les deux thèmes sur ordinateur et mobile.
