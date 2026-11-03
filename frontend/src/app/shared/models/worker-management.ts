import { DetailCard } from '../ui/detail-card-grid.component';
import { formatMemory, workerAcceleratorBadges } from './worker-hardware';
import {
  ResourceSheetBadge,
  ResourceSheetItem,
  ResourceSheetMetric,
  ResourceSheetNotice,
  ResourceSheetSection,
} from '../ui/resource-sheet.component';
import { RecordFormData } from '../ui/record-form.component';
import { GuidedStep } from '../ui/guided-setup.component';
import { DiscoveredCentral, LocalWorkerPairing, LocalWorkerStatus } from './local-worker';
import { WorkerResponse } from './central';

export const NODE_MANAGEMENT_TABS = [
  { value: 'node', label: 'Ce worker' },
  { value: 'cluster', label: 'Central' },
  { value: 'groups', label: 'Groupes de workers' },
];
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

/** Quatre blocs de synthèse communs aux cartes des membres du central. */
export function memberWorkerOverviewSections(
  node: WorkerResponse,
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
    ? workerAcceleratorBadges(node).map((label) => ({
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
        { label: 'Central', value: clusterName },
      ],
    },
  ];
}

export interface LocalWorkerSheetInput {
  readonly status: LocalWorkerStatus | null;
  readonly node: WorkerResponse | null;
  readonly clusterName: string | null;
  readonly agentUnavailable: boolean;
  /** Tâches actives observées ; `null` tant que la source n'a pas répondu. */
  readonly activeTasks: number | null;
  readonly now?: number;
}

/**
 * Fiche du worker local, fondée uniquement sur les états observés : une fonction inconnue n'est jamais présentée
 * comme inactive, et une valeur périmée est identifiée comme dernière valeur connue.
 */
export function localWorkerSheet(input: LocalWorkerSheetInput): {
  readonly title: string;
  readonly subtitle: string;
  readonly notices: readonly ResourceSheetNotice[];
  readonly sections: readonly ResourceSheetSection[];
} {
  const { status, node, agentUnavailable } = input;
  const roles = status
    ? { server: status.roles.includes('server'), worker: status.roles.includes('worker') }
    : null;
  const roleBadges = (role: 'server' | 'worker'): readonly ResourceSheetBadge[] => {
    const enabled = roles ? roles[role] : undefined;
    const badges: ResourceSheetBadge[] =
      enabled === undefined
        ? [{ label: 'État indisponible', tone: 'warning' }]
        : agentUnavailable
          ? [{ label: `Dernier état connu : ${enabled ? 'actif' : 'inactif'}`, tone: 'warning' }]
          : [{ label: enabled ? 'Actif' : 'Inactif', tone: enabled ? 'success' : 'neutral' }];
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
      title: 'L’agent de ce worker ne répond pas',
      description:
        'Les informations ci-dessous sont les dernières connues ; elles peuvent ne plus être exactes.',
    });
  if (status?.enrollmentFailure)
    notices.push({
      tone: 'danger',
      title: 'Rattachement annulé',
      description: status.enrollmentFailure,
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
              detail: node ? undefined : 'Le suivi du worker est assuré par le serveur du central.',
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
              detail: 'Disponible après le prochain signal du worker.',
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
        { label: 'Identifiant du worker', value: status?.workerId ?? 'Indisponible', mono: true },
        {
          label: 'Central',
          value: input.clusterName ?? status?.centralName ?? 'Central',
        },
      ],
    },
  ];
  return {
    title: node?.displayName?.trim() || status?.name || 'Ce worker',
    subtitle: `Membre de ${input.clusterName ?? status?.centralName ?? 'son central'}`,
    notices,
    sections,
  };
}

export function centralAddressForm(address: string): RecordFormData {
  return {
    title: 'Adresse du central',
    description: '',
    submitLabel: 'Vérifier la connexion',
    review: false,
    fields: [
      {
        id: 'address',
        label: 'Adresse du central',
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
  readonly target: DiscoveredCentral | null;
  readonly status: LocalWorkerStatus | null;
  readonly pairing: LocalWorkerPairing | null;
  readonly clusters: readonly DiscoveredCentral[];
  readonly localServerActive: boolean;
  readonly canChoose: boolean;
}

export function centralJoinSteps(data: JoinPresentation): readonly GuidedStep[] {
  const name = data.target?.name ?? data.status?.centralName ?? 'le central d’accueil';
  const expires =
    data.pairing?.expiresAt ?? data.status?.pairingExpiresAt;
  const expiry = expires
    ? new Intl.DateTimeFormat('fr-FR', { dateStyle: 'short', timeStyle: 'short' }).format(
        new Date(expires),
      )
    : null;
  const code = data.status?.verificationCode ?? data.pairing?.verificationCode;
  return [
    {
      id: 'address',
      label: 'Choisir le central',
      description:
        'Choisissez un central détecté ou saisissez l’adresse d’un worker serveur du central à rejoindre.',
      ...(data.clusters.length
        ? {
            choices: {
              columns: [
                { key: 'name', label: 'Central' },
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
        ? 'La vérification contacte ce central. Aucune demande de rattachement n’est encore envoyée.'
        : 'La fonction worker est indisponible. Activez-la depuis Ce worker ou rétablissez sa connexion avant de continuer.',
    },
    {
      id: 'identity',
      label: 'Vérifier et confirmer',
      description:
        'Le central répond. Comparez son nom et son identifiant avec ceux indiqués sur le serveur d’accueil.',
      cards: data.target
        ? [
            informationCard('join-target', data.target.name, 'Central joignable', [
              { label: 'Adresse', description: data.target.apiBaseUrl },
              { label: 'Identifiant du central', description: data.target.id },
            ]),
          ]
        : [],
      note: data.localServerActive
        ? 'Après approbation, le serveur local reste actif. Les configurations et états convergent avec le central par le maillage SQLite. Les fichiers médias et résultats ne sont pas répliqués et doivent rester accessibles selon les accès configurés. La configuration locale n’est pas importée automatiquement. Un changement en cours peut être annulé avant l’approbation.'
        : 'Le central d’accueil doit approuver votre demande. Les accès aux bibliothèques et les capacités de ce worker restent nécessaires pour recevoir des traitements.',
      actions: [
        { id: 'back', label: 'Modifier l’adresse' },
        { id: 'request', label: 'Envoyer la demande', primary: true },
      ],
    },
    {
      id: 'waiting',
      label: 'Approbation',
      description: `Demande envoyée à ${name}. Un administrateur doit la vérifier depuis Workers, puis Demandes de rattachement.`,
      heroCode: code
        ? {
            code,
            label: 'Code à comparer',
            helper: `Un administrateur du central ${name} doit confirmer ce code identique sur son écran.`,
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
            { label: 'Worker demandeur', description: data.status?.name ?? 'Ce worker' },
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
      note: 'L’approbation peut arrêter cette interface locale. En cas de perte de connexion, consultez le central d’accueil pour connaître le résultat ; une déconnexion ne prouve pas une réussite.',
      actions: [
        { id: 'later', label: 'Fermer et suivre plus tard' },
        { id: 'cancel', label: 'Annuler la demande' },
      ],
    },
    {
      id: 'done',
      label: 'Rattachement terminé',
      description: `Le worker a confirmé son rattachement à ${name}.`,
      cards: [
        informationCard('join-complete', `Membre de ${name}`, 'Rattachement confirmé par le worker', [
          {
            label: 'Traitement des bibliothèques',
            description:
              'Le central attribue les tâches selon les capacités et les accès aux médias déclarés pour ce worker.',
          },
        ]),
      ],
      actions: [{ id: 'finish', label: 'Voir le central', primary: true }],
    },
  ];
}
