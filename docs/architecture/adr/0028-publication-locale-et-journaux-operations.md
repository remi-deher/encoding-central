---
id: 0028
title: Publication locale quand worker et accès bibliothèque coïncident
status: implemented
domain: runtime
date: 2026-10-30
decided_by: Rémi (2026-10-30)
supersedes: []
superseded_by: []
tags: [publication, transfert, journal, performance, cluster]
---

# 0028 — Publication locale quand worker et accès bibliothèque coïncident

## Contexte

Un worker qui possède aussi l'accès direct à la bibliothèque produit l'artefact sur le même nœud que celui qui le publie. Le chemin générique réutilisait néanmoins le blob durable du serveur puis le téléchargeait de nouveau vers ce nœud. Les journaux de pipeline ne séparaient pas les commandes média des transferts, scans et écritures de publication.

## Décision

- L'artefact reste envoyé au stockage durable du serveur avant le checkpoint, pour permettre reprise et relais.
- Quand le worker et le nœud d'accès sont le même agent, le service de publication peut réutiliser le chemin local de l'artefact courant et éviter le téléchargement retour. Après redémarrage de l'agent, il revient au blob durable. La copie vers `.encoding-results`, son SHA-256, son flush et sa publication atomique restent obligatoires.
- Le pipeline conserve les commandes système et ajoute des entrées d'opération séparées, avec horodatages, état et volume d'octets. Il journalise les téléversements, téléchargements, scans et copie finale. Les opérations de publication distantes sont rattachées au bloc de publication actif.

## Alternatives écartées

- Remplacer le stockage durable par une référence au seul chemin local : rejeté, car une perte de worker casserait reprise et relais.
- Présenter une opération fichier comme une commande shell : rejeté, car cela confond processus externe et copie/transfert internes.

## Conséquences et risques

- Le nœud d'accès n'a plus à télécharger de nouveau le blob quand le worker local et le publisher partagent le même agent ; les échanges de contrôle et l'envoi durable initial subsistent.
- Le dossier de résultats reçoit toujours une copie complète du fichier lorsque le chemin de travail et le média sont distincts. Le protocole préserve la source et détecte les copies incomplètes.
- Les nouveaux champs de journal sont facultatifs et les anciens audits restent lisibles ; aucune migration SQL n'est nécessaire.
- Le gain de débit et l'atomicité sur NAS/TrueNAS restent à mesurer sur les hôtes réels.
