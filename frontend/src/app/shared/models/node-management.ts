import { DetailCard } from '../ui/detail-card-grid.component';
import { formatMemory, nodeAcceleratorBadges } from './node-hardware';
import {
  ResourceSheetBadge,
  ResourceSheetItem,
  ResourceSheetMetric,
  ResourceSheetNotice,
  ResourceSheetSection,
} from '../ui/resource-sheet.component';
import { RecordFormData } from '../ui/record-form.component';
import { GuidedStep } from '../ui/guided-setup.component';
import { DiscoveredCluster, LocalNodePairing, LocalNodeStatus } from './local-node';
import { NodeModeStatus } from './node-mode';
import { NodeResponse } from './cluster';
import { ClusterReconciliationHealthStatus, ClusterTaskStatus } from '../services/cluster-api';

export const NODE_MANAGEMENT_TABS = [
  { value: 'node', label: 'Ce nœud' },
  { value: 'cluster', label: 'Cluster' },
  { value: 'groups', label: 'Groupes de nœuds' },
];
export const CREATE_CLUSTER_FORM: RecordFormData = {
  title: 'Créer un cluster',
  description: 'Ce nœud devient le premier membre du nouveau cluster.',
  submitLabel: 'Créer le cluster',
  reviewTitle: 'Créer ce cluster ?',
  fields: [
    {
      id: 'name',
      label: 'Nom du cluster',
      type: 'text',
      required: true,
      maxLength: 128,
      hint: 'Un nom reconnaissable pour toutes vos installations.',
    },
  ],
};

function informationCard(
  id: string,
  title: string,
  subtitle: string,
  items: readonly { label: string; description: string }[],
): DetailCard {
  return { id, title, subtitle, icon: 'server', tags: [], sections: [{ title: 'Détails', items }] };
}

/** Formate un délai écoulé ; une date invalide reste « indisponible », jamais zéro. */
export function formatLastSignal(
  value: string | null | undefined,
  now = Date.now(),
): string | null {
  const time = value ? new Date(value).getTime() : NaN;
  if (Number.isNaN(time)) return null;
  const seconds = Math.max(0, Math.floor((now - time) / 1000));
  if (seconds < 5) return 'À l’instant';
  if (seconds < 60) return `Il y a ${seconds} s`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `Il y a ${minutes} min`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `Il y a ${hours} h`;
  return new Intl.DateTimeFormat('fr-FR', { dateStyle: 'medium', timeStyle: 'short' }).format(
    new Date(time),
  );
}

export function runtimeEnvironmentLabel(value: string | null | undefined): string {
  switch (value?.toLowerCase()) {
    case 'docker':
      return 'Docker · conteneur';
    case 'podman':
      return 'Podman · conteneur';
    case 'container':
      return 'Conteneur';
    case 'native':
      return 'Natif';
    default:
      return 'Non renseigné';
  }
}

/** Quatre blocs de synthèse communs aux cartes des membres du cluster. */
export function memberNodeOverviewSections(
  node: NodeResponse,
  activeTasks: number | null,
  clusterName: string,
): readonly ResourceSheetSection[] {
  const online = node.status === 'online';
  const worker = node.roles.includes('worker');
  const inventory = node.inventory;
  const usableEncoders = node.capabilities.flatMap((tool) =>
    (tool.encoderChecks ?? [])
      .filter((check) => check.status === 'usable')
      .map((check) => check.encoderId.toLowerCase()),
  );
  const codecPatterns: readonly [string, readonly string[]][] = [
    ['H.264', ['h264_', 'libx264']],
    ['HEVC / H.265', ['hevc_', 'libx265']],
    ['AV1', ['av1_', 'libsvtav1', 'libaom-av1']],
  ];
  const codecs = codecPatterns
    .filter(([, patterns]) =>
      usableEncoders.some((encoder) => patterns.some((pattern) => encoder.includes(pattern))),
    )
    .map(([label]) => ({ label, tone: 'success' as const }));
  const accelerators = worker
    ? nodeAcceleratorBadges(node).map((label) => ({
        label,
        tone: label === 'CPU' ? ('neutral' as const) : ('success' as const),
      }))
    : [{ label: 'Non applicable', tone: 'neutral' as const }];
  const taskMetric = !worker
    ? { label: 'Traitements actifs', value: 'Non applicable', unavailable: true }
    : activeTasks === null || !online
      ? {
          label: 'Traitements actifs',
          value: 'Indisponible',
          unavailable: true,
          detail: `${node.maxConcurrentTasks} traitement(s) simultané(s) au maximum`,
        }
      : {
          label: 'Traitements actifs',
          value: `${activeTasks} / ${node.maxConcurrentTasks}`,
          detail: `${node.maxConcurrentTasks} place(s) de traitement simultané`,
        };

  return [
    {
      id: 'state',
      title: 'État et activité',
      items: node.roles.map((role) => ({
        label: role === 'server' ? 'Serveur' : 'Worker',
        badges: [{ label: online ? 'Actif' : 'Injoignable', tone: online ? 'success' : 'warning' }],
      })),
      metrics: [
        taskMetric,
        {
          label: 'Dernier signal',
          value: formatLastSignal(node.lastSeenAt) ?? 'Indisponible',
          unavailable: !node.lastSeenAt,
        },
      ],
    },
    {
      id: 'hardware',
      title: 'Matériel déclaré',
      fields: inventory
        ? [
            {
              label: 'Processeur',
              value: inventory.processorModel?.trim() || `${inventory.processorCount} cœurs`,
              detail: `${inventory.processorCount} cœurs logiques`,
            },
            { label: 'Mémoire', value: formatMemory(inventory.memoryMb) },
            {
              label: 'Système',
              value: `${inventory.operatingSystem} · ${inventory.architecture}`,
            },
            {
              label: 'Environnement',
              value: runtimeEnvironmentLabel(inventory.runtimeEnvironment),
            },
            {
              label:
                inventory.gpus?.length === 1 ? 'Processeur graphique' : 'Processeurs graphiques',
              value: inventory.gpus?.length
                ? inventory.gpus.map((gpu) => gpu.name).join(' · ')
                : inventory.gpus
                  ? 'Aucun détecté'
                  : 'Non déclaré',
            },
          ]
        : [{ label: 'Matériel', value: 'Non renseigné' }],
    },
    {
      id: 'capabilities',
      title: 'Capacités de traitement',
      notice: !worker
        ? 'La fonction worker est désactivée.'
        : node.capabilities.length
          ? undefined
          : 'Aucune capacité déclarée.',
      items: [
        {
          label: 'Moteur d’accélération',
          badges: accelerators,
          detail: accelerators.some((item) => item.label === 'CPU')
            ? 'Traitements assurés par le processeur principal.'
            : undefined,
        },
        {
          label: 'Codecs compatibles',
          badges: codecs.length ? codecs : [{ label: 'Aucun codec validé', tone: 'neutral' }],
        },
      ],
    },
    {
      id: 'identity',
      title: 'Identité et appartenance',
      fields: [
        { label: 'Identifiant', value: node.id, mono: true },
        { label: 'Nom technique', value: node.name },
        { label: 'Cluster', value: clusterName },
      ],
    },
  ];
}

export interface LocalNodeSheetInput {
  readonly status: LocalNodeStatus | null;
  readonly node: NodeResponse | null;
  readonly mode: NodeModeStatus | null;
  readonly standalone: boolean;
  readonly clusterName: string | null;
  readonly agentUnavailable: boolean;
  /** Tâches actives observées ; `null` tant que la source n'a pas répondu. */
  readonly activeTasks: number | null;
  readonly now?: number;
}

/**
 * Fiche du nœud local, fondée uniquement sur les états observés : une fonction inconnue n'est jamais présentée
 * comme inactive, et une valeur périmée est identifiée comme dernière valeur connue.
 */
export function localNodeSheet(input: LocalNodeSheetInput): {
  readonly title: string;
  readonly subtitle: string;
  readonly notices: readonly ResourceSheetNotice[];
  readonly sections: readonly ResourceSheetSection[];
} {
  const { status, node, standalone, agentUnavailable } = input;
  const roles =
    input.mode ??
    (status
      ? { server: status.roles.includes('server'), worker: status.roles.includes('worker') }
      : null);
  const desired = node?.desiredRoles ?? null;
  const roleBadges = (role: 'server' | 'worker'): readonly ResourceSheetBadge[] => {
    const enabled = roles ? roles[role] : undefined;
    const badges: ResourceSheetBadge[] =
      enabled === undefined
        ? [{ label: 'État indisponible', tone: 'warning' }]
        : agentUnavailable
          ? [{ label: `Dernier état connu : ${enabled ? 'actif' : 'inactif'}`, tone: 'warning' }]
          : [{ label: enabled ? 'Actif' : 'Inactif', tone: enabled ? 'success' : 'neutral' }];
    if (desired && desired.includes(role) !== (enabled ?? false))
      badges.push({
        label: `Demandé : ${desired.includes(role) ? 'actif' : 'inactif'}`,
        tone: 'accent',
      });
    return badges;
  };
  const workerOff = roles?.worker === false;
  const max = status?.maxConcurrentTasks ?? node?.maxConcurrentTasks ?? null;
  const processing: ResourceSheetMetric = workerOff
    ? {
        label: 'Traitements actifs',
        value: 'Non applicable',
        unavailable: true,
        detail: 'La fonction worker est désactivée.',
      }
    : input.activeTasks === null || max === null
      ? {
          label: 'Traitements actifs',
          value: 'Indisponible',
          unavailable: true,
          detail: max === null ? undefined : `${max} traitement(s) simultané(s) au maximum`,
        }
      : {
          label: 'Traitements actifs',
          value: `${input.activeTasks} / ${max}`,
          stale: agentUnavailable,
          detail: agentUnavailable
            ? 'Dernière valeur connue, pas une occupation actuelle'
            : undefined,
          meter: {
            value: input.activeTasks,
            max,
            label: `${input.activeTasks} places de traitement occupées sur ${max}`,
            stale: agentUnavailable,
          },
        };
  const signal = formatLastSignal(node?.lastSeenAt, input.now);
  const inventory = node?.inventory ?? status?.inventory ?? null;
  const gpus = inventory?.gpus ?? [];
  const tools = status?.capabilities ?? [];
  const notices: ResourceSheetNotice[] = [];
  if (agentUnavailable)
    notices.push({
      tone: 'danger',
      title: 'L’agent de ce nœud ne répond pas',
      description:
        'Les informations ci-dessous sont les dernières connues ; elles peuvent ne plus être exactes.',
    });
  if (status?.switchFailure)
    notices.push({
      tone: 'danger',
      title: 'Rattachement annulé',
      description: status.switchFailure,
    });
  if (
    desired &&
    roles &&
    (desired.includes('server') !== roles.server || desired.includes('worker') !== roles.worker)
  )
    notices.push({
      tone: 'accent',
      title: 'Changement de fonctions en attente',
      description: 'Les fonctions appliquées ne changent qu’après confirmation du nœud.',
    });
  const sections: ResourceSheetSection[] = [
    {
      id: 'state',
      title: 'État et activité',
      caption: agentUnavailable ? 'Agent injoignable' : 'Agent joignable',
      items: [
        {
          label: 'Serveur',
          badges: roleBadges('server'),
        },
        {
          label: 'Worker',
          badges: roleBadges('worker'),
        },
      ],
      metrics: [
        processing,
        signal
          ? { label: 'Dernier signal', value: signal, stale: agentUnavailable }
          : {
              label: 'Dernier signal',
              value: 'Indisponible',
              unavailable: true,
              detail: node ? undefined : 'Le suivi du nœud est assuré par le serveur du cluster.',
            },
      ],
    },
    {
      id: 'hardware',
      title: 'Matériel déclaré',
      metrics: inventory
        ? [
            {
              label: 'Processeur',
              value: inventory.processorModel?.trim() || `${inventory.processorCount} cœurs`,
              detail: `${inventory.processorCount} cœurs logiques · ${formatMemory(inventory.memoryMb)} de mémoire installée · ${inventory.operatingSystem} · ${inventory.architecture}`,
            },
            gpus.length
              ? {
                  label: gpus.length > 1 ? 'Processeurs graphiques' : 'Processeur graphique',
                  value: gpus.map((gpu) => gpu.name).join(' · '),
                  detail:
                    gpus
                      .map((gpu) =>
                        [gpu.device, gpu.driver ? `pilote ${gpu.driver}` : null]
                          .filter(Boolean)
                          .join(' · '),
                      )
                      .filter(Boolean)
                      .join(' ; ') || undefined,
                }
              : {
                  label: 'Processeur graphique',
                  value: inventory.gpus ? 'Aucun détecté' : 'Non déclaré',
                  unavailable: true,
                  detail: inventory.gpus ? undefined : 'Cet agent ne remonte pas encore ses GPU.',
                },
            { label: 'Mémoire', value: formatMemory(inventory.memoryMb) },
            {
              label: 'Système',
              value: `${inventory.operatingSystem} · ${inventory.architecture}`,
            },
            {
              label: 'Environnement',
              value: runtimeEnvironmentLabel(inventory.runtimeEnvironment),
            },
          ]
        : [
            {
              label: 'Matériel',
              value: 'Non renseigné',
              unavailable: true,
              detail: 'Disponible après le prochain signal du nœud.',
            },
          ],
    },
    {
      id: 'capabilities',
      title: 'Capacités de traitement',
      notice: workerOff
        ? 'La fonction worker est désactivée : aucun outil n’est déclaré.'
        : tools.length
          ? undefined
          : 'Aucun outil déclaré.',
      items: workerOff
        ? []
        : (() => {
            const items: ResourceSheetItem[] = [];
            const ffmpeg = tools.find((t) => t.toolId.toLowerCase() === 'ffmpeg');
            const usableChecks = new Set(
              ffmpeg?.encoderChecks
                ?.filter((c) => c.status === 'usable')
                .map((c) => c.encoderId.toLowerCase()) ?? [],
            );
            const rawEncoders = new Set((ffmpeg?.encoders ?? []).map((e) => e.toLowerCase()));
            const hardwareAccels = (ffmpeg?.accelerationTypes ?? []).map((a) => a.toUpperCase());

            // 1. Moteur d'accélération
            const motorBadges: ResourceSheetBadge[] = [];
            if (hardwareAccels.length > 0) {
              motorBadges.push({ label: hardwareAccels.join(' · '), tone: 'success' });
              if (gpus.length > 0 && gpus[0].name) {
                motorBadges.push({ label: gpus[0].name, tone: 'neutral' });
              }
            } else {
              motorBadges.push({ label: 'Encodage logiciel CPU', tone: 'neutral' });
            }
            items.push({
              label: 'Moteur d’accélération',
              badges: motorBadges,
              detail:
                hardwareAccels.length > 0
                  ? undefined
                  : 'Traitements assurés par le processeur principal',
            });

            // 2. Codecs vidéo clés pris en charge
            const codecBadges: ResourceSheetBadge[] = [];
            // AV1
            const av1Hw = ['av1_nvenc', 'av1_qsv', 'av1_vaapi'].some((id) => usableChecks.has(id));
            const av1Sw = ['libsvtav1', 'libaom-av1'].some(
              (id) => usableChecks.has(id) || rawEncoders.has(id),
            );
            if (av1Hw) codecBadges.push({ label: 'AV1 (Matériel)', tone: 'success' });
            else if (av1Sw) codecBadges.push({ label: 'AV1 (CPU)', tone: 'neutral' });

            // HEVC / H.265
            const hevcHw = ['hevc_nvenc', 'hevc_qsv', 'hevc_vaapi'].some((id) =>
              usableChecks.has(id),
            );
            const hevcSw = usableChecks.has('libx265') || rawEncoders.has('libx265');
            if (hevcHw) codecBadges.push({ label: 'HEVC / H.265 (Matériel)', tone: 'success' });
            else if (hevcSw) codecBadges.push({ label: 'HEVC / H.265 (CPU)', tone: 'neutral' });

            // H.264
            const h264Hw = ['h264_nvenc', 'h264_qsv', 'h264_vaapi'].some((id) =>
              usableChecks.has(id),
            );
            const h264Sw = usableChecks.has('libx264') || rawEncoders.has('libx264');
            if (h264Hw) codecBadges.push({ label: 'H.264 (Matériel)', tone: 'success' });
            else if (h264Sw) codecBadges.push({ label: 'H.264 (CPU)', tone: 'neutral' });

            if (codecBadges.length > 0) {
              items.push({
                label: 'Codecs vidéo pris en charge',
                badges: codecBadges,
              });
            }

            return items;
          })(),
      action: workerOff || !node ? undefined : { id: 'capabilities', label: 'Détails techniques' },
    },
    {
      id: 'identity',
      title: 'Identité et appartenance',
      fields: [
        { label: 'Nom technique', value: status?.name ?? 'Indisponible' },
        { label: 'Identifiant du nœud', value: status?.nodeId ?? 'Indisponible', mono: true },
        {
          label: 'Cluster',
          value: standalone
            ? 'Aucun (nœud autonome)'
            : (input.clusterName ?? status?.clusterName ?? 'Cluster'),
        },
      ],
    },
  ];
  return {
    title: node?.displayName?.trim() || status?.name || 'Ce nœud',
    subtitle: standalone
      ? 'Nœud autonome'
      : `Membre de ${input.clusterName ?? status?.clusterName ?? 'son cluster'}`,
    notices,
    sections,
  };
}

export const AUTONOMOUS_CARDS: readonly DetailCard[] = [
  {
    ...informationCard(
      'autonomous-node',
      'Votre installation reste utilisable',
      'Aucun rattachement obligatoire',
      [
        {
          label: 'Un nœud autonome',
          description:
            'Gérez les fonctions et les capacités de cette installation depuis l’onglet Ce nœud.',
        },
        {
          label: 'Une configuration partagée quand vous le souhaitez',
          description:
            'Un cluster permet de gérer plusieurs nœuds et de leur répartir les traitements.',
        },
      ],
    ),
    icon: 'system',
  },
];

export function clusterAddressForm(address: string): RecordFormData {
  return {
    title: 'Adresse du cluster',
    description: '',
    submitLabel: 'Vérifier la connexion',
    review: false,
    fields: [
      {
        id: 'address',
        label: 'Adresse du cluster',
        type: 'text',
        required: true,
        hint: 'Exemple : http://192.168.1.51:8090',
      },
    ],
    initialValues: { address: address },
  };
}

interface JoinPresentation {
  readonly addressForm: RecordFormData;
  readonly target: DiscoveredCluster | null;
  readonly status: LocalNodeStatus | null;
  readonly pairing: LocalNodePairing | null;
  readonly clusters: readonly DiscoveredCluster[];
  readonly localServerActive: boolean;
  readonly canChoose: boolean;
}

export function clusterJoinSteps(data: JoinPresentation): readonly GuidedStep[] {
  const name = data.target?.name ?? data.status?.clusterName ?? 'le cluster d’accueil';
  const expires =
    data.pairing?.expiresAt ?? data.status?.pairingExpiresAt ?? data.status?.switching?.deadline;
  const expiry = expires
    ? new Intl.DateTimeFormat('fr-FR', { dateStyle: 'short', timeStyle: 'short' }).format(
        new Date(expires),
      )
    : null;
  const code = data.status?.verificationCode ?? data.pairing?.verificationCode;
  return [
    {
      id: 'address',
      label: 'Choisir le cluster',
      description:
        'Choisissez un cluster détecté ou saisissez l’adresse d’un nœud serveur du cluster à rejoindre.',
      ...(data.clusters.length
        ? {
            choices: {
              columns: [
                { key: 'name', label: 'Cluster' },
                { key: 'address', label: 'Adresse' },
                { key: 'state', label: 'Disponibilité' },
              ],
              rows: data.clusters.map((cluster) => ({
                id: cluster.id,
                name: cluster.name,
                address: cluster.apiBaseUrl,
                state: cluster.reachable === false ? 'Ne répond plus' : 'À vérifier',
                manual: cluster.source === 'manual',
              })),
              actions: [
                { id: 'forget', label: 'Oublier', visibleWhen: (row) => row['manual'] === true },
              ],
            },
          }
        : {}),
      form: data.addressForm,
      note: data.canChoose
        ? 'La vérification contacte ce cluster. Aucune demande de rattachement n’est encore envoyée.'
        : 'La fonction worker est indisponible. Activez-la depuis Ce nœud ou rétablissez sa connexion avant de continuer.',
    },
    {
      id: 'identity',
      label: 'Vérifier et confirmer',
      description:
        'Le cluster répond. Comparez son nom et son identifiant avec ceux indiqués sur le serveur d’accueil.',
      cards: data.target
        ? [
            informationCard('join-target', data.target.name, 'Cluster joignable', [
              { label: 'Adresse', description: data.target.apiBaseUrl },
              { label: 'Identifiant du cluster', description: data.target.id },
            ]),
          ]
        : [],
      note: data.localServerActive
        ? 'Après approbation, le serveur local reste actif. Les configurations et états convergent avec le cluster par le maillage SQLite. Les fichiers médias et résultats ne sont pas répliqués et doivent rester accessibles selon les accès configurés. La configuration locale n’est pas importée automatiquement. Un changement en cours peut être annulé avant l’approbation.'
        : 'Le cluster d’accueil doit approuver votre demande. Les accès aux bibliothèques et les capacités de ce nœud restent nécessaires pour recevoir des traitements.',
      actions: [
        { id: 'back', label: 'Modifier l’adresse' },
        { id: 'request', label: 'Envoyer la demande', primary: true },
      ],
    },
    {
      id: 'waiting',
      label: 'Approbation',
      description: `Demande envoyée à ${name}. Un administrateur doit la vérifier depuis Nœuds, puis Cluster, puis Demandes de rattachement.`,
      heroCode: code
        ? {
            code,
            label: 'Code à comparer',
            helper: `Un administrateur du cluster ${name} doit confirmer ce code identique sur son écran.`,
            badge: 'En attente d’approbation',
            tone: 'accent',
          }
        : undefined,
      cards: [
        informationCard(
          'join-pending',
          'En attente d’approbation',
          'Le rattachement précédent est conservé pendant la vérification',
          [
            { label: 'Nœud demandeur', description: data.status?.name ?? 'Ce nœud' },
            ...(code
              ? []
              : [{ label: 'Code à comparer', description: 'Lecture du code de vérification…' }]),
            ...(expiry ? [{ label: 'Demande valable jusqu’au', description: expiry }] : []),
            ...(data.pairing?.pairingKey || data.status?.pairingKey
              ? [
                  {
                    label: 'Clé de jumelage · méthode secondaire',
                    description: data.pairing?.pairingKey ?? data.status!.pairingKey!,
                  },
                ]
              : []),
          ],
        ),
      ],
      note: 'L’approbation peut arrêter cette interface locale. En cas de perte de connexion, consultez le cluster d’accueil pour connaître le résultat ; une déconnexion ne prouve pas une réussite.',
      actions: [
        { id: 'later', label: 'Fermer et suivre plus tard' },
        { id: 'cancel', label: 'Annuler la demande' },
      ],
    },
    {
      id: 'done',
      label: 'Rattachement terminé',
      description: `Le nœud a confirmé son rattachement à ${name}.`,
      cards: [
        informationCard('join-complete', `Membre de ${name}`, 'Rattachement confirmé par le nœud', [
          {
            label: 'Traitement des bibliothèques',
            description:
              'Le cluster attribue les tâches selon les capacités et les accès aux médias déclarés pour ce nœud.',
          },
        ]),
      ],
      actions: [{ id: 'finish', label: 'Voir le cluster', primary: true }],
    },
  ];
}

export interface ReconciliationBannerPresentation {
  readonly title: string;
  readonly description: string;
  readonly isAlert: boolean;
}

export function reconciliationBannerPresentation(
  status: ClusterReconciliationHealthStatus | null,
  serversCount: number,
  isReady: boolean = true,
  tasks?: ClusterTaskStatus | null,
): ReconciliationBannerPresentation {
  const relayed =
    tasks?.observations.filter((observation) => observation.observerPeerId !== tasks.localPeerId)
      .length ?? 0;
  const hints = relayed
    ? ` ${relayed} observation(s) de présence relayée(s), utilisées comme indices uniquement.`
    : '';
  if (tasks?.neutralized) {
    return {
      title: 'Serveur neutralisé — reprise suspendue',
      description:
        'Ce serveur doit rattraper son journal puis être réadmis par un administrateur. Les tâches cédées restent à leur nouveau propriétaire.',
      isAlert: true,
    };
  }
  if (status?.projections?.length) {
    const oldest = status.projections[0];
    const causes: Record<string, string> = {
      immutable_snapshot_conflict: 'Instantané immuable divergent',
      username_conflict: 'Collision de nom de compte',
      dependency_missing: 'Dépendance manquante',
      constraint_conflict: 'Conflit de données',
      storage_unavailable: 'Stockage indisponible',
    };
    return {
      title: 'Projections du cluster bloquées',
      description: `${status.projections.length} événement(s) en échec. ${causes[oldest.cause] ?? 'Projection impossible'} dans ${oldest.table}, depuis ${oldest.firstFailedAt}. ${oldest.action}`,
      isAlert: true,
    };
  }
  if (status?.attributionBlockers?.length) {
    const reasons = status.attributionBlockers.map((code) => {
      if (code.startsWith('peer_catch_up_unconfirmed:')) {
        return `Rattrapage sortant non confirmé pour le pair ${code.split(':')[1]}`;
      }
      return (
        (
          {
            pending_entity_projections: 'Projections des données en attente',
            pending_ownership_events: 'Événements de propriété en attente',
            pending_coordination_events: 'Événements de coordination en attente',
            local_peer_neutralized: 'Serveur local neutralisé',
          } as Record<string, string>
        )[code] ?? 'Prérequis du cluster non satisfait'
      );
    });
    return { title: 'Attribution suspendue', description: reasons.join('. '), isAlert: true };
  }
  if (tasks?.isolated) {
    return {
      title: 'Serveur isolé — travail possédé conservé',
      description: `Les calculs et publications sans écrasement continuent après le rattrapage initial. Les transferts et modifications des sources attendent la réconciliation. ${tasks.waitingSourceWrites} opération(s) source en attente. Un pair injoignable ne peut pas être repris automatiquement.${hints}`,
      isAlert: true,
    };
  }
  if (tasks?.catchingUp) {
    return {
      title: 'Rattrapage du journal en cours',
      description:
        'Les transferts et modifications des sources attendent la réconciliation complète. Au redémarrage, les anciennes tâches restent suspendues pendant ce rattrapage.',
      isAlert: false,
    };
  }
  if (tasks && (tasks.waitingSourceWrites || tasks.pendingTransfers)) {
    return {
      title: 'Coordination des traitements en cours',
      description: `${tasks.waitingSourceWrites} opération(s) source en attente ; ${tasks.pendingTransfers} transfert(s) en préparation ou réception inactive. Une réception ne permet pas d’exécuter la tâche avant son activation.${hints}`,
      isAlert: true,
    };
  }
  if (
    tasks &&
    (tasks.pendingCheckpointPreparations ?? 0) + (tasks.pendingCheckpointCaches ?? 0) > 0
  ) {
    return {
      title: 'Préparation de la reprise des traitements',
      description: `${tasks.pendingCheckpointPreparations ?? 0} checkpoint(s) à préparer ; ${tasks.pendingCheckpointCaches ?? 0} jeu(x) de fichiers à mettre en cache. Une reprise reste refusée tant que ses fichiers vérifiés sont absents.${hints}`,
      isAlert: false,
    };
  }
  if (!status) {
    return {
      title: 'État de synchronisation indisponible',
      description: 'Impossible de lire l’état du maillage entre les serveurs.',
      isAlert: true,
    };
  }
  if (!status.enabled) {
    return {
      title: 'Synchronisation entre serveurs inactive',
      description: `${status.disabledReason ?? 'Le rattachement du serveur au cluster n’est pas terminé.'} Sans ce maillage, chaque serveur ne voit que ses données locales.`,
      isAlert: true,
    };
  }
  const peers = status.peers.filter((peer) => peer.configured || peer.discovered);
  if (!peers.length) {
    if (isReady && serversCount <= 1) {
      return {
        title: 'Serveur unique',
        description:
          'Ce cluster ne comporte qu’un seul serveur : les données sont conservées localement. La synchronisation maillée s’activera automatiquement dès qu’un second serveur rejoindra le cluster.',
        isAlert: false,
      };
    }
    const detail = status.discoveryAvailable
      ? 'Aucun pair n’a été découvert. Vérifiez que mDNS est autorisé entre les serveurs ou configurez Cluster:Reconciliation:Peers__0 avec une adresse joignable.'
      : `La découverte mDNS est indisponible${status.discoveryError ? ` (${status.discoveryError})` : ''}. Configurez Cluster:Reconciliation:Peers__0 avec une adresse joignable.`;
    return { title: 'Aucun serveur pair détecté', description: detail, isAlert: true };
  }
  const failed = peers.filter((peer) => peer.lastError);
  if (failed.length) {
    const firstError = failed[0].lastError;
    return {
      title: `Synchronisation en échec avec ${failed.length} serveur(s)`,
      description: firstError
        ? `${failed[0].url} — ${firstError}`
        : 'Vérifiez que les serveurs sont approuvés dans le cluster et peuvent communiquer.',
      isAlert: true,
    };
  }
  const pending = peers.filter((peer) => !peer.lastSuccessAt).length;
  if (pending) {
    return {
      title: 'Serveur(s) détecté(s), premier échange en attente',
      description: `${pending} pair(s) n’a pas encore confirmé de réconciliation. Vérifiez sa disponibilité réseau et son admission au cluster.`,
      isAlert: true,
    };
  }
  return {
    title: `Synchronisation active avec ${peers.length} serveur(s)`,
    description: `Dernier échange réussi : ${new Date(Math.max(...peers.map((peer) => Date.parse(peer.lastSuccessAt!)))).toLocaleString()}.${hints}`,
    isAlert: false,
  };
}
