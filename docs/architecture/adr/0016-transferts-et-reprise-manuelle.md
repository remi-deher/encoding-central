---
id: 0016
title: Transferts inter-serveurs et reprise manuelle sans quorum
status: implemented
domain: runtime
date: 2026-10-25
decided_by: Rémi (proposition D3–D5 et neutralisation explicite)
supersedes: []
superseded_by: []
tags: [propriété, transfert, checkpoint, partition, reprise, gossip]
---

# 0016 — Transferts inter-serveurs et reprise manuelle sans quorum

## Décision
Le propriétaire initial reste l'initiateur (ADR 0015). Un administrateur ou un bloc `nodes-handoff` en mode `server` peut céder une suite de flow. Un déplacement n'est jamais un claim distant : les baux et réservations restent locaux au propriétaire.

1. La source vérifie le rattrapage des serveurs, enregistre une proposition et **gèle** la tâche. Elle révoque le bail, augmente son époque et conserve le média réservé. Une invocation non rejouable inachevée, une attente ou une publication en cours rendent le checkpoint non transférable.
2. La cible vérifie auteur, propriété/époque précédente, capacités et accès direct local. Elle récupère les blobs déclarés, vérifie taille et SHA-256, puis confirme leur écriture durable. La réception reste **inactive** : aucun `execution_work` n'est créé. L'accusé signé lie identité, transfert, époque, preuve précédente et empreinte exacte de la proposition.
3. La source revérifie son gel et journalise l'activation signée (époque +1). Ce commit interdit de revenir à la propriété précédente. La cible importe le checkpoint et active son travail uniquement après cette preuve.

Un accusé perdu fait avorter la proposition et dégeler la source ; la cible reçoit l'abandon par le journal. Une activation perdue laisse la source cédée et la cible inactive jusqu'au rattrapage. Une source arrêtée avant l'accusé n'a pas cédé sa propriété. Les propositions sans activation après un arrêt brutal peuvent être abandonnées après 35 minutes ; un commit d'activation interdit cet abandon. Un relais de flow refusé trois fois devient un échec visible, sans repli silencieux.

Quand le serveur visé par un relais `mode=server` est déjà le serveur propriétaire de l'exécution (par exemple un worker rattaché à ce serveur), il n'y a rien à céder entre serveurs : l'étape est acceptée et le relais devient un relais vers un autre worker éligible du même serveur (correctif du 28/10/2026 ; l'ancien comportement refusait l'étape avec `cluster_refused`). Le plugin `nodes` 1.8.0 accepte `mode=server` et `targetServerPeerId`. Le relais conserve identité d'exécution, métadonnées originales/courantes, plan et checkpoint suivant. Les anciens workers ne reçoivent pas ce mode ; les modes worker/groupe/étiquettes restent compatibles. La cible doit disposer d'un agent local compatible avec accès direct lisible/inscriptible à la même bibliothèque. Sinon la réception est refusée et la source reste propriétaire. Les capacités sont revérifiées au claim.

## Checkpoints et fichiers
Les checkpoints signés sont des registres du maillage existant. Leur contenu est chiffré AES-GCM avec la clé du cluster et un domaine lié à l'exécution. Les réglages de plugins sont déchiffrés localement puis reprotégés par la cible : aucune copie de secrets DP inutilisables sur un autre serveur. La préparation d'un checkpoint fige les réglages avant son utilisation. `BeforeMetadata` reste l'origine ; `CurrentMetadata` décrit le blob courant.

La création d'une grosse file marque les checkpoints à préparer dans sa transaction, sans signer un document par fichier dans ce commit. Un service les prépare par lots, sous admissions SQLite distinctes. Claim et clôture d'un bloc sauvegardent aussi le checkpoint compatible. Les pairs mettent les artefacts en cache hors transaction SQLite ; transfert explicite et reprise refont les contrôles. Un résultat corrompu/incomplet ne donne aucun accusé.

La reprise demande un checkpoint **effectivement répliqué** et tous ses fichiers vérifiés. Une coupure avant réplication ou un stockage perdu produit un refus explicite ; une métadonnée seule ne rend aucun fichier disponible. Les compteurs de préparation/cache sont exposés à l'interface. Les caches durables augmentent l'espace occupé. La rétention protège propositions/réceptions en cours et résultats en quarantaine. Le cache d'un pair sans travail local n'est pas purgé automatiquement par la rétention des exécutions locales.

## Partition et doublons
Calculs possédés et publications distinctes continuent après le rattrapage initial. Déplacements, changements de fonctions serveur et retrait de serveur attendent les échanges authentifiés. La désinscription d'un simple worker sans bail garde la fusion LWW/tombstone : elle ne transfère aucune propriété de serveur. Le changement local par le lanceur utilise la même garde que la demande distante. Un serveur n'est pas retiré tant que des tâches possédées sont actives dans la vue locale.

Les créations concurrentes d'un média `(library_id, media_key)` sont ordonnées par mise en file UTC, propriétaire puis identifiant d'exécution. Les périodes d'exécution doivent se recouvrir ; une tentative créée après une fin connue n'est pas un doublon. La projection met le perdant en quarantaine et invalide ses baux. Ses résultats distincts restent conservés. Cette projection est recalculable à mesure que l'historique converge ; elle n'écrase jamais un média. Le verrou physique et les empreintes de l'ADR 0014 restent indispensables : connectivité et écriture fichier ne sont pas une transaction atomique.

Chaque serveur publie des observations signées : observateur, sujet, séquence et date. Elles expirent après 45 s et se relaient par le maillage. Ce sont des **indices**, même quand elles rapportent un pair joignable ; elles ne neutralisent personne et n'autorisent aucune reprise ou écriture source.

## Reprise manuelle à deux serveurs
Un administrateur atteste l'arrêt effectif de l'installation défaillante **et de ses workers**, avec une justification. L'attestation est signée et durable ; elle ne résulte jamais d'un timeout ou d'une observation. Délai minimal : 135 s (2 × bail 60 s + marge 15 s), depuis l'attestation, sans date fournie par le client.

La reprise utilise le checkpoint répliqué de l'époque courante, refuse un état terminal connu, vérifie capacités/accès/fichiers et émet une décision signée d'époque supérieure. Les flows pouvant avoir un effet externe non rejouable sont refusés : un ancien checkpoint ne prouve pas qu'un script n'a pas commencé juste après. Un travail rejouable effectué depuis le dernier checkpoint peut être refait ; il ne s'agit pas d'une reprise à chaud au milieu de FFmpeg.

Le serveur neutralisé est exclu des autorisations d'écriture source et sa file ne se réveille pas à son retour. Pour le réadmettre, l'auteur de l'attestation exige une réponse directe signée liée à un nonce, le rattrapage du vecteur du journal et l'absence de projections en attente. Les tâches d'époque supérieure restent au nouveau propriétaire. Une vieille attestation rejouée ne réactive pas une neutralisation levée.

À **trois serveurs ou plus**, calculs, réplication et transferts coopératifs gardent ce modèle. La neutralisation/reprise de cette version est refusée : témoin ou mini-consensus majoritaire dédié restent une extension future, conformément au non-objectif d'automatisation. Aucun consensus sur les données n'est réintroduit.

## Stockage et compatibilité
Migration additive 0056. **Locales** : inbox, propositions/réceptions, projections de checkpoints, neutralisations, observations, quarantaines, préparation et marqueurs de cache. **Réconciliés** : registres signés `tasks/v2/<event>` transportés par le journal existant. La projection lit toutes ses valeurs immuables, pas seulement le gagnant LWW d'une clé. Les événements en avance sur leur dépendance sont différés avec rotation de l'inbox.

L'échange du maillage ne change pas. Le canal de coordination/fichiers distinct exige admission RSA, signature, version, type d'opération et nonce de réponse. Les routes administratives restent fermées ; lectures de statut autorisées aux viewers. Un ancien serveur ne sachant pas recevoir n'acquiert aucune tâche par cette voie. L'historique d'un travail local possédé n'est pas écrasé par une projection distante.

## Validation et limites réelles
Tests avec deux serveurs HTTP en mémoire et deux fichiers SQLite : transfert, perte d'accusé, activation perdue/rattrapée, corruption, grâce, fichiers absents, retour/réadmission, déduplication, expiration des observations, ancien worker, encodage/remux et publication sur un média synthétique. Mesure opt-in à 7 000 tâches, deux serveurs logiques, deux workers simulés, lectures HTTP paginées et capture incrémentale concurrente.

Cela ne valide ni deux hôtes physiques, ni un NAS/NFS/SMB, ni la production. La création exclusive du verrou média et la durabilité du stockage partagé restent à y éprouver. Restaurer une ancienne base n'autorise jamais à contourner le rattrapage de propriété.
