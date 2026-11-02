# TODO : Règle d'or de l'accélération matérielle et isolation des outils de conteneur

> **Destinataire** : Agent IA / Développeur  
> **Statut** : À prendre en charge (`HardwareCapabilities.RequiresHardwareAcceleration` existe dans `AiToolContracts.cs` ; le drapeau `IsHardwareAccelerated`, le rejet par le validateur de plugins et la restriction du résolveur ne sont pas codés)  
> **Priorité** : Haute  
> **Domaines** : Runtime Worker, Plugins, Store/Validation, Sécurité Landlock, Outils Média  
> **Documents de référence** : [cluster](../architecture/domains/cluster.md), [plugins](../architecture/domains/plugins.md) (Store d'outils gérés livré par le socle de l'ADR 0029), [auth-ops](../architecture/domains/auth-ops.md).

Le Store vérifie aujourd'hui l'archive et le hash de chaque exécutable, et le résolveur utilise le paquet partagé choisi pour le rôle. Ce contrôle d'intégrité ne qualifie pas à lui seul un binaire pour les pilotes/GPU présents sur un nœud. Les sondes synthétiques FFmpeg déjà exécutées sur le binaire résolu restent obligatoires ; les règles d'interdiction/réservation décrites ci-dessous restent à compléter et à valider sur du matériel réel.

---

## 1. Contexte & Règle d'or métier

### Règle absolue
> **Tout composant de flow, script ou plugin qui requiert ou utilise l'accélération matérielle (GPU, VA-API, Intel QSV, NVIDIA NVENC, Apple VideoToolbox, NPU/VPU, CUDA) DOIT OBLIGATOIREMENT utiliser l'exécutable fourni et qualifié par le conteneur (`Stock` ou environnement contrôlé du nœud hôte).**  
> **Il est formellement interdit à un plugin tiers d'embarquer ou de substituer un outil pour exécuter des tâches sous accélération matérielle.**

### Justification technique (Pourquoi cette règle est vitale)
1. **Couplage étroit aux pilotes de l'hôte et du conteneur** :
   - L'accélération matérielle repose sur des bibliothèques dynamiques système (`.so`), des sockets IPC (`/dev/dri`, NVIDIA Container Toolkit IPC) et des pilotes spécifiques installés dans l'image Docker `encoding` (`intel-media-va-driver-non-free`, `libvpl2`, `libmfx`, `mesa-va-drivers`, `ffnvcodec`).
   - Un binaire générique embarqué dans un `.zip` de plugin tiers est incapable de lier de manière fiable ces pilotes dynamiques hôtes (incompatibilités glibc, versions de libva différentes, symboles manquants).
2. **Garantie de non-régression et d'invariance** :
   - Si un plugin vidéo utilisait un binaire embarqué statique, l'encodage basculerait silencieusement sur le CPU ou planterait avec une erreur de bibliothèque dynamique, détruisant les performances du cluster et gaspillant des ressources CPU.
3. **Périmètre légitime des outils embarqués dans les plugins** :
   - Les outils embarqués dans les archives de plugins (`.zip`) sont **strictement réservés aux outils spécialisés purement logiciels (CPU)** sans pilotes matériels complexes (ex. `flac`, `mediainfo`, petits binaires CLI de parsing, remuxeurs légers, générateurs de sous-titres CPU).

---

## 2. Spécification technique & Comportement attendu

### A. Capacités matérielles protégées
Définir une liste canonique de capacités matérielles qui déclenchent le verrouillage conteneur :
```csharp
public static class HardwareCapabilities
{
    public static readonly ImmutableHashSet<string> HardwareAcceleratedCapabilities =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
            "nvenc", "nvdec", "cuda",
            "qsv", "vpl", "mfx",
            "vaapi", "vdpau",
            "amf", "videotoolbox", "vulkan", "npu");

    public static bool RequiresHardwareAcceleration(IEnumerable<string> capabilities) =>
        capabilities.Any(c => HardwareAcceleratedCapabilities.Contains(c));
}
```

### B. Contrôle à l'installation du plugin (`PluginPackageValidator`)
Lors de l'import ou du téléchargement d'un paquet `.zip` ([plugins](../architecture/domains/plugins.md)) :
1. Inspecter `plugin.json`.
2. Si un composant ou un outil déclaré dans le plugin prétend fournir ou requérir une capacité matérielle tout en pointant vers un binaire embarqué (`bin/` ou chemin relatif dans le zip) :
   - **Rejeter le paquet avec une erreur explicite** :
     `"PLUGIN_EMBEDDED_HARDWARE_TOOL_FORBIDDEN: L'accélération matérielle ({capability}) ne peut pas être fournie par un binaire embarqué. Le plugin doit déclarer la dépendance vers l'outil du conteneur (ex. requiredTools: ['ffmpeg'])."`

### C. Résolution d'exécution au runtime (`ToolResolver` / `RuntimeEligibility`)
Lorsqu'un nœud de flow s'exécute ou qu'un worker vérifie son éligibilité ([plugins](../architecture/domains/plugins.md)) :
1. Analyser les `RequiredCapabilities` du composant et de la configuration courante (ex: profile encodeur ciblant `nvenc`, `qsv` ou `vaapi`).
2. **Si l'opération requiert une accélération matérielle** :
   - Forcer le fournisseur vers **`Stock` (Conteneur)** ou **`Custom` Nœud validé par la mire synthétique matérielle**.
   - Ignorer/interdire formellement tout outil embarqué dans le plugin pour cette étape.
   - Si le conteneur n'a pas validé l'accélérateur matériel via `ToolCapabilityProbe` (mire synthétique échouée ou absente), échouer ou appliquer la politique stricte de repli CPU déclarée par le profil sans exécuter de binaire non qualifié.
3. **Si l'opération est purement logicielle (CPU)** :
   - Autoriser la cascade standard : Bloc > Outil embarqué du plugin > Outil conteneur.

### D. Confinement Landlock ([runtime](../architecture/domains/runtime.md))
- Les binaires embarqués dans les plugins (`/opt/encoding/custom-plugins/<plugin>/bin/`) n'ont aucun accès aux nœuds de périphériques `/dev/dri`, `/dev/nvidia*`.
- Seul le processus de référence exécutant les binaires système du conteneur dispose des descripteurs de périphériques matériels montés.

---

## 3. Plan de travail / Checklist pour l'IA exécutante

- [ ] **1. Contrats & Métadonnées (`Encoding.Contracts`)** :
  - Ajouter l'énumération / helper `HardwareCapabilities.RequiresHardwareAcceleration(capabilities)`.
  - Compléter `FlowToolRequirement` et `ToolDescriptor` avec le drapeau booléen calculé `IsHardwareAccelerated`.
- [ ] **2. Validateur de Paquet Plugin (`Encoding.Api / Plugins`)** :
  - Dans la validation de `plugin.json` (fiche plugins, Store), vérifier qu'aucun binaire embarqué ne revendique de capacités matérielles.
  - Ajouter les tests unitaires correspondants (`PluginPackageValidatorTests`).
- [ ] **3. Résolveur d'outils Runtime (`Encoding.Worker / Runtime`)** :
  - Dans la résolution de l'exécutable pour une étape (`ToolResolver` / `FlowStepExecutor`), interdire la substitution par un binaire plugin dès lors qu'un encodeur matériel est actif.
  - Vérifier que `ToolCapabilityProbe` reste le garant unique de l'accélération matérielle (fiches cluster et plugins).
- [ ] **4. Documentation Architecture** :
  - Inscrire cette règle dans la fiche [plugins](../architecture/domains/plugins.md) ; si les outils custom/managed sont repris, écrire un ADR dans `docs/architecture/adr/`.
- [ ] **5. Tests de validation automatisés** :
  - Test unitaire : Rejet d'un plugin tentant d'embarquer un binaire avec accélération `nvenc`/`vaapi`.
  - Test d'intégration : Un composant demandant `nvenc` utilise systématiquement le `ffmpeg` du conteneur même si un autre outil est présent dans le répertoire de travail.
