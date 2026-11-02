import { JoinUserCollisionsComponent } from './join-user-collisions.component';
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
import { firstValueFrom, Observable } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { MatSlideToggleChange, MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatDialog } from '@angular/material/dialog';
import { PasswordChangeDialogComponent } from '../../../core/auth/password-change-dialog.component';
import { ConfigurationSectionComponent } from '../../../shared/ui/configuration-section.component';
import { SettingsEditor } from '../settings-editor';
import { AuthService } from '../../../core/auth/auth.service';
import {
  MINIMUM_PASSWORD_LENGTH,
  USER_ROLE_LABELS,
  UserAccount,
} from '../../../shared/models/auth';
import { NotificationService } from '../../../shared/services/notification.service';
import { UserAccounts } from '../../../shared/services/user-accounts';
import {
  SettingGroupComponent,
  SettingRowComponent,
} from '../../../shared/ui/settings/setting-row.component';
import { ConfirmationDialogComponent } from '../../../shared/ui/confirmation-dialog.component';
import { RecordFormComponent, RecordFormData } from '../../../shared/ui/record-form.component';
import { PageShellContextService } from '../../../core/layout/page-shell-context';

interface UserRow {
  readonly id: string;
  readonly username: string;
  readonly role: string;
  readonly meta: string;
  readonly isActive: boolean;
  readonly isSelf: boolean;
}

@Component({
  selector: 'app-user-management-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    JoinUserCollisionsComponent,
    ConfigurationSectionComponent,
    SettingGroupComponent,
    SettingRowComponent,
    MatButtonModule,
    MatSlideToggleModule,
    RecordFormComponent,
  ],
  template: `
    <app-join-user-collisions />
    <app-configuration-section
      title="Utilisateurs"
      description="Les personnes qui peuvent accéder au cluster."
      icon="settings"
      [badge]="'' + users().length"
    >
      <button section-actions matButton="tonal" type="button" (click)="newUser()">
        Créer un compte
      </button>
      @if (state() === 'loading') {
        <p role="status">Chargement des utilisateurs…</p>
      } @else if (state() === 'error') {
        <p role="alert">Impossible de charger les utilisateurs.</p>
        <button matButton="text" (click)="refresh()">Réessayer</button>
      } @else {
        <app-setting-group>
          @for (row of filteredRows(); track row.id) {
            <app-setting-row [label]="row.username" [hint]="row.meta" [dense]="true">
              <span row-lead class="avatar" aria-hidden="true">{{ initial(row) }}</span>
              <mat-slide-toggle
                [checked]="row.isActive"
                [disabled]="row.isSelf || busy()"
                [aria-label]="(row.isActive ? 'Désactiver ' : 'Activer ') + row.username"
                (change)="setActive(row, $event)"
              />
              <button
                matButton="outlined"
                type="button"
                [attr.aria-label]="'Changer le mot de passe de ' + row.username"
                (click)="changePassword(row)"
              >
                Changer le mot de passe
              </button>
            </app-setting-row>
          } @empty {
            <p>Aucun utilisateur.</p>
          }
        </app-setting-group>
      }
    </app-configuration-section>
    <ng-template #userEditor
      ><app-record-form
        [data]="form()"
        [busy]="busy()"
        (submitted)="submit($event)"
        (cancelled)="editor.close()"
    /></ng-template>
  `,
  styles: `
    .avatar {
      display: grid;
      place-items: center;
      flex: none;
      width: 32px;
      height: 32px;
      border-radius: 50%;
      background: var(--accent-soft);
      color: var(--accent);
      font-size: var(--font-label);
      font-weight: 700;
    }
  `,
})
export class UserManagementTabComponent {
  protected readonly editor = inject(SettingsEditor);
  private readonly userEditor = viewChild.required<TemplateRef<unknown>>('userEditor');
  private readonly recordForm = viewChild(RecordFormComponent);
  private readonly accounts = inject(UserAccounts);
  private readonly dialog = inject(MatDialog);
  private readonly auth = inject(AuthService);
  private readonly notifications = inject(NotificationService);
  private readonly pageShell = inject(PageShellContextService);

  readonly usersUpdated = output<readonly UserAccount[]>();
  protected readonly users = signal<readonly UserAccount[]>([]);
  protected readonly state = signal<'ready' | 'loading' | 'error'>('loading');
  protected readonly localQuery = signal('');
  protected readonly query = computed(
    () => this.pageShell.current()?.query?.() ?? this.localQuery(),
  );
  protected readonly busy = signal(false);

  protected readonly rows = computed<readonly UserRow[]>(() =>
    this.users().map((user) => {
      const isSelf = user.id === this.auth.user()?.id;
      return {
        id: user.id,
        username: user.username,
        role: user.role,
        isActive: user.isActive,
        isSelf,
        meta: [
          USER_ROLE_LABELS[user.role],
          ...(isSelf ? ['vous'] : []),
          `créé le ${new Date(user.createdAt).toLocaleDateString('fr-FR')}`,
        ].join(' · '),
      };
    }),
  );

  protected readonly filteredRows = computed(() => {
    const q = this.query().trim().toLowerCase();
    return this.rows().filter((row) => !q || row.username.toLowerCase().includes(q));
  });

  protected readonly form = computed<RecordFormData>(() => ({
    title: 'Nouvel utilisateur',
    description: 'Créez un compte pour un membre de l’équipe.',
    review: false,
    submitLabel: 'Créer l’utilisateur',
    fields: [
      {
        id: 'username',
        label: 'Nom d’utilisateur',
        type: 'text',
        required: true,
        maxLength: 64,
        autocomplete: 'off',
      },
      {
        id: 'password',
        label: 'Mot de passe initial',
        type: 'password',
        required: true,
        autocomplete: 'new-password',
        minLength: MINIMUM_PASSWORD_LENGTH,
        hint: `Au moins ${MINIMUM_PASSWORD_LENGTH} caractères.`,
      },
      {
        id: 'role',
        label: 'Rôle',
        type: 'select',
        required: true,
        options: [
          { value: 'viewer', label: USER_ROLE_LABELS.viewer },
          { value: 'admin', label: USER_ROLE_LABELS.admin },
        ],
      },
    ],
    initialValues: { role: 'viewer' },
  }));

  constructor() {
    void this.refresh();
  }

  async refresh(): Promise<void> {
    try {
      const list = await firstValueFrom(this.accounts.list());
      this.users.set(list);
      this.usersUpdated.emit(list);
      this.state.set('ready');
    } catch {
      this.state.set(this.users().length ? 'ready' : 'error');
    }
  }

  protected newUser(): void {
    void this.editor.open({
      title: 'Créer un compte',
      content: this.userEditor(),
      dirty: () => this.recordForm()?.hasUnsavedChanges() ?? false,
      busy: () => this.busy(),
    });
  }

  protected closeForm(): void {
    this.editor.reset();
  }

  protected initial(row: UserRow): string {
    return row.username.charAt(0).toUpperCase();
  }

  // Même fenêtre, mêmes règles et mêmes messages que « Changer le mot de passe » du menu du compte.
  protected changePassword(row: UserRow): void {
    this.dialog.open(
      PasswordChangeDialogComponent,
      row.isSelf ? undefined : { data: { target: { id: row.id, username: row.username } } },
    );
  }

  protected setActive(row: UserRow, change: MatSlideToggleChange): void {
    const active = change.checked;
    const role = row.role as 'admin' | 'viewer';
    if (active) {
      void this.run(
        this.accounts.update(row.id, role, true),
        `« ${row.username} » est réactivé.`,
      ).then((done) => {
        if (!done) change.source.checked = false;
      });
      return;
    }
    this.dialog
      .open(ConfirmationDialogComponent, {
        data: {
          title: 'Désactiver ce compte ?',
          message: `Les sessions de « ${row.username} » seront fermées immédiatement.`,
          confirmLabel: 'Désactiver',
        },
      })
      .afterClosed()
      .subscribe((confirmed: boolean | undefined) => {
        if (confirmed) {
          void this.run(
            this.accounts.update(row.id, role, false),
            `« ${row.username} » est désactivé.`,
          ).then((done) => {
            if (!done) change.source.checked = true;
          });
        } else {
          change.source.checked = true;
        }
      });
  }

  protected async submit(value: Record<string, string>): Promise<void> {
    this.busy.set(true);
    try {
      await firstValueFrom(
        this.accounts.create(
          value['username'].trim(),
          value['password'],
          value['role'] as 'admin' | 'viewer',
        ),
      );
      this.notifications.show(`« ${value['username'].trim()} » a été créé.`);
      this.closeForm();
      await this.refresh();
    } catch (error) {
      this.notifications.show(AuthService.errorMessage(error, 'L’opération a échoué.'));
    } finally {
      this.busy.set(false);
    }
  }

  private async run(request: Observable<unknown>, success: string): Promise<boolean> {
    try {
      await firstValueFrom(request);
      this.notifications.show(success);
      await this.refresh();
      return true;
    } catch (error) {
      this.notifications.show(AuthService.errorMessage(error, 'L’opération a échoué.'));
      return false;
    }
  }
}
