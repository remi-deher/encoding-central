import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  output,
  signal,
  TemplateRef,
  viewChild,
} from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import {
  ApiTokenReplicationView,
  ApiTokenResponse,
  CreatedApiTokenResponse,
  tokenPropagation,
} from '../../../shared/models/api-token';
import { formatRelativeTime } from '../../../shared/models/date-format';
import { ApiTokenService } from '../../../shared/services/api-token.service';
import { ClipboardService } from '../../../shared/services/clipboard.service';
import { NotificationService } from '../../../shared/services/notification.service';
import { ConfigurationSectionComponent } from '../../../shared/ui/configuration-section.component';
import {
  AccessListAction,
  AccessListActionEvent,
  AccessListComponent,
  AccessListRow,
} from '../../../shared/ui/access-list.component';
import { RecordFormComponent, RecordFormData } from '../../../shared/ui/record-form.component';
import { SettingsEditor } from '../settings-editor';

interface TokenRow extends AccessListRow {
  readonly isActive: boolean;
}

@Component({
  selector: 'app-api-token-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    AccessListComponent,
    MatButtonModule,
    ConfigurationSectionComponent,
    RecordFormComponent,
  ],
  template: `
    <app-configuration-section
      title="Jetons d’API"
      description="Un accès dédié à chaque application ou script. Le mot de passe du compte est requis pour afficher la clé, qui ne sera présentée qu’une fois."
      icon="api"
    >
      <button section-actions matButton="tonal" (click)="openCreate()">Créer un jeton</button>
      @if (state() === 'loading') {
        <p role="status">Chargement des jetons…</p>
      } @else if (state() === 'error') {
        <p role="alert">Impossible de charger les jetons.</p>
        <button matButton="text" (click)="refresh()">Réessayer</button>
      } @else {
        <app-access-list
          [rows]="rows()"
          [actions]="actions"
          (rowAction)="onAction($event)"
          emptyMessage="Aucun jeton d’API. Créez-en un pour autoriser une application."
        />
      }
    </app-configuration-section>
    <ng-template #tokenEditor>
      @if (secret()) {
        <div class="secret-content">
          <h3>Copiez votre jeton</h3>
          <p>Il ne sera plus affiché après la fermeture de ce panneau.</p>
          <code>{{ secret() }}</code
          ><button matButton="tonal" (click)="copySecret()">Copier le jeton</button
          ><button matButton="text" (click)="editor.close()">Terminer</button>
        </div>
      } @else {
        <app-record-form
          [data]="form"
          [busy]="busy()"
          (submitted)="create($event)"
          (cancelled)="editor.close()"
        />
      }
    </ng-template>
  `,
  styles: `
    .secret-content {
      display: grid;
      gap: var(--space-4);
    }
    .secret-content h3,
    .secret-content p {
      margin: 0;
    }
    .secret-content code {
      overflow-wrap: anywhere;
      padding: var(--space-4);
      background: var(--surface-container-low);
      border-radius: var(--radius-md);
      user-select: all;
    }
  `,
})
export class ApiTokenTabComponent {
  private readonly tokenService = inject(ApiTokenService);
  private readonly notifications = inject(NotificationService);
  private readonly clipboard = inject(ClipboardService);
  protected readonly editor = inject(SettingsEditor);
  private readonly tokenEditor = viewChild.required<TemplateRef<unknown>>('tokenEditor');
  private readonly recordForm = viewChild(RecordFormComponent);
  readonly tokensUpdated = output<readonly ApiTokenResponse[]>();
  readonly tokenCreated = output<CreatedApiTokenResponse>();
  protected readonly tokens = signal<readonly ApiTokenResponse[]>([]);
  protected readonly replication = signal<ApiTokenReplicationView | null>(null);
  protected readonly state = signal<'loading' | 'error' | 'ready'>('loading');
  protected readonly busy = signal(false);
  protected readonly secret = signal('');
  protected readonly form: RecordFormData = {
    title: 'Nouveau jeton',
    description: 'Choisissez un nom et les droits nécessaires à cette application.',
    submitLabel: 'Créer le jeton',
    review: false,
    fields: [
      { id: 'name', label: 'Nom du jeton', type: 'text', required: true, maxLength: 64 },
      { id: 'currentPassword', label: 'Mot de passe du compte', type: 'password', required: true },
      {
        id: 'role',
        label: 'Rôle',
        type: 'select',
        required: true,
        options: [
          { value: 'viewer', label: 'Lecture seule' },
          { value: 'admin', label: 'Administrateur' },
        ],
      },
      {
        id: 'mcpAccess',
        label: 'Écriture via le MCP',
        type: 'select',
        required: true,
        options: [
          { value: 'read', label: 'Lecture seule (recommandé)' },
          { value: 'write', label: 'Écriture complète (administrateur uniquement)' },
        ],
      },
      {
        id: 'duration',
        label: 'Validité',
        type: 'select',
        required: true,
        options: [
          { value: 'never', label: 'Sans expiration' },
          { value: '30', label: '30 jours' },
          { value: '90', label: '90 jours' },
          { value: '365', label: '1 an' },
        ],
      },
    ],
    initialValues: { role: 'viewer', mcpAccess: 'read', duration: 'never' },
  };
  protected readonly actions: readonly AccessListAction<TokenRow>[] = [
    {
      id: 'revoke',
      label: 'Révoquer',
      tone: 'danger',
      visibleWhen: (row) => !!row['isActive'],
      confirmation: (row) => ({
        title: 'Révoquer ce jeton ?',
        message: `Les applications utilisant « ${row.title} » perdront leur accès au cluster.`,
        confirmLabel: 'Révoquer',
      }),
    },
    {
      id: 'delete',
      label: 'Supprimer',
      tone: 'danger',
      confirmation: (row) => ({
        title: 'Supprimer ce jeton ?',
        message: `Le jeton « ${row.title} » sera définitivement supprimé.`,
        confirmLabel: 'Supprimer',
      }),
    },
  ];
  protected readonly rows = computed<readonly TokenRow[]>(() =>
    this.tokens().map((token) => {
      const propagation = tokenPropagation(token, this.replication());
      const expired = !!token.expiresAt && new Date(token.expiresAt).getTime() <= Date.now();
      const usage = !token.isActive
        ? { label: 'Révoqué', tone: 'danger' as const }
        : expired
          ? { label: 'Expiré', tone: 'danger' as const }
          : token.lastUsedAt
            ? {
                label: `Dernier usage : ${formatRelativeTime(token.lastUsedAt)}`,
                tone: 'success' as const,
              }
            : { label: 'Jamais utilisé', tone: 'warning' as const };
      return {
        id: token.id,
        title: token.name,
        kind: 'token' as const,
        isActive: token.isActive,
        monoDetail: `${token.tokenPrefix}…`,
        details: [
          token.expiresAt
            ? `Expire le ${new Date(token.expiresAt).toLocaleDateString('fr-FR')}`
            : 'Sans expiration',
        ],
        badges: [
          {
            label: token.role === 'admin' ? 'Administrateur' : 'Lecture seule',
            tone: 'accent' as const,
            plain: true,
          },
          ...(token.scopes?.length
            ? [{ label: 'Écriture MCP', tone: 'warning' as const, plain: true }]
            : []),
          usage,
          ...(propagation ? [propagation] : []),
        ],
      };
    }),
  );
  constructor() {
    void this.refresh();
  }
  async refresh(): Promise<void> {
    try {
      const list = await firstValueFrom(this.tokenService.list());
      this.tokens.set(list);
      this.tokensUpdated.emit(list);
      this.state.set('ready');
      // La propagation est une information complémentaire : son échec ne masque pas la liste.
      this.tokenService.replication().subscribe({
        next: (view) => this.replication.set(view),
        error: () => this.replication.set(null),
      });
    } catch {
      this.state.set('error');
      this.notifications.show('Impossible de récupérer les jetons d’API.');
    }
  }
  protected openCreate(): void {
    this.secret.set('');
    void this.editor.open({
      title: 'Créer un jeton',
      content: this.tokenEditor(),
      dirty: () => !this.secret() && (this.recordForm()?.hasUnsavedChanges() ?? false),
      busy: () => this.busy(),
      onClosed: () => this.secret.set(''),
    });
  }
  protected async create(value: Record<string, string>): Promise<void> {
    this.busy.set(true);
    try {
      const expiresAt =
        value['duration'] === 'never'
          ? null
          : new Date(Date.now() + Number(value['duration']) * 86400000).toISOString();
      const result = await firstValueFrom(
        this.tokenService.create({
          name: value['name'].trim(),
          role: value['role'] as 'admin' | 'viewer',
          currentPassword: value['currentPassword'],
          expiresAt,
          scopes: value['role'] === 'admin' && value['mcpAccess'] === 'write' ? ['mcp:write'] : [],
        }),
      );
      this.secret.set(result.token);
      this.tokenCreated.emit(result);
      this.notifications.show('Jeton créé. Copiez sa valeur avant de fermer le panneau.');
      await this.refresh();
    } catch {
      this.notifications.show('Impossible de créer le jeton.');
    } finally {
      this.busy.set(false);
    }
  }
  protected async copySecret(): Promise<void> {
    this.notifications.show(
      (await this.clipboard.copy(this.secret()))
        ? 'Jeton copié.'
        : 'Impossible de copier le jeton.',
    );
  }
  protected async onAction(event: AccessListActionEvent<TokenRow>): Promise<void> {
    try {
      await firstValueFrom(
        event.actionId === 'delete'
          ? this.tokenService.delete(String(event.row.id))
          : this.tokenService.revoke(String(event.row.id)),
      );
      await this.refresh();
      this.notifications.show('Jeton mis à jour.');
    } catch {
      this.notifications.show('Impossible de modifier le jeton.');
    }
  }
}
