import { Injectable, computed, inject, signal } from '@angular/core';
import { LiveAnnouncer } from '@angular/cdk/a11y';
import { BreakpointObserver } from '@angular/cdk/layout';
import { toSignal } from '@angular/core/rxjs-interop';
import { map } from 'rxjs';
import { MatDialog } from '@angular/material/dialog';
import { DetailCard } from '../../src/app/shared/ui/detail-card-grid.component';
import {
  DataTableColumn,
  DataTableRow,
  DataTableRowAction,
} from '../../src/app/shared/ui/data-table.component';
import { NodeModeDialogComponent } from '../../src/app/shared/ui/node-mode-dialog.component';
import { RecordFormData } from '../../src/app/shared/ui/record-form.component';
import { NotificationService } from '../../src/app/shared/services/notification.service';
import { NodeMode } from '../../src/app/shared/models/node-mode';
import { GuidedStep } from '../shared/guided-setup';

type Scene = 'standalone' | 'member' | 'administrator';
const card = (
  id: string,
  title: string,
  subtitle: string,
  details: readonly { label: string; description: string }[],
): DetailCard => ({
  id,
  title,
  subtitle,
  icon: 'server',
  tags: [],
  sections: [{ title: 'Détails', items: details }],
});

@Injectable({ providedIn: 'root' })
export class MockClusterState {
  readonly scene = signal<Scene>('standalone');
  readonly tab = signal('node');
  readonly drawer = signal<'join' | 'create' | null>(null);
  readonly joinIndex = signal(0);
  readonly busy = signal(false);
  readonly error = signal('');
  readonly address = signal('http://192.168.1.51:8090');
  readonly clusterName = signal('Maison');
  readonly mode = signal<NodeMode>({ server: true, worker: true });
  readonly waiting = signal(false);
  readonly failNext = signal(false);
  readonly pendingRequest = signal(true);
  readonly createdHere = signal(false);
  readonly localName = signal('encoding-50');
  readonly name = computed(() => this.localName());
  readonly standalone = computed(() => this.scene() === 'standalone');
  readonly tabs = [
    { value: 'node', label: 'Ce nœud' },
    { value: 'cluster', label: 'Cluster' },
  ];
  private readonly dialogs = inject(MatDialog);
  private readonly notifications = inject(NotificationService);
  private readonly announcer = inject(LiveAnnouncer);
  readonly handset = toSignal(
    inject(BreakpointObserver)
      .observe('(max-width: 700px)')
      .pipe(map((result) => result.matches)),
    { initialValue: false },
  );
  readonly createForm: RecordFormData = {
    title: 'Créer un cluster',
    description: '',
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
    initialValues: { name: 'Maison' },
  };
  readonly columns: readonly DataTableColumn[] = [
    { key: 'name', label: 'Nœud', sortable: true },
    { key: 'address', label: 'Adresse' },
    { key: 'functions', label: 'Fonctions' },
    { key: 'state', label: 'État' },
  ];
  readonly requestColumns: readonly DataTableColumn[] = [
    { key: 'name', label: 'Nœud demandeur' },
    { key: 'address', label: 'Adresse' },
    { key: 'state', label: 'État' },
  ];
  readonly members = computed<readonly DataTableRow[]>(() =>
    this.createdHere()
      ? [
          {
            id: '50',
            name: 'encoding-50 · Ce nœud',
            address: '192.168.1.50',
            functions: 'Serveur · Worker',
            state: 'En ligne',
          },
        ]
      : [
          {
            id: '51',
            name: 'encoding-51' + (this.scene() === 'administrator' ? ' · Ce nœud' : ''),
            address: '192.168.1.51',
            functions: 'Serveur · Worker',
            state: 'En ligne',
          },
          ...(!this.pendingRequest() || this.scene() === 'member'
            ? [
                {
                  id: '50',
                  name: 'encoding-50' + (this.scene() === 'member' ? ' · Ce nœud' : ''),
                  address: '192.168.1.50',
                  functions: 'Worker',
                  state: 'En ligne',
                },
              ]
            : []),
        ],
  );
  readonly requests: readonly DataTableRow[] = [
    { id: '50', name: 'encoding-50', address: '192.168.1.50', state: 'En attente d’approbation' },
  ];
  readonly requestActions: readonly DataTableRowAction[] = [
    {
      id: 'approve',
      label: 'Examiner la demande',
      confirmation: () => ({
        title: 'Autoriser encoding-50 à rejoindre Maison ?',
        confirmLabel: 'Approuver le rattachement',
        message:
          'Vérifiez le nom, l’adresse et le code avec la personne qui a envoyé cette demande.',
        items: [
          { label: 'Nœud : encoding-50', detail: '192.168.1.50 · Fonction demandée : Worker' },
          {
            label: 'Code : 58C2–A91F',
            detail: 'Doit correspondre au code affiché sur le nœud demandeur.',
          },
        ],
      }),
    },
  ];
  readonly nodeCards = computed<readonly DetailCard[]>(() => [
    card('roles', 'Rôle du nœud', 'Fonctions de cette installation', [
      {
        label: 'Serveur · ' + (this.mode().server ? 'Actif' : 'Inactif'),
        description: 'Interface, configuration partagée et orchestration.',
      },
      {
        label: 'Worker · ' + (this.mode().worker ? 'Actif' : 'Inactif'),
        description: 'Traitement des médias avec les outils de ce nœud.',
      },
    ]),
    card('identity', 'Cette installation', 'Identité et connexion', [
      {
        label: this.name(),
        description: this.name() === 'encoding-51' ? '192.168.1.51' : '192.168.1.50',
      },
      {
        label: this.standalone() ? 'Nœud autonome' : 'Membre de ' + this.clusterName(),
        description: this.standalone() ? 'Aucun cluster associé.' : 'Connexion au cluster établie.',
      },
    ]),
    {
      ...card('tools', 'Capacités de traitement', 'Détectées sur cette installation', [
        { label: '8 cœurs · 32 Go de mémoire', description: 'Linux · x64' },
        {
          label: 'FFmpeg · ffprobe · MKVToolNix',
          description: '2 traitements simultanés au maximum.',
        },
      ]),
      icon: 'tool',
      tags: [{ label: 'Outils disponibles', tone: 'success' }],
    },
  ]);
  readonly standaloneCards: readonly DetailCard[] = [
    {
      ...card(
        'standalone',
        'Votre installation reste utilisable',
        'Aucun rattachement obligatoire',
        [
          {
            label: 'Un nœud autonome',
            description: 'Gérez ses fonctions et ses capacités depuis l’onglet Ce nœud.',
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
  readonly clusterCards = computed<readonly DetailCard[]>(() => [
    card('cluster', 'Cluster ' + this.clusterName(), 'Configuration partagée', [
      {
        label: this.members().length + ' nœud' + (this.members().length > 1 ? 's' : ''),
        description: 'Les fonctions de chaque membre sont indiquées ci-dessous.',
      },
      {
        label: 'État : opérationnel',
        description: 'Les nœuds communiquent et partagent la configuration.',
      },
    ]),
  ]);
  readonly steps = computed<readonly GuidedStep[]>(() => [
    {
      id: 'address',
      label: 'Choisir le cluster',
      description: 'Saisissez l’adresse d’un nœud serveur du cluster à rejoindre.',
      form: {
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
        initialValues: { address: this.address() },
      },
      note: 'Aucun cluster détecté automatiquement. Vous pouvez utiliser son adresse directement.',
    },
    {
      id: 'identity',
      label: 'Vérifier et confirmer',
      description:
        'Le cluster répond. Vérifiez qu’il s’agit bien de celui que vous souhaitez rejoindre.',
      cards: [
        card('target', 'Maison', 'Cluster joignable', [
          { label: 'encoding-51', description: this.address() },
          {
            label: 'Identifiant : 51a28e48…e09e',
            description: 'À comparer avec la fiche Cluster sur le serveur d’accueil.',
          },
        ]),
      ],
      note: 'Après approbation, encoding-50 devient worker du cluster Maison. Sa fonction serveur locale s’arrête. Sa configuration locale n’est pas importée automatiquement.',
      actions: [
        { id: 'back', label: 'Modifier l’adresse' },
        { id: 'request', label: 'Envoyer la demande', primary: true },
      ],
    },
    {
      id: 'waiting',
      label: 'Approbation',
      description:
        'La demande a été envoyée à Maison. Un administrateur doit l’approuver depuis Cluster → Demandes de rattachement.',
      cards: [
        card('request', 'En attente d’approbation', 'Votre nœud reste autonome pendant l’attente', [
          { label: 'Nœud demandeur : encoding-50', description: 'Adresse : 192.168.1.50' },
          {
            label: 'Code de vérification : 58C2–A91F',
            description: 'Comparez ce code avec celui affiché sur Maison avant d’approuver.',
          },
        ]),
      ],
      actions: [
        { id: 'later', label: 'Fermer et suivre plus tard' },
        { id: 'cancel-request', label: 'Annuler la demande' },
      ],
    },
    {
      id: 'done',
      label: 'Rattachement terminé',
      description:
        'encoding-50 fait maintenant partie de Maison. Le cluster peut lui attribuer des traitements compatibles avec ses capacités et ses accès aux bibliothèques.',
      cards: [
        card('done', 'Bienvenue dans Maison', 'Connexion au cluster établie', [
          {
            label: 'Fonction : Worker',
            description: 'Ce nœud apparaît désormais dans la liste des membres.',
          },
        ]),
      ],
      actions: [{ id: 'finish', label: 'Voir le cluster', primary: true }],
    },
  ]);

  openJoin(): void {
    this.tab.set('cluster');
    this.error.set('');
    this.joinIndex.set(this.waiting() ? 2 : 0);
    this.drawer.set('join');
  }
  close(): void {
    this.drawer.set(null);
  }
  preview(scene: Scene): void {
    this.scene.set(scene);
    this.drawer.set(null);
    this.waiting.set(false);
    this.joinIndex.set(0);
    this.error.set('');
    this.createdHere.set(false);
    this.localName.set(scene === 'administrator' ? 'encoding-51' : 'encoding-50');
    this.failNext.set(false);
    this.pendingRequest.set(true);
    this.clusterName.set('Maison');
    this.mode.set({ server: scene !== 'member', worker: true });
    this.tab.set(scene === 'standalone' ? 'node' : 'cluster');
  }
  changeMode(): void {
    this.dialogs
      .open(NodeModeDialogComponent, {
        width: '560px',
        maxWidth: 'calc(100vw - 32px)',
        data: { managed: true, ...this.mode() },
      })
      .afterClosed()
      .subscribe((mode: NodeMode | undefined) => {
        if (mode) {
          this.mode.set(mode);
          this.notifications.show('Fonctions du nœud mises à jour dans la maquette.');
        }
      });
  }
  create(values: Record<string, string>): void {
    if (!values['name'].trim()) {
      this.notifications.show('Saisissez un nom pour le cluster.');
      return;
    }
    this.clusterName.set(values['name'].trim());
    this.scene.set('administrator');
    this.pendingRequest.set(false);
    this.createdHere.set(true);
    this.drawer.set(null);
    this.tab.set('cluster');
    this.notifications.show('Cluster créé. Ce nœud en est le premier membre.');
  }
  async verify(values: Record<string, string>): Promise<void> {
    this.address.set(values['address'].trim());
    this.error.set('');
    try {
      const url = new URL(this.address());
      if (!['http:', 'https:'].includes(url.protocol)) throw new Error();
    } catch {
      this.error.set('Saisissez une adresse HTTP ou HTTPS complète. Votre saisie est conservée.');
      return;
    }
    this.busy.set(true);
    await new Promise((resolve) => setTimeout(resolve, 700));
    this.busy.set(false);
    if (this.failNext()) {
      this.failNext.set(false);
      this.error.set(
        'Le cluster ne répond pas à cette adresse. Vérifiez l’adresse et le port, puis réessayez. Aucune demande n’a été envoyée.',
      );
      return;
    }
    this.joinIndex.set(1);
    void this.announcer.announce('Maison est joignable. Vérifiez son identité.');
  }
  act(id: string): void {
    if (id === 'back') this.joinIndex.set(0);
    if (id === 'request') {
      this.waiting.set(true);
      this.joinIndex.set(2);
      this.notifications.show('Demande envoyée au cluster Maison.');
    }
    if (id === 'later') this.drawer.set(null);
    if (id === 'cancel-request') {
      this.waiting.set(false);
      this.drawer.set(null);
      this.notifications.show('Demande annulée. Ce nœud reste autonome.');
    }
    if (id === 'finish') {
      this.drawer.set(null);
      this.tab.set('cluster');
    }
  }
  simulateApproval(): void {
    this.scene.set('member');
    this.mode.set({ server: false, worker: true });
    this.waiting.set(false);
    this.pendingRequest.set(false);
    this.joinIndex.set(3);
    this.notifications.show('Maison a approuvé le rattachement d’encoding-50.');
    this.drawer.set('join');
  }
  approveRequest(): void {
    this.pendingRequest.set(false);
    this.notifications.show('Rattachement approuvé. encoding-50 est membre de Maison.');
  }
}
