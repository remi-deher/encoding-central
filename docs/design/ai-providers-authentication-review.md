# IA & fournisseurs — audit et proposition

Date : 18 octobre 2026. Statut : audit initial et proposition conservés pour référence. La décision a évolué : les deux CLI utilisent désormais leurs commandes natives de connexion, dont le parcours navigateur Claude `claude auth login`. L’implémentation actuelle est décrite dans [0071](../architecture/domains/ai.md).

## Diagnostic dans le dépôt

Le problème ne se corrige pas en remplaçant deux URL.

- `DeviceCodeAuthService.StartDeviceAuth` fabrique un code aléatoire et renvoie une URL fixe avant toute réponse de la CLI. Ce code n'est enregistré chez aucun fournisseur.
- Codex est lancé avec `auth login --device-code`, commande différente de la commande officielle.
- Les sorties stdout/stderr sont lues en entier mais jamais exploitées pour transmettre les instructions de connexion au navigateur. Les échecs de processus ne deviennent pas systématiquement des erreurs visibles ; le processus n'est pas arrêté à l'expiration.
- `CompleteAuthorization` écrit un marqueur `credentials.json`, sans preuve de connexion. Le bouton « Simuler validation » et l'endpoint `/auth/complete` peuvent ainsi produire un faux état connecté.
- `XDG_CONFIG_HOME` seul ne sélectionne pas les répertoires dédiés documentés pour ces deux outils. Le client d'inférence doit employer le même contexte d'identifiants que le login.
- `CliAiTextClient` utilise également des arguments Codex incorrects et retourne une réponse simulée comme un succès si l'exécutable est absent.
- La factory accepte les deux CLI comme moteurs, mais le sélecteur de la page ne propose qu'Ollama et OpenAI. Le moteur par défaut du backend peut pourtant être `codex-cli`.
- Le service d'authentification est exécuté côté API, avec un stockage local au serveur contacté ; ce n'est pas un enrôlement effectif sur tous les workers.

## Méthodes officielles vérifiées

| Fournisseur | Connexion | Contrôle local | Déconnexion |
| --- | --- | --- | --- |
| Codex CLI | `codex login --device-auth` | `codex login status` | `codex logout` |
| Claude Code | `claude auth login` | `claude auth status` | `claude auth logout` |

Codex documente un flux device ; l'application doit afficher les instructions réellement retournées par la CLI. Un statut local positif indique la présence d'identifiants, pas la garantie d'une requête distante réussie. Source : [référence officielle Codex](https://developers.openai.com/codex/cli/reference).

Claude documente un login navigateur et une autre voie pour les environnements automatisés : `claude setup-token`, puis injection du secret via `CLAUDE_CODE_OAUTH_TOKEN`. Le jeton imprimé n'est pas enregistré automatiquement. Il ne faut pas présenter à Claude le même écran de code court qu'à Codex sans flux documenté correspondant. Sources : [commandes Claude](https://code.claude.com/docs/en/cli-reference), [authentification et jetons pour automatisation](https://code.claude.com/docs/en/authentication).

Pour une première intégration Docker fiable, je recommande Codex par device auth et Claude par un jeton généré sur le poste de l'administrateur puis enregistré comme secret sur le nœud cible. L'intégration complète du login navigateur Claude nécessite de valider les interactions terminal/navigateur de la version livrée ; ne pas inventer de callback ou d'URL OAuth. La documentation décrit la génération de jeton pour les scripts, elle ne constitue pas une validation de tous les usages métier d'Encoding.

Utiliser `CODEX_HOME` pour le contexte Codex et `CLAUDE_CONFIG_DIR` pour le contexte Claude, identiques pendant connexion, vérification, inférence et déconnexion. Sources : [référence Codex](https://developers.openai.com/codex/cli/reference), [stockage Claude](https://code.claude.com/docs/en/authentication#credential-management).

## Mise en œuvre recommandée

1. Créer deux adaptateurs de connexion. Valider l'identifiant de fournisseur et la présence/version du binaire avant le démarrage. Retourner « outil absent » plutôt qu'une session fictive.
2. Pour Codex, lancer la commande officielle et lire les deux flux pendant son exécution, avec nettoyage ANSI, buffers bornés et extraction des seules instructions attendues. Fournir URL/code réels à l'UI ; ne pas exposer de sortie brute contenant des secrets. Si la version ne permet pas une extraction fiable, afficher une erreur explicite et proposer la commande sur le nœud.
3. Proposition historique remplacée par l'association navigateur `claude auth login`, décrite dans [0071](../architecture/domains/ai.md). L'ancienne voie `claude setup-token` n'est plus présentée dans l'interface.
4. Supprimer le bouton de simulation et l'endpoint de validation arbitraire en production. Remplacer les marqueurs historiques par « connexion à revérifier ». Ne déclarer la connexion réussie qu'après la fin du parcours et le contrôle de la CLI ; proposer séparément un test distant explicite, susceptible de consommer du quota.
5. Piloter expiration/annulation et arrêt de l'arbre de processus. Sérialiser les connexions par couple nœud/fournisseur ; reprendre proprement après un redémarrage, sans conserver une attente fictive. Les sessions doivent rester liées au nœud qui les exécute, même lorsque le navigateur contacte un autre serveur du cluster.
6. Protéger les dossiers de secrets (répertoire 0700, fichiers 0600 sur Linux), les conserver sur volume persistant et ne pas répliquer les secrets via le maillage. Déconnecter via l'outil et supprimer le secret local géré ; ne pas confondre déconnexion locale et révocation globale du compte chez le fournisseur.
7. Corriger l'inférence Codex avec `codex exec` et une sortie structurée ou le fichier de dernière réponse ; conserver `claude -p … --output-format text`. Utiliser un dossier de travail isolé et des permissions d'outils minimales. Retirer les réponses simulées du runtime de production.
8. Corriger l'architecture 0071 et les manifestes : elle décrit actuellement un device flow commun non établi et des commandes Codex incorrectes. Cette note reste une proposition jusqu'à l'implémentation.

Validation à prévoir avec des exécutables de test contrôlés : absence du binaire, URL/code reçus progressivement, refus, expiration, double démarrage, annulation, redémarrage, secret invalide, contexte partagé login/inférence, faux marqueur historique, déconnexion, absence de secrets dans les réponses et logs. Puis essai manuel avec les versions réellement distribuées et les comptes du propriétaire. Aucun compte réel n'a été connecté pendant cet audit.

## Proposition de page

Question principale : « Quel moteur vais-je utiliser, et est-il prêt ? »

Remplacer les grandes cartes et les deux formulaires simultanés par une liste compacte à gauche et une fiche contextuelle à droite. Une seule configuration apparaît à la fois. Sur mobile, la fiche s'ouvre sous l'élément sélectionné. Réutiliser les composants Material et les jetons du socle, avec surfaces tonales, focus visible et thèmes clair/sombre.

```text
Paramètres / Intelligence artificielle
IA & fournisseurs
Choisissez votre moteur et gérez ses accès.

Moteur par défaut   [ Ollama                         ▾ ]

FOURNISSEURS                     OLLAMA
┌───────────────────────────┐    Serveur local ou réseau
│ Ollama          Par défaut│
│ Configuration enregistrée │    Adresse du serveur
├───────────────────────────┤    [ http://…                       ]
│ OpenAI / API compatible   │    Modèle
│ À configurer              │    [ …                              ]
├───────────────────────────┤
│ Codex CLI                 │    [Enregistrer]
│ Connexion à vérifier      │
├───────────────────────────┤    ▸ Paramètres avancés
│ Claude Code               │      Délai maximal, lot de sous-titres
│ Non associé               │
└───────────────────────────┘
```

Les états ci-dessus sont des exemples de maquette, pas des observations du cluster.

- **Sélectionner** une ligne ouvre sa fiche ; cela ne change pas le moteur par défaut. Le sélecteur par défaut inclut les quatre moteurs effectivement acceptés par le backend, avec contrôle de disponibilité et enregistrement explicite.
- **Ollama** : adresse et modèle. **OpenAI/API compatible** : adresse, modèle et clé masquée ; champ vide conserve la clé existante, suppression explicite séparée.
- **Codex/Claude** : nœud concerné, état du binaire, méthode et état de connexion. Une action principale « Associer » ou « Vérifier la connexion » ; les actions secondaires et la déconnexion sont regroupées dans le menu de la fiche.
- **Avancé** : délai HTTP et taille des lots, dans une section commune repliée. Préciser la portée effective des réglages ; le client CLI impose actuellement son propre délai de 120 secondes.
- **Vérité des états** : « outil absent », « non associé », « autorisation en attente », « secret enregistré », « connexion à vérifier », « accès vérifié le… », « expiré », « échec ». Afficher le nœud et la date de dernière observation ; une ancienne vérification n'est pas un état temps réel.
- **Modale Codex** : préparation → URL et code reçus de la CLI → attente avec vérification automatique → résultat. Annuler arrête la tentative. Aucun bouton permettant de simuler un succès.
- **Fiche Claude** : instructions courtes de génération du jeton, champ secret et action d'enregistrement ; pas de faux code court. Les détails techniques ne sont visibles que dans cette étape où ils sont nécessaires.
- **Enregistrement** : action à portée claire dans la fiche, notification via `NotificationService`, protection des modifications non enregistrées à la navigation. Ne pas effacer une clé ou un brouillon lors d'un rafraîchissement.

Cette proposition réduit la densité tout en conservant les paramètres actuels. Elle n'ajoute ni jauges de consommation sans source réelle, ni promesse d'accès à un abonnement sur tous les nœuds.
