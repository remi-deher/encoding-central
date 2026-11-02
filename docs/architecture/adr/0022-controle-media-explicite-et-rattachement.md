# ADR 0022 — Contrôle média explicite et prérequis de rattachement

- Statut : superseded
- Remplacé par : ADR 0027 pour la politique d’intégrité ; les décisions de rattachement et de permissions restent en vigueur.
- Date : 2026-10-27

## Contexte

Le décodage intégral systématique avant publication peut durer plusieurs minutes, même pour un remux rapide. Le réglage `verifyDecoding=false` du bloc historique ne supprimait pas ce contrôle. Des installations initialisées indépendamment peuvent aussi posséder des comptes homonymes distincts. Les droits déclarés d’un accès bibliothèque ne constituent pas des permissions effectives.

## Décision

Le bloc `probe-check-integrity` du plugin `probe` 1.1.0 rend explicite la recherche d’erreurs. Le mode `quick` vérifie l’identification, les pistes, une durée exploitable et trois passages représentatifs ; il ne garantit pas les octets non décodés entre ces passages. Le mode `deep` décode intégralement les flux vidéo et audio. `cpu` impose le CPU ; `gpu` exige un décodeur matériel vérifié sur le média courant et ne revient jamais silencieusement au CPU. `auto` peut essayer un GPU puis utiliser le CPU. Les annonces d’encodeurs ne sont pas considérées comme preuve de décodage : un essai explicite vérifie ce dernier sur le fichier exact. L’audio reste sur CPU.

La présence de ce bloc dans le flow active la politique explicite v1. Chaque résultat modifié doit alors avoir un contrôle réussi pour son SHA-256 exact avant publication. La preuve est aussi vérifiée côté serveur dans les checkpoints durables du bloc, pas seulement dans une variable modifiable par un script. Les reprises peuvent réutiliser une preuve du même résultat. Toute modification de l’empreinte invalide cette preuve. Le contrôle structurel, l’intégrité des transferts, la publication atomique et la politique de conservation/remplacement des sources restent obligatoires.

La règle initiale qui imposait le décodage intégral aux flows sans bloc est remplacée par l’ADR 0027 : flows historiques et nouveaux n’effectuent pas de décodage d’intégrité sans `probe-check-integrity`. Les versions publiées ne sont pas réécrites. Les anciens réglages `verifyDecoding` restent lisibles, mais sont ignorés. Les chemins qui contournent un bloc d’intégrité présent ne peuvent publier un résultat modifié sans preuve pour son empreinte exacte.

Les nouveaux flows exigent la capacité `media-integrity-policy/v1` sur tous les workers de leurs segments. Un serveur ou worker ancien ne doit pas exécuter une politique qu’il ne comprend pas. Le catalogue conserve les références `probe` 1.0.0 ; les champs de progression et de remontée des commandes sont optionnels. Les commandes en cours sont remontées avec le renouvellement du bail existant, bornées et liées à l’invocation ; un renouvellement tardif ne réécrit pas un audit terminé.

Avant rattachement, le cluster d’accueil signe une liste publique UUID/nom liée au cluster et au pair destinataire. Le nœud entrant valide la chaîne d’admission et cette signature. Une collision insensible à la casse bloque l’adoption locale, conserve le compte d’accueil et présente un renommage explicite du compte entrant. Le renommage conserve UUID, mot de passe, rôle et sessions ; une précondition sur l’ancien nom évite d’appliquer une décision périmée. Un cluster ancien qui ne fournit pas cette liste doit être mis à jour avant le rattachement d’une installation possédant des comptes. Aucun compte n’est fusionné.

Pour les permissions personnalisées, le mécanisme pris en charge est un point d’accès système propre au couple bibliothèque/nœud : montage ou ACL géré sur l’hôte, sélectionné par le chemin existant de l’accès direct. Le processus reste non privilégié ; l’application n’accorde pas de droits par une simple valeur UID/GID. Elle vérifie les opérations effectives dans un espace temporaire. Les groupes Docker restent globaux au processus ; l’isolation par bibliothèque doit venir des ACL ou montages de l’hôte. Aucune modification automatique récursive des médias ou de leurs ACL n’est introduite.

## Alternatives écartées

Rendre facultatif le contrôle des flows historiques sans signaler le changement de garantie a été écarté ; l’ADR 0027 tranche explicitement ce changement au niveau du projet. Conserver uniquement un champ UID/GID ne donnerait aucun droit système réel. Fusionner ou renommer automatiquement des comptes homonymes confondrait des identités ou supprimerait le choix de l’opérateur.

## Conséquences

La garantie rapide est plus faible que le décodage intégral et est explicitement présentée dans le catalogue. L’empreinte et la preuve durables empêchent de publier une autre version d’un fichier. Une exécution peut confirmer durablement sa publication puis échouer sur une intégration ; ce cas doit être affiché sans prétendre que la publication n’a jamais eu lieu.

Les tests utilisent des médias synthétiques et des bases locales isolées. Ils ne prouvent ni la disponibilité physique de tous les GPU, ni les ACL d’une installation TrueNAS, ni la cause d’un blocage historique qui n’est plus reproductible. Ces points restent des limites de validation sur l’environnement réel, sans autoriser un contournement des gardes.
