# Prototype de maillage à trois nœuds

Ce banc d’essai lance trois processus .NET sur la même machine et vérifie :

- la découverte des deux autres pairs par annonce UDP multicast signée, limitée au même identifiant de cluster ;
- l’acceptation d’un message de présence signé avec le secret du cluster, le rejet d’un secret incorrect et le rejet d’un rejeu ;
- la propagation des trois présences sur les trois processus, avec résolution déterministe par incarnation puis numéro de séquence.

Depuis ce dossier, exécuter `dotnet run -- test`. Le scénario crée une clé éphémère, choisit un identifiant de cluster, attend la découverte, vérifie les réponses HTTP et arrête tous les processus. Il nécessite une interface qui accepte le multicast IPv4. Le test Docker de développement utilisé dans l’environnement Codex est :

```sh
docker run --rm --network host -v "$PWD":/src -w /src/backend/tools/ClusterMeshPrototype \
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet run -- test
```

## Portée

C’est un prototype de protocole, sans stockage durable ni intégration au runtime Encoding. L’authentification utilise une clé HMAC partagée : elle vérifie l’appartenance au cluster, mais n’établit pas une identité individuelle résistante à un membre compromis. La découverte multicast est adaptée au LAN et ne traverse pas les réseaux routés. La présence expire après huit secondes sans mise à jour. Il n’y a ni consensus, ni quorum, ni réplication des décisions/configurations, ni garantie de livraison pendant une partition.

Avant toute intégration, remplacer la clé partagée par une identité de pair enrôlée (certificats mutuels ou clés individuelles), définir la rotation/révocation, persister les incarnations, et tester les partitions, redémarrages, expiration et réconciliation. Ce spike ne change pas le stockage actuellement utilisé par l’application.
