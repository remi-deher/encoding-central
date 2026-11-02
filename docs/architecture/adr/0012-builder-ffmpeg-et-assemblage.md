---
id: 0012
title: Builder FFmpeg explicite et assemblage MKV atomique
status: implemented
domain: runtime
date: 2026-10-24
decided_by: Rémi (2026-10-24)
supersedes: []
superseded_by: []
tags: [ffmpeg, flows, métadonnées, checkpoint]
---

# 0012 — Builder FFmpeg explicite et assemblage MKV atomique

## Contexte
Les blocs directs peuvent produire plusieurs fichiers intermédiaires. Le modèle FileFlows demandé par Rémi prépare les réglages entre Start et Executor. Une condition doit ensuite observer le résultat réel, y compris avant une seconde conversion. Les scripts FileFlows fournis effectuent aussi des opérations autonomes : leurs effets ne peuvent pas être fusionnés implicitement.

## Décision
- Ajouter `base-ffmpeg-builder-start` et `base-ffmpeg-builder-execute`. Dans cette portée séquentielle, les réglages FFmpeg préparent uniquement le plan, indépendamment de leur ancien mode direct/plan. Le validateur contrôle chaque chemin, y compris les branches d'erreur ; pas de portée imbriquée, de script ni de mutation autonome à l'intérieur.
- Executor compose une commande FFmpeg, valide et scanne sa sortie, la dépose dans le stockage durable puis adopte simultanément fichier courant, métadonnées et résolution d'encodeur. Les métadonnées initiales restent immuables. Une sortie vide est `skipped` ; une erreur avec branche de secours conserve le dernier média valide et ferme le plan.
- Un relais explicite peut transporter la préparation ouverte et reprendre sur un autre worker. Les checkpoints durables et les garanties de publication restent obligatoires.
- Ajouter `base-assemble-media` pour traiter les sous-titres, joindre les polices et nommer les pistes d'un MKV avec une seule adoption finale. Les modifications d'en-têtes réutilisent seulement une sortie privée de cet assemblage, jamais la source ou le checkpoint d'entrée.
- Réutiliser le scan du média courant pour les lectures et la composition. Tout candidat est rescanné. Un contrôle explicite de décodage et la publication partagent le résultat seulement pour le même SHA-256 vérifié ; une reprise le refait. Une copie locale vérifiée remplace le second téléchargement de la même source.

## Alternatives écartées
- Fusionner automatiquement tout le graphe : conditions, scripts et outils différents rendent les effets ambigus.
- Supprimer les checkpoints entre opérations : perte de reprise et de relais entre workers.
- Remplacer les composants historiques : casse des versions publiées. Ils gardent leur comportement ; les nouveaux exigent base 1.9.0.

## Conséquences et risques
Aucune migration ni route nouvelle. Les workers anciens restent compatibles avec les anciens blocs mais ne reçoivent pas les nouveaux. Tests réels sur médias synthétiques : deux conversions, métadonnées en aval, secours après erreur, relais et publication, assemblage et préservation de source. Aucun gain chiffré annoncé : gros fichiers, GPU et NAS restent à mesurer. L'assemblage MKV reste distinct du builder FFmpeg ; extraction/conversion des sous-titres peut nécessiter ses propres processus.
