---
id: 0001
title: Réplication chiffrée des connexions IA
status: implemented
domain: ai
date: 2026-10-21
decided_by: Rémi (2026-10-21)
supersedes: []
superseded_by: []
tags: [ai, secrets, cluster]
---

# 0001 — Réplication chiffrée des connexions IA

## Contexte
Les connexions IA (Codex CLI, Claude Code, clé OpenAI) étaient locales à chaque nœud ([ai](../domains/ai.md)). Si le seul nœud connecté tombait, les flows IA des autres nœuds échouaient, et il fallait se reconnecter à la main sur chaque nœud.

## Décision
- Les trois connexions sont répliquées par la réconciliation HTTP, dans la table `ai_credentials` (migration 0044), comme les paramètres de plugins : chiffrées au repos par Data Protection du nœud, et par la clé de données du cluster dans le journal.
- `AiCredentialSyncService` (toutes les 20 s) compare la connexion locale à la dernière valeur synchronisée : un changement local (connexion, renouvellement, déconnexion) est publié, un changement distant est appliqué. Si les deux ont changé, la modification la plus récente l’emporte.
- Une déconnexion est propagée (valeur « supprimée »). Seuls des condensats sont gardés localement pour détecter les changements.
- Seule la clé OpenAI est répliquée côté réglages ; URL, modèles et activation restent propres à chaque nœud.

## Alternatives écartées
- Ne pas répliquer et se connecter nœud par nœud : aucune résilience si le nœud connecté est perdu.
- Ne pas transmettre les renouvellements Codex : si le jeton tourne à chaque renouvellement, la copie des autres nœuds devient périmée.

## Conséquences et risques
- La compromission d’un nœud expose désormais la connexion IA de tout le cluster (déjà vrai des jetons API et des secrets de plugins). Une révocation côté fournisseur reste nécessaire en cas de vol.
- Deux nœuds qui renouvellent Codex au même instant peuvent invalider une copie  : cela se règle en reconnectant un nœud. Le comportement réel du renouvellement Codex n’est pas validé ici : à tester sur les nœuds TrueNAS.
- Les anciennes versions ignorent la table (migration à appliquer sur tous les nœuds, mise à jour ensemble).
- Remplace l’invariant « aucun secret répliqué » de la fiche IA par « les secrets IA ne transitent que chiffrés ».
