import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  output,
  signal,
} from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { NotificationService } from '../../shared/services/notification.service';
import { PluginConfigurationResponse } from '../../shared/models/flow-validation';
import {
  SettingGroupComponent,
  SettingRowComponent,
} from '../../shared/ui/settings/setting-row.component';
import { SettingsNoticeComponent } from '../../shared/ui/settings/settings-notice.component';
import { PlexEncodingLibrary, PlexLibraries, PlexServer, PlexSetupApi } from './plex-setup.api';

const POLL_MS = 2000;
const CUSTOM_URI = '__custom__';

/**
 * Assistant de configuration du plugin Plex (ADR 0008) : connexion plex.tv par code, choix du serveur,
 * récupération des bibliothèques Plex et correspondance avec les bibliothèques Encoding.
 * Il n'écrit que la configuration globale du plugin, par l'API ; le formulaire déclaratif reste disponible dessous.
 */
@Component({
  selector: 'app-plex-setup-assistant',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    SettingGroupComponent,
    SettingRowComponent,
    SettingsNoticeComponent,
  ],
  template: `
    <section class="plex-setup" aria-labelledby="plex-setup-title">
      <h3 id="plex-setup-title">Assistant Plex</h3>

      <app-setting-group>
        <app-setting-row
          label="1. Se connecter avec Plex"
          [hint]="
            servers().length
              ? 'Compte Plex connecté. Choisissez le serveur et l’adresse joignable depuis Encoding.'
              : 'Ouvre la page de connexion Plex ; Encoding reçoit un jeton propre au serveur choisi, jamais affiché.'
          "
          [stacked]="servers().length > 0"
        >
          @if (!servers().length) {
            <button
              mat-flat-button
              type="button"
              (click)="connect()"
              [disabled]="signInBusy()"
              data-testid="plex-sign-in"
            >
              {{ signInBusy() ? 'En attente de Plex…' : 'Se connecter avec Plex' }}
            </button>
          } @else {
            <div class="server-choice">
              <mat-form-field appearance="outline">
                <mat-label>Serveur</mat-label>
                <mat-select [value]="serverId()" (valueChange)="selectServer($event)">
                  @for (server of servers(); track server.id) {
                    <mat-option [value]="server.id">
                      {{ server.name }}{{ server.owned ? '' : ' (partagé)' }}
                    </mat-option>
                  }
                </mat-select>
              </mat-form-field>
              <mat-form-field appearance="outline">
                <mat-label>Adresse</mat-label>
                <mat-select [value]="uriChoice()" (valueChange)="uriChoice.set($event)">
                  @for (connection of selectedServer()?.connections ?? []; track connection.uri) {
                    <mat-option [value]="connection.uri">
                      {{ connection.uri }}
                      {{
                        connection.relay
                          ? '· relais'
                          : connection.local
                            ? '· réseau local'
                            : '· distant'
                      }}
                    </mat-option>
                  }
                  <mat-option [value]="customUri">Autre adresse…</mat-option>
                </mat-select>
              </mat-form-field>
              @if (uriChoice() === customUri) {
                <mat-form-field appearance="outline">
                  <mat-label>Adresse du serveur</mat-label>
                  <input
                    matInput
                    [value]="customUriValue()"
                    (input)="customUriValue.set($any($event.target).value)"
                    placeholder="http://192.168.1.10:32400"
                  />
                </mat-form-field>
              }
              <button
                mat-flat-button
                type="button"
                (click)="useServer()"
                [disabled]="serverBusy() || !effectiveUri()"
                data-testid="plex-use-server"
              >
                {{ serverBusy() ? 'Vérification…' : 'Utiliser ce serveur' }}
              </button>
            </div>
          }
        </app-setting-row>

        <app-setting-row
          label="2. Correspondance des bibliothèques"
          hint="Lit les bibliothèques du serveur Plex enregistré et propose, pour chaque bibliothèque Encoding, le dossier Plex correspondant."
          [stacked]="!!libraries()"
        >
          @if (!libraries()) {
            <button
              mat-stroked-button
              type="button"
              (click)="loadLibraries()"
              [disabled]="librariesBusy()"
              data-testid="plex-load-libraries"
            >
              {{ librariesBusy() ? 'Lecture…' : 'Récupérer les bibliothèques Plex' }}
            </button>
          } @else {
            <div class="mapping-table" role="table" aria-label="Correspondance des bibliothèques">
              @for (library of libraries()!.libraries; track library.id) {
                <div class="mapping-row" role="row">
                  <div class="mapping-local" role="cell">
                    <strong>{{ library.name }}</strong>
                    <code>{{
                      library.localPaths.join(' · ') || 'aucun accès direct déclaré'
                    }}</code>
                  </div>
                  <div class="mapping-remote" role="cell">
                    <mat-form-field appearance="outline">
                      <mat-label>Dossier Plex</mat-label>
                      <mat-select
                        [value]="choices()[library.id] ?? ''"
                        (valueChange)="choose(library.id, $event)"
                        [attr.data-library]="library.id"
                      >
                        <mat-option value="">Aucune correspondance</mat-option>
                        @for (option of locationOptions(); track option.value) {
                          <mat-option [value]="option.value">{{ option.label }}</mat-option>
                        }
                      </mat-select>
                    </mat-form-field>
                    @if (hintFor(library); as hint) {
                      <span class="mapping-hint">{{ hint }}</span>
                    }
                  </div>
                </div>
              }
            </div>
            @if (duplicateRoots().length) {
              <app-settings-notice tone="warning">
                Un même dossier Plex est choisi pour plusieurs bibliothèques :
                {{ duplicateRoots().join(', ') }}.
              </app-settings-notice>
            }
            <div class="mapping-actions">
              <button
                mat-button
                type="button"
                (click)="loadLibraries()"
                [disabled]="librariesBusy()"
              >
                Relire
              </button>
              <button
                mat-flat-button
                type="button"
                (click)="saveMappings()"
                [disabled]="mappingBusy() || duplicateRoots().length > 0"
                data-testid="plex-save-mappings"
              >
                {{ mappingBusy() ? 'Enregistrement…' : 'Enregistrer la correspondance' }}
              </button>
            </div>
          }
        </app-setting-row>
      </app-setting-group>

      @if (error()) {
        <app-settings-notice tone="danger">{{ error() }}</app-settings-notice>
      }
    </section>
  `,
  styles: `
    .plex-setup {
      display: flex;
      flex-direction: column;
      gap: var(--space-3);
      margin-bottom: var(--space-5);
    }
    h3 {
      margin: 0;
      font: var(--font-title-sm, inherit);
    }
    .server-choice,
    .mapping-actions {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--space-3);
    }
    .server-choice mat-form-field {
      min-width: 220px;
      flex: 1 1 220px;
    }
    .mapping-table {
      display: flex;
      flex-direction: column;
      gap: 3px;
      min-width: 0;
    }
    .mapping-row {
      display: grid;
      grid-template-columns: minmax(0, 1fr) minmax(0, 1.2fr);
      gap: var(--space-3);
      align-items: center;
      padding: var(--space-2) var(--space-3);
      border-radius: var(--radius-sm);
      background: var(--surface-container-low);
    }
    .mapping-local {
      display: flex;
      flex-direction: column;
      min-width: 0;
    }
    .mapping-local code {
      font-family: ui-monospace, 'JetBrains Mono', Consolas, monospace;
      font-size: var(--font-label, 12px);
      color: var(--text-secondary);
      overflow-wrap: anywhere;
    }
    .mapping-remote {
      display: flex;
      flex-direction: column;
      min-width: 0;
    }
    .mapping-remote mat-form-field {
      width: 100%;
    }
    .mapping-hint {
      font-size: var(--font-label, 12px);
      color: var(--text-secondary);
    }
    .mapping-actions {
      justify-content: flex-end;
    }
    @media (max-width: 700px) {
      .mapping-row {
        grid-template-columns: minmax(0, 1fr);
      }
    }
  `,
})
export class PlexSetupAssistantComponent {
  private readonly api = inject(PlexSetupApi);
  private readonly notifications = inject(NotificationService);

  /** Configuration enregistrée par l'assistant : la page met à jour son formulaire et sa révision. */
  readonly configurationSaved = output<PluginConfigurationResponse>();

  protected readonly customUri = CUSTOM_URI;
  protected readonly signInBusy = signal(false);
  protected readonly serverBusy = signal(false);
  protected readonly librariesBusy = signal(false);
  protected readonly mappingBusy = signal(false);
  protected readonly error = signal('');
  protected readonly servers = signal<readonly PlexServer[]>([]);
  protected readonly serverId = signal('');
  protected readonly uriChoice = signal('');
  protected readonly customUriValue = signal('');
  protected readonly libraries = signal<PlexLibraries | null>(null);
  protected readonly choices = signal<Readonly<Record<string, string>>>({});
  private session = '';
  private signInOperation = 0;

  protected readonly selectedServer = computed(
    () => this.servers().find((server) => server.id === this.serverId()) ?? null,
  );
  protected readonly effectiveUri = computed(() =>
    (this.uriChoice() === CUSTOM_URI ? this.customUriValue() : this.uriChoice()).trim(),
  );
  protected readonly locationOptions = computed(() =>
    (this.libraries()?.sections ?? []).flatMap((section) =>
      section.locations.map((location) => ({
        value: location,
        label: `${section.title} — ${location}`,
      })),
    ),
  );
  protected readonly duplicateRoots = computed(() => {
    const roots = Object.values(this.choices()).filter((root) => root);
    return [...new Set(roots.filter((root, index) => roots.indexOf(root) !== index))];
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => this.signInOperation++);
  }

  protected async connect(): Promise<void> {
    const operation = ++this.signInOperation;
    this.error.set('');
    this.signInBusy.set(true);
    try {
      const pin = await this.api.createPin();
      window.open(pin.authUrl, '_blank', 'noopener');
      const deadline = Date.parse(pin.expiresAt) || Date.now() + 15 * 60_000;
      while (operation === this.signInOperation && Date.now() < deadline) {
        await new Promise((resolve) => setTimeout(resolve, POLL_MS));
        if (operation !== this.signInOperation) return;
        const check = await this.api.checkPin(pin.pinId, pin.clientIdentifier);
        if (check.status === 'authorized' && check.session) {
          this.session = check.session;
          const servers = check.servers ?? [];
          this.servers.set(servers);
          if (!servers.length) this.error.set('Ce compte Plex n’a accès à aucun serveur.');
          else this.selectServer(servers[0].id);
          return;
        }
        if (check.status === 'expired') break;
      }
      if (operation === this.signInOperation)
        this.error.set('Le code de connexion Plex a expiré. Recommencez la connexion.');
    } catch (error) {
      if (operation === this.signInOperation) this.error.set(this.message(error));
    } finally {
      if (operation === this.signInOperation) this.signInBusy.set(false);
    }
  }

  protected selectServer(id: string): void {
    this.serverId.set(id);
    const first = this.servers().find((server) => server.id === id)?.connections[0]?.uri;
    this.uriChoice.set(first ?? CUSTOM_URI);
  }

  protected async useServer(): Promise<void> {
    const uri = this.effectiveUri();
    if (!this.session || !this.serverId() || !uri) return;
    this.error.set('');
    this.serverBusy.set(true);
    try {
      const saved = await this.api.selectServer(this.session, this.serverId(), uri);
      this.configurationSaved.emit(saved);
      this.notifications.show('Serveur Plex enregistré et joignable.');
      await this.loadLibraries();
    } catch (error) {
      this.error.set(this.message(error));
    } finally {
      this.serverBusy.set(false);
    }
  }

  protected async loadLibraries(): Promise<void> {
    this.error.set('');
    this.librariesBusy.set(true);
    try {
      const result = await this.api.libraries();
      this.libraries.set(result);
      this.choices.set(
        Object.fromEntries(
          result.libraries.map((library) => [
            library.id,
            library.currentRemoteRoot ?? library.suggestion?.remoteRoot ?? '',
          ]),
        ),
      );
    } catch (error) {
      this.error.set(this.message(error));
    } finally {
      this.librariesBusy.set(false);
    }
  }

  protected choose(libraryId: string, remoteRoot: string): void {
    this.choices.update((choices) => ({ ...choices, [libraryId]: remoteRoot }));
  }

  protected hintFor(library: PlexEncodingLibrary): string {
    const choice = this.choices()[library.id] ?? '';
    if (library.currentRemoteRoot && choice === library.currentRemoteRoot) return 'Enregistrée';
    if (library.suggestion && choice === library.suggestion.remoteRoot)
      return `Proposée : ${library.suggestion.reason}`;
    if (!library.suggestion && !library.currentRemoteRoot)
      return 'Aucune proposition : choisissez si besoin';
    return '';
  }

  protected async saveMappings(): Promise<void> {
    const mappings = Object.entries(this.choices())
      .filter(([, remoteRoot]) => remoteRoot)
      .map(([libraryId, remoteRoot]) => ({ libraryId, remoteRoot }));
    this.error.set('');
    this.mappingBusy.set(true);
    try {
      const saved = await this.api.saveMappings(mappings);
      this.configurationSaved.emit(saved);
      this.notifications.show(
        `Correspondance enregistrée pour ${mappings.length} bibliothèque${mappings.length > 1 ? 's' : ''}.`,
      );
      await this.loadLibraries();
    } catch (error) {
      this.error.set(this.message(error));
    } finally {
      this.mappingBusy.set(false);
    }
  }

  private message(error: unknown): string {
    if (error instanceof HttpErrorResponse && typeof error.error?.title === 'string')
      return error.error.title;
    return 'Opération impossible. Vérifiez la connexion au serveur Encoding puis réessayez.';
  }
}
