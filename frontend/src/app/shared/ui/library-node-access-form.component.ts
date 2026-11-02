import { MatExpansionModule } from '@angular/material/expansion';
import { HttpClient } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, FormRecord, ReactiveFormsModule } from '@angular/forms';
import { firstValueFrom, Subscription, filter, switchMap, take, timeout, timer } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { NodeGroupResponse } from '../models/cluster';
import { LibraryNodeAccessMode, LibraryNodeAccessResponse } from './library-node-access-types';

export interface LibraryNodeAccessOption {
  readonly id: string;
  readonly name: string;
  readonly roles: readonly string[];
}

export interface LibraryNodeAccessValue {
  readonly nodeId: string;
  readonly mode: LibraryNodeAccessMode;
  readonly isEnabled: boolean;
  readonly localPath: string | null;
  readonly canRead: boolean;
  readonly canWrite: boolean;
  readonly transferSourceNodeId: string | null;
}

interface NodeDirectoryBrowserEntry {
  readonly name: string;
  readonly path: string;
}

interface NodeDirectoryBrowserResponse {
  readonly id: string;
  readonly path: string;
  readonly state: 'queued' | 'claimed' | 'completed' | 'failed';
  readonly entries: readonly NodeDirectoryBrowserEntry[];
  readonly hasMore: boolean;
  readonly error: string | null;
}

interface NodeAccessControls {
  mode: FormControl<LibraryNodeAccessMode>;
  isEnabled: FormControl<boolean>;
  localPath: FormControl<string>;
  canRead: FormControl<boolean>;
  canWrite: FormControl<boolean>;
  transferSourceNodeId: FormControl<string>;
}

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-library-node-access-form',
  imports: [
    MatExpansionModule,
    MatButtonModule,
    MatCheckboxModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressSpinnerModule,
    MatSelectModule,
    ReactiveFormsModule,
  ],
  template: `
    @if (!nodes().length) {
      <div class="access-empty" role="status">
        <strong>Aucun nœud n’est encore enregistré</strong>
        <p>
          Ajoutez un nœud au cluster avant de configurer l’accès aux médias de «
          {{ libraryName() }} ».
        </p>
        <button matButton="filled" type="button" (click)="cancelled.emit()">Fermer</button>
      </div>
    } @else {
      <section class="access-form" [attr.aria-label]="'Accès aux médias pour ' + libraryName()">
        <div class="access-context">
          <span class="step-label">{{
            setupMode() ? 'Configuration initiale' : 'Modification des accès'
          }}</span>
          <p>
            Choisissez comment chaque nœud atteint les fichiers de « {{ libraryName() }} ». Le
            chemin peut être différent sur chaque machine.
          </p>
          <p class="access-safety-note">
            Un nœud sans accès ne recevra aucune tâche pour cette bibliothèque.
          </p>
        </div>

        @if (!reviewing()) {
          <form [formGroup]="form" (ngSubmit)="review()">
            @if (setupMode()) {
              <section class="node-selection" aria-labelledby="node-selection-title">
                <h3 id="node-selection-title">Nœuds qui ont accès aux fichiers</h3>
                <mat-form-field appearance="outline">
                  <mat-label>Mode de sélection</mat-label>
                  <mat-select
                    [value]="selectionMode()"
                    (selectionChange)="selectionMode.set($event.value)"
                  >
                    <mat-option value="automatic">Automatique</mat-option>
                    <mat-option value="nodes">Sélectionner des nœuds</mat-option>
                    <mat-option value="groups">Sélectionner des groupes de nœuds</mat-option>
                  </mat-select>
                </mat-form-field>
                @if (selectionMode() === 'automatic') {
                  <p class="mode-help">
                    Les nœuds déjà configurés en accès direct à une autre bibliothèque sont
                    sélectionnés automatiquement ({{ selectedNodes().length }}).
                  </p>
                } @else if (selectionMode() === 'nodes') {
                  <mat-form-field appearance="outline">
                    <mat-label>Nœuds sélectionnés</mat-label>
                    <mat-select
                      multiple
                      [value]="selectedNodeIds()"
                      (selectionChange)="selectedNodeIds.set($event.value)"
                    >
                      @for (node of nodes(); track node.id) {
                        <mat-option [value]="node.id">{{ node.name }}</mat-option>
                      }
                    </mat-select>
                  </mat-form-field>
                } @else {
                  <mat-form-field appearance="outline">
                    <mat-label>Groupes sélectionnés</mat-label>
                    <mat-select
                      multiple
                      [value]="selectedGroupIds()"
                      (selectionChange)="selectedGroupIds.set($event.value)"
                    >
                      @for (group of groups(); track group.id) {
                        <mat-option [value]="group.id"
                          >{{ group.name }} ({{ group.nodeIds.length }} nœud(s))</mat-option
                        >
                      }
                    </mat-select>
                    @if (!groups().length) {
                      <mat-hint
                        >Aucun groupe n’est encore créé. Créez-en dans la section « Groupes de nœuds
                        ».</mat-hint
                      >
                    }
                  </mat-form-field>
                }
              </section>
            }
            <div class="node-access-list">
              @for (node of selectedNodes(); track node.id) {
                @let controls = form.controls[node.id];
                <section
                  class="node-access-card"
                  [formGroup]="controls"
                  [attr.aria-label]="'Accès de ' + node.name"
                >
                  <header class="node-heading">
                    <div>
                      <strong>{{ node.name }}</strong
                      ><span>{{ rolesLabel(node.roles) }}</span>
                    </div>
                    <span class="current-access">{{ currentSummary(node.id) }}</span>
                  </header>

                  @if (controls.controls.mode.value === 'unsupported') {
                    <p class="mode-help">Aucun accès n’est configuré pour ce nœud.</p>
                    <button
                      matButton="outlined"
                      type="button"
                      (click)="onModeChange(node.id, 'direct')"
                    >
                      Configurer l’accès
                    </button>
                  } @else {
                    <mat-form-field appearance="outline">
                      <mat-label>Comment ce nœud accède-t-il aux fichiers ?</mat-label>
                      <mat-select
                        formControlName="mode"
                        (selectionChange)="onModeChange(node.id, $event.value)"
                      >
                        <mat-option value="direct">Le nœud a accès au fichier</mat-option>
                        <mat-option value="transfer" [disabled]="!node.roles.includes('worker')"
                          >Un autre nœud ou groupe lui transfère les copies</mat-option
                        >
                      </mat-select>
                    </mat-form-field>
                  }

                  @if (controls.controls.mode.value !== 'unsupported') {
                    <mat-checkbox formControlName="isEnabled"
                      >Autoriser ce nœud à recevoir des tâches</mat-checkbox
                    >
                  }

                  @if (controls.controls.mode.value === 'direct') {
                    <mat-form-field appearance="outline">
                      <mat-label>Chemin des fichiers sur {{ node.name }}</mat-label>
                      <input
                        matInput
                        formControlName="localPath"
                        autocomplete="off"
                        placeholder="Ex. /media/films ou D:\\Films"
                      />
                      <mat-hint
                        >Indiquez le chemin local ou le point de montage visible depuis ce
                        nœud.</mat-hint
                      >
                    </mat-form-field>
                    <button
                      matButton="outlined"
                      type="button"
                      [disabled]="busy() || browserLoading()"
                      (click)="openDirectoryBrowser(node.id, controls.controls.localPath.value)"
                    >
                      Parcourir les dossiers sur {{ node.name }}
                    </button>
                    <p class="mode-help">
                      Les dossiers affichés sont ceux visibles par le service Encoding sur ce nœud.
                    </p>
                    @if (browserNodeId() === node.id) {
                      <section
                        class="directory-browser"
                        [attr.aria-label]="'Dossiers sur ' + node.name"
                      >
                        <header class="directory-browser-header">
                          <div class="directory-browser-location">
                            <strong>{{ browserPath() || 'Racine du nœud' }}</strong>
                            <span>{{ node.name }}</span>
                          </div>
                          <div class="directory-browser-actions">
                            @if (browserPath()) {
                              <button
                                matButton="text"
                                type="button"
                                [disabled]="browserLoading()"
                                (click)="browseDirectory(node.id, '')"
                              >
                                Racine du nœud
                              </button>
                            }
                            @if (hasParentDirectory()) {
                              <button
                                matButton="text"
                                type="button"
                                [disabled]="browserLoading()"
                                (click)="browseDirectory(node.id, parentDirectoryPath())"
                              >
                                Dossier parent
                              </button>
                            }
                            @if (browserPath()) {
                              <button
                                matButton="outlined"
                                type="button"
                                [disabled]="browserLoading()"
                                (click)="chooseBrowserPath(node.id)"
                              >
                                Utiliser ce dossier
                              </button>
                            }
                            <button
                              matButton="text"
                              type="button"
                              (click)="closeDirectoryBrowser()"
                            >
                              Fermer
                            </button>
                          </div>
                        </header>
                        @if (browserLoading()) {
                          <div class="directory-browser-status" role="status">
                            <mat-spinner diameter="22" />
                            <span>Lecture des dossiers sur {{ node.name }}…</span>
                          </div>
                        } @else if (browserError()) {
                          <p class="directory-browser-error" role="alert">{{ browserError() }}</p>
                          <button
                            matButton="outlined"
                            type="button"
                            (click)="browseDirectory(node.id, browserPath())"
                          >
                            Réessayer
                          </button>
                        } @else if (!browserEntries().length) {
                          <p class="directory-browser-empty">
                            Aucun sous-dossier accessible dans cet emplacement.
                          </p>
                        } @else {
                          <div
                            class="directory-browser-list"
                            role="list"
                            aria-label="Sous-dossiers"
                          >
                            @for (entry of browserEntries(); track entry.path) {
                              <div role="listitem">
                                <button
                                  class="directory-browser-entry"
                                  type="button"
                                  (click)="browseDirectory(node.id, entry.path)"
                                >
                                  <span class="directory-browser-folder" aria-hidden="true"
                                    >📁</span
                                  >
                                  <span>{{ entry.name }}</span>
                                  <span class="directory-browser-open">Ouvrir</span>
                                </button>
                              </div>
                            }
                          </div>
                          @if (browserHasMore()) {
                            <p class="directory-browser-note">
                              Affichage limité aux 200 premiers dossiers.
                            </p>
                          }
                        }
                      </section>
                    }
                    <div class="permissions" aria-label="Droits de l’accès direct">
                      <mat-checkbox formControlName="canRead"
                        >Lire les fichiers source</mat-checkbox
                      >
                      <mat-checkbox formControlName="canWrite">Écrire les résultats</mat-checkbox>
                    </div>
                    <mat-expansion-panel>
                      <mat-expansion-panel-header
                        >Accès personnalisé par montage ou ACL</mat-expansion-panel-header
                      >
                      <p>
                        Si les droits par défaut ne suffisent pas, préparez sur l’hôte un montage ou
                        une ACL propre à cette bibliothèque, puis choisissez son chemin ci-dessus
                        pour ce nœud. Le processus utilisera réellement ce point d’accès.
                      </p>
                      <p>
                        Les cases Lecture et Écriture déclarent les besoins ; elles n’accordent
                        aucun droit système. Avant traitement, le nœud vérifie les droits effectifs
                        dans un dossier temporaire dédié.
                      </p>
                      <p>
                        Un groupe ajouté au conteneur s’applique à tout le processus. Pour limiter
                        l’accès par bibliothèque, utilisez des ACL ou montages distincts sur l’hôte.
                        Aucun UID/GID saisi ne peut remplacer cette configuration.
                      </p>
                    </mat-expansion-panel>
                  } @else if (controls.controls.mode.value === 'transfer') {
                    <p class="mode-help">
                      Le worker encode une copie locale temporaire. Le nœud source conserve le
                      fichier original et reçoit le résultat.
                    </p>
                    <mat-form-field appearance="outline">
                      <mat-label>Nœud qui a accès aux fichiers</mat-label>
                      <mat-select formControlName="transferSourceNodeId">
                        @for (source of transferSources(node.id); track source.id) {
                          <mat-option [value]="source.id">{{ source.name }}</mat-option>
                        }
                      </mat-select>
                      @if (!transferSources(node.id).length) {
                        <mat-hint
                          >Choisissez d’abord un autre nœud avec accès direct en lecture et en
                          écriture.</mat-hint
                        >
                      }
                    </mat-form-field>
                  } @else {
                    <p class="mode-help">
                      Ce nœud sera exclu de la distribution des tâches liées à cette bibliothèque.
                    </p>
                  }
                </section>
              }
            </div>
            <div class="form-actions">
              <button matButton="text" type="button" [disabled]="busy()" (click)="cancelled.emit()">
                {{ setupMode() ? 'Plus tard' : 'Annuler' }}
              </button>
              <button matButton="filled" type="submit" [disabled]="!canSave() || busy()">
                Vérifier les changements
              </button>
            </div>
          </form>
        } @else {
          <section class="access-review" aria-labelledby="access-review-title">
            <div class="review-heading">
              <h3 id="access-review-title">Vérifier les accès avant l’enregistrement</h3>
              <p>
                Comparez la configuration actuelle avec les choix proposés. Les modifications
                s’appliqueront à tous les nœuds listés.
              </p>
            </div>
            <div class="review-list">
              @for (node of selectedNodes(); track node.id) {
                <article class="review-row">
                  <strong>{{ node.name }}</strong>
                  <div>
                    <span>Actuellement</span>
                    <p>{{ currentSummary(node.id) }}</p>
                  </div>
                  <div>
                    <span>Après enregistrement</span>
                    <p>{{ proposedSummary(node.id) }}</p>
                  </div>
                </article>
              }
            </div>
            <div class="form-actions">
              <button
                matButton="text"
                type="button"
                [disabled]="busy()"
                (click)="reviewing.set(false)"
              >
                Revenir aux choix
              </button>
              <button matButton="filled" type="button" [disabled]="busy()" (click)="save()">
                {{ busy() ? 'Enregistrement…' : 'Enregistrer les accès' }}
              </button>
            </div>
          </section>
        }
      </section>
    }
  `,
  styles: `
    .access-form {
      display: grid;
      gap: 18px;
    }
    .node-selection {
      display: grid;
      gap: 10px;
      padding: 14px;
      border: 1px solid var(--border-subtle);
      border-radius: 10px;
      background: var(--surface-muted);
    }
    .node-selection h3 {
      margin: 0;
      color: var(--text-primary);
      font-size: 13px;
    }
    .access-context {
      display: grid;
      gap: 7px;
    }
    .access-context p {
      margin: 0;
      color: var(--text-secondary);
      line-height: 1.5;
    }
    .step-label {
      color: var(--accent);
      font-size: 14px;
      font-weight: 700;
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }
    .access-safety-note {
      padding: 10px 12px;
      border-left: 3px solid var(--accent);
      background: var(--surface-muted);
    }
    form,
    .access-review {
      display: grid;
      gap: 16px;
    }
    .node-access-list {
      display: grid;
      gap: 12px;
    }
    .node-access-card {
      padding: 16px;
      display: grid;
      gap: 10px;
      border: 1px solid var(--border-subtle);
      border-radius: 10px;
      background: var(--surface);
    }
    .node-heading {
      display: grid;
      gap: 7px;
    }
    .node-heading div {
      display: flex;
      flex-wrap: wrap;
      align-items: baseline;
      gap: 8px;
    }
    .node-heading strong {
      color: var(--text-primary);
      font-size: 14px;
    }
    .node-heading div span {
      color: var(--text-muted);
      font-size: 14px;
      text-transform: capitalize;
    }
    .current-access {
      color: var(--text-secondary);
      font-size: 14px;
    }
    mat-form-field {
      width: 100%;
    }
    .permissions {
      display: flex;
      flex-wrap: wrap;
      gap: 16px;
    }
    .directory-browser {
      display: grid;
      gap: 10px;
      padding: 12px;
      border: 1px solid var(--border-subtle);
      border-radius: 12px;
      background: var(--surface-muted);
      min-width: 0;
    }
    .directory-browser-header,
    .directory-browser-actions {
      display: flex;
      align-items: center;
      justify-content: space-between;
      flex-wrap: wrap;
      gap: 8px;
    }
    .directory-browser-location {
      display: grid;
      gap: 3px;
      min-width: 0;
    }
    .directory-browser-location strong {
      overflow-wrap: anywhere;
      color: var(--text-primary);
      font-size: 14px;
    }
    .directory-browser-location span,
    .directory-browser-note {
      color: var(--text-muted);
      font-size: 12px;
    }
    .directory-browser-list {
      display: grid;
      max-height: 280px;
      overflow: auto;
      border: 1px solid var(--border-subtle);
      border-radius: 8px;
      background: var(--surface);
    }
    .directory-browser-entry {
      display: grid;
      grid-template-columns: 22px minmax(0, 1fr) auto;
      align-items: center;
      gap: 8px;
      padding: 10px 12px;
      border: 0;
      border-bottom: 1px solid var(--border-subtle);
      background: transparent;
      color: var(--text-primary);
      text-align: left;
      cursor: pointer;
    }
    .directory-browser-list > [role='listitem']:last-child .directory-browser-entry {
      border-bottom: 0;
    }
    .directory-browser-entry:hover {
      background: var(--surface-container-high);
    }
    .directory-browser-entry > span:nth-child(2) {
      overflow-wrap: anywhere;
    }
    .directory-browser-open {
      color: var(--accent);
      font-size: 12px;
    }
    .directory-browser-status {
      display: flex;
      align-items: center;
      gap: 10px;
      color: var(--text-secondary);
    }
    .directory-browser-error {
      margin: 0;
      color: var(--color-error, #b3261e);
    }
    .directory-browser-empty {
      margin: 0;
      color: var(--text-secondary);
    }
    .directory-browser-note {
      margin: 0;
    }
    .mode-help {
      margin: 0;
      color: var(--text-secondary);
      line-height: 1.5;
    }
    .form-actions {
      display: flex;
      justify-content: flex-end;
      gap: 8px;
      padding-top: 4px;
    }
    .access-empty {
      display: grid;
      justify-items: start;
      gap: 12px;
    }
    .access-empty strong {
      color: var(--text-primary);
    }
    .access-empty p {
      margin: 0;
      color: var(--text-secondary);
      line-height: 1.5;
    }
    .review-heading {
      display: grid;
      gap: 6px;
    }
    .review-heading h3 {
      margin: 0;
      color: var(--text-primary);
      font-size: 14px;
    }
    .review-heading p {
      margin: 0;
      color: var(--text-secondary);
      line-height: 1.5;
    }
    .review-list {
      display: grid;
      gap: 8px;
    }
    .review-row {
      padding: 12px;
      display: grid;
      gap: 10px;
      border: 1px solid var(--border-subtle);
      border-radius: 9px;
      background: var(--surface);
    }
    .review-row > strong {
      color: var(--text-primary);
      font-size: 14px;
    }
    .review-row div {
      display: grid;
      gap: 3px;
    }
    .review-row div span {
      color: var(--text-muted);
      font-size: 14px;
      font-weight: 700;
      text-transform: uppercase;
    }
    .review-row p {
      margin: 0;
      color: var(--text-secondary);
      line-height: 1.45;
      overflow-wrap: anywhere;
    }
  `,
})
export class LibraryNodeAccessFormComponent implements OnInit {
  private readonly http = inject(HttpClient);
  private readonly destroyRef = inject(DestroyRef);
  private browserPoll: Subscription | null = null;
  private browserGeneration = 0;
  readonly libraryName = input.required<string>();
  readonly setupMode = input(false);
  readonly nodes = input.required<readonly LibraryNodeAccessOption[]>();
  readonly groups = input<readonly NodeGroupResponse[]>([]);
  readonly automaticallySelectedNodeIds = input<readonly string[]>([]);
  readonly initialAccesses = input<readonly LibraryNodeAccessResponse[]>([]);
  readonly busy = input(false);
  readonly saved = output<readonly LibraryNodeAccessValue[]>();
  readonly cancelled = output<void>();
  protected readonly form = new FormRecord<FormGroup<NodeAccessControls>>({});
  protected readonly reviewing = signal(false);
  protected readonly selectionMode = signal<'automatic' | 'nodes' | 'groups'>('automatic');
  protected readonly selectedNodeIds = signal<readonly string[]>([]);
  protected readonly selectedGroupIds = signal<readonly string[]>([]);
  protected readonly browserNodeId = signal<string | null>(null);
  protected readonly browserPath = signal('');
  protected readonly browserEntries = signal<readonly NodeDirectoryBrowserEntry[]>([]);
  protected readonly browserHasMore = signal(false);
  protected readonly browserLoading = signal(false);
  protected readonly browserError = signal<string | null>(null);
  private readonly currentAccesses = new Map<string, LibraryNodeAccessResponse>();

  protected async openDirectoryBrowser(nodeId: string, path: string): Promise<void> {
    this.browserNodeId.set(nodeId);
    this.browserPath.set(path.trim());
    await this.browseDirectory(nodeId, path.trim());
  }

  protected async browseDirectory(nodeId: string, path: string): Promise<void> {
    this.browserPoll?.unsubscribe();
    const generation = ++this.browserGeneration;
    this.browserNodeId.set(nodeId);
    this.browserPath.set(path);
    this.browserEntries.set([]);
    this.browserHasMore.set(false);
    this.browserError.set(null);
    this.browserLoading.set(true);
    try {
      const request = await firstValueFrom(
        this.http.post<{ id: string }>(
          `/api/v1/nodes/${encodeURIComponent(nodeId)}/directory-browse`,
          { path: path || null },
        ),
      );
      if (generation !== this.browserGeneration) return;
      this.browserPoll = timer(0, 400)
        .pipe(
          switchMap(() =>
            this.http.get<NodeDirectoryBrowserResponse>(
              `/api/v1/nodes/${encodeURIComponent(nodeId)}/directory-browse/${encodeURIComponent(request.id)}`,
            ),
          ),
          filter((result) => result.state === 'completed' || result.state === 'failed'),
          take(1),
          timeout({ first: 30_000 }),
          takeUntilDestroyed(this.destroyRef),
        )
        .subscribe({
          next: (result) => {
            if (generation !== this.browserGeneration) return;
            this.browserPath.set(result.path);
            this.browserEntries.set(result.entries);
            this.browserHasMore.set(result.hasMore);
            this.browserError.set(
              result.state === 'failed' ? (result.error ?? 'Impossible de lire ce dossier.') : null,
            );
            this.browserLoading.set(false);
          },
          error: () => {
            if (generation !== this.browserGeneration) return;
            this.browserError.set(
              'Le nœud ne répond pas. Vérifiez qu’il est en ligne, puis réessayez.',
            );
            this.browserLoading.set(false);
          },
        });
    } catch {
      if (generation !== this.browserGeneration) return;
      this.browserError.set('La demande d’exploration n’a pas pu être envoyée au nœud.');
      this.browserLoading.set(false);
    }
  }

  protected parentDirectoryPath(): string {
    const current = this.browserPath();
    const separator = current.includes('\\') ? '\\' : '/';
    const trimmed = current.replace(/[\\/]+$/, '');
    const index = trimmed.lastIndexOf(separator);
    return index <= 0
      ? separator === '\\' && /^[A-Za-z]:$/.test(trimmed)
        ? `${trimmed}\\`
        : separator
      : separator === '\\' && index === 2 && /^[A-Za-z]:/.test(trimmed)
        ? `${trimmed.slice(0, 2)}\\`
        : trimmed.slice(0, index);
  }

  protected hasParentDirectory(): boolean {
    if (!this.browserPath()) return false;
    const parent = this.parentDirectoryPath();
    return parent !== this.browserPath();
  }

  protected chooseBrowserPath(nodeId: string): void {
    const controls = this.form.controls[nodeId]?.controls;
    if (!controls || !this.browserPath()) return;
    controls.localPath.setValue(this.browserPath());
    this.closeDirectoryBrowser();
  }

  protected closeDirectoryBrowser(): void {
    this.browserGeneration++;
    this.browserPoll?.unsubscribe();
    this.browserPoll = null;
    this.browserNodeId.set(null);
    this.browserLoading.set(false);
    this.browserError.set(null);
  }
  protected canSave(): boolean {
    const selectedNodes = this.selectedNodes();
    return (
      selectedNodes.length > 0 &&
      selectedNodes.every((node) => {
        const controls = this.form.controls[node.id]?.controls;
        if (!controls) return false;
        const mode = controls.mode.value;
        if (mode === 'direct') return !!controls.localPath.value.trim() && controls.canRead.value;
        if (mode === 'unsupported') return true;
        if (mode !== 'transfer' || !controls.transferSourceNodeId.value) return false;
        const source = this.form.controls[controls.transferSourceNodeId.value]?.controls;
        return (
          !!source &&
          source.mode.value === 'direct' &&
          !!source.localPath.value.trim() &&
          source.canRead.value &&
          source.canWrite.value
        );
      })
    );
  }

  protected selectedNodes(): readonly LibraryNodeAccessOption[] {
    const nodeIds = this.setupMode()
      ? this.selectionMode() === 'automatic'
        ? new Set(this.automaticallySelectedNodeIds())
        : this.selectionMode() === 'nodes'
          ? new Set(this.selectedNodeIds())
          : new Set(
              this.groups()
                .filter((group) => this.selectedGroupIds().includes(group.id))
                .flatMap((group) => group.nodeIds),
            )
      : new Set(this.nodes().map((node) => node.id));
    return this.nodes().filter((node) => nodeIds.has(node.id));
  }

  ngOnInit(): void {
    for (const access of this.initialAccesses()) this.currentAccesses.set(access.nodeId, access);
    for (const node of this.nodes()) {
      const access = this.currentAccesses.get(node.id);
      this.form.addControl(
        node.id,
        new FormGroup<NodeAccessControls>({
          mode: new FormControl(access?.mode ?? (this.setupMode() ? 'direct' : 'unsupported'), {
            nonNullable: true,
          }),
          isEnabled: new FormControl(access?.isEnabled ?? access?.mode !== 'unsupported', {
            nonNullable: true,
          }),
          localPath: new FormControl(access?.localPath ?? '', { nonNullable: true }),
          canRead: new FormControl(access?.mode === 'direct' ? access.canRead : false, {
            nonNullable: true,
          }),
          canWrite: new FormControl(access?.mode === 'direct' ? access.canWrite : false, {
            nonNullable: true,
          }),
          transferSourceNodeId: new FormControl(access?.transferSourceNodeId ?? '', {
            nonNullable: true,
          }),
        }),
      );
    }
  }

  protected transferSources(targetNodeId: string): readonly LibraryNodeAccessOption[] {
    return this.selectedNodes().filter((node) => {
      if (node.id === targetNodeId) return false;
      const source = this.form.controls[node.id]?.controls;
      return (
        !!source &&
        source.mode.value === 'direct' &&
        !!source.localPath.value.trim() &&
        source.canRead.value &&
        source.canWrite.value
      );
    });
  }

  protected onModeChange(nodeId: string, mode: LibraryNodeAccessMode): void {
    this.form.controls[nodeId]?.controls.mode.setValue(mode);
    this.form.controls[nodeId]?.controls.isEnabled.setValue(mode !== 'unsupported');
  }

  protected review(): void {
    if (this.canSave()) this.reviewing.set(true);
  }

  protected rolesLabel(roles: readonly string[]): string {
    return roles
      .map((role) => (role === 'worker' ? 'Worker' : role === 'server' ? 'Serveur' : role))
      .join(' · ');
  }

  protected currentSummary(nodeId: string): string {
    const access = this.currentAccesses.get(nodeId);
    if (!access) return 'Non configuré';
    const summary = this.describe(
      access.mode,
      access.localPath,
      access.transferSourceNodeName,
      access.canRead,
      access.canWrite,
    );
    return access.mode !== 'unsupported' && !access.isEnabled ? `Désactivé · ${summary}` : summary;
  }

  protected proposedSummary(nodeId: string): string {
    const controls = this.form.controls[nodeId].controls;
    const mode = controls.mode.value;
    const source =
      this.nodes().find((node) => node.id === controls.transferSourceNodeId.value)?.name ?? null;
    const summary = this.describe(
      mode,
      controls.localPath.value.trim(),
      source,
      controls.canRead.value,
      controls.canWrite.value,
    );
    return mode !== 'unsupported' && !controls.isEnabled.value ? `Désactivé · ${summary}` : summary;
  }

  private describe(
    mode: LibraryNodeAccessMode,
    path: string | null,
    source: string | null,
    canRead: boolean,
    canWrite: boolean,
  ): string {
    if (mode === 'direct') {
      const permissions = [canRead ? 'lecture' : '', canWrite ? 'écriture' : '']
        .filter(Boolean)
        .join(' et ');
      return `Accès direct · ${path || 'chemin à préciser'} · ${permissions || 'aucun droit'}`;
    }
    if (mode === 'transfer') return `Copie temporaire depuis ${source ?? 'source à choisir'}`;
    return 'Ne recevra pas de tâches pour cette bibliothèque';
  }

  protected save(): void {
    if (!this.canSave()) return;
    const values = this.selectedNodes().map((node): LibraryNodeAccessValue => {
      const controls = this.form.controls[node.id].controls;
      const mode = controls.mode.value;
      return {
        nodeId: node.id,
        mode,
        isEnabled: mode !== 'unsupported' && controls.isEnabled.value,
        localPath: mode === 'direct' ? controls.localPath.value.trim() : null,
        canRead: mode === 'direct' ? controls.canRead.value : mode === 'transfer',
        canWrite: mode === 'direct' ? controls.canWrite.value : mode === 'transfer',
        transferSourceNodeId: mode === 'transfer' ? controls.transferSourceNodeId.value : null,
      };
    });
    this.saved.emit(values);
  }
}
