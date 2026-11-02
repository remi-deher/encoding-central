import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  computed,
  inject,
  signal,
} from '@angular/core';
import { concat, last } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { AuthService } from '../../../core/auth/auth.service';
import { PageShellContextService } from '../../../core/layout/page-shell-context';
import { AiToolsService } from '../../../shared/services/ai-tools.service';
import { NotificationService } from '../../../shared/services/notification.service';
import { AiClusterModelCatalog, AiDiscoveredModel } from '../../../shared/models/ai-tools';
import { ConfigurationSectionComponent } from '../../../shared/ui/configuration-section.component';
import { SearchFieldComponent } from '../../../shared/ui/search-field.component';
import {
  SettingGroupComponent,
  SettingRowComponent,
} from '../../../shared/ui/settings/setting-row.component';
import { aiEffortLabel, aiTypeLabel } from '../../ai/components/ai-model-selection';

type ModelFilter = 'all' | 'enabled' | 'disabled';

interface ModelEntry {
  readonly model: AiDiscoveredModel;
  readonly enabled: boolean;
  /** Détail des nœuds, seulement quand le modèle n'est pas partout. */
  readonly nodes: string;
}
interface ProviderGroup {
  readonly id: string;
  readonly name: string;
  readonly stateLabel: string;
  readonly stateTone: 'ok' | 'warning' | 'muted';
  readonly nodeSummary: string;
  readonly message: string | null;
  readonly total: number;
  readonly models: readonly ModelEntry[];
  readonly allEnabled: boolean;
}

/**
 * Modèles annoncés par les nœuds, regroupés par fournisseur : l'interrupteur est une règle
 * du cluster (fournisseur + modèle), il n'a donc pas lieu d'être répété par nœud.
 */
@Component({
  selector: 'app-ai-model-catalog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ConfigurationSectionComponent,
    SettingGroupComponent,
    SettingRowComponent,
    SearchFieldComponent,
    MatButtonModule,
    MatButtonToggleModule,
    MatSlideToggleModule,
  ],
  template: `
    <app-configuration-section
      title="Modèles disponibles"
      description="Choisissez les modèles proposés à l’assistant et aux flows. Valable pour tout le cluster."
      icon="pack"
    >
      @if (loading()) {
        <p class="status" role="status">Lecture des catalogues fournisseurs…</p>
      }
      @if (error()) {
        <p class="status" role="alert">{{ error() }}</p>
      }
      @if (groups().length) {
        <div class="toolbar">
          <app-search-field
            class="search"
            variant="filled"
            label="Rechercher un modèle"
            [value]="query()"
            (valueChange)="updateQuery($event)"
          />
          <mat-button-toggle-group
            aria-label="Filtrer les modèles"
            [value]="filter()"
            (change)="filter.set($event.value)"
          >
            <mat-button-toggle value="all">Tous</mat-button-toggle>
            <mat-button-toggle value="enabled">Activés</mat-button-toggle>
            <mat-button-toggle value="disabled">Désactivés</mat-button-toggle>
          </mat-button-toggle-group>
        </div>
      }
      @for (group of visibleGroups(); track group.id) {
        <section class="provider" [attr.aria-label]="group.name">
          <header>
            <h3>{{ group.name }}</h3>
            <span class="chip" [attr.data-tone]="group.stateTone">{{ group.stateLabel }}</span>
            @if (group.nodeSummary) {
              <span class="chip">{{ group.nodeSummary }}</span>
            }
            <span class="spacer"></span>
            @if (isAdmin() && group.total > 1) {
              <button
                matButton="outlined"
                type="button"
                [disabled]="saving()"
                (click)="setAll(group.id, !group.allEnabled)"
              >
                {{ group.allEnabled ? 'Tout désactiver' : 'Tout activer' }}
              </button>
            }
            <button
              matButton="text"
              type="button"
              [attr.aria-expanded]="!collapsed().has(group.id)"
              [attr.aria-label]="(collapsed().has(group.id) ? 'Déplier ' : 'Replier ') + group.name"
              (click)="toggleCollapsed(group.id)"
            >
              {{ collapsed().has(group.id) ? '▸' : '▾' }}
            </button>
          </header>
          @if (group.message) {
            <p class="status" role="status">{{ group.message }}</p>
          }
          @if (!collapsed().has(group.id)) {
            <app-setting-group>
              @for (entry of group.models; track entry.model.id) {
                <app-setting-row
                  [label]="entry.model.label"
                  [dense]="true"
                  [class.disabled]="!entry.enabled"
                >
                  <span row-detail class="detail">
                    @if (entry.model.id !== entry.model.label) {
                      <code>{{ entry.model.id }}</code>
                    }
                    @if (entry.nodes) {
                      <span>{{ entry.nodes }}</span>
                    }
                  </span>
                  <span class="levels">
                    @if (!entry.model.reasoningKnown) {
                      <span>raisonnement non communiqué</span>
                    } @else if (!entry.model.reasoningEfforts.length) {
                      <span>sans raisonnement</span>
                    } @else {
                      @for (effort of entry.model.reasoningEfforts; track effort) {
                        <span>{{ effortLabel(effort) }}</span>
                      }
                    }
                  </span>
                  <mat-slide-toggle
                    [checked]="entry.enabled"
                    [disabled]="!isAdmin() || saving()"
                    [aria-label]="entry.model.label"
                    (change)="setEnabled(group.id, entry.model.id, $event.checked)"
                  />
                </app-setting-row>
              } @empty {
                @if (group.total) {
                  <p class="status">Aucun modèle ne correspond.</p>
                }
              }
            </app-setting-group>
          }
        </section>
      }
      @if (!loading() && !groups().length && !error()) {
        <p class="status">Aucune connexion disponible.</p>
      }
    </app-configuration-section>
  `,
  styles: `
    :host {
      display: block;
    }
    .toolbar {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-3);
      margin-bottom: var(--space-4);
    }
    .search {
      flex: 1 1 220px;
    }
    .provider {
      margin-bottom: var(--space-5);
    }
    header {
      display: flex;
      align-items: center;
      flex-wrap: wrap;
      gap: var(--space-2);
      padding: var(--space-2) var(--space-4);
      margin-bottom: 3px;
      background: var(--surface-container);
      border-radius: var(--radius-xl) var(--radius-xl) var(--radius-sm) var(--radius-sm);
    }
    h3 {
      margin: 0;
      font-size: var(--font-body);
      font-weight: 650;
    }
    .spacer {
      flex: 1;
    }
    .chip {
      padding: 2px var(--space-3);
      border-radius: var(--radius-pill);
      background: var(--surface-container-highest);
      font-size: var(--font-label);
    }
    .chip[data-tone='ok'] {
      background: var(--success-soft);
      color: var(--success);
    }
    .chip[data-tone='warning'] {
      background: var(--warning-soft);
      color: var(--warning);
    }
    .chip[data-tone='muted'] {
      color: var(--text-secondary);
    }
    .status,
    .detail {
      color: var(--text-secondary);
      font-size: var(--font-label);
    }
    .status {
      margin: var(--space-2) var(--space-1);
    }
    .detail {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-2);
    }
    code {
      font-family: ui-monospace, Consolas, monospace;
      overflow-wrap: anywhere;
    }
    .levels {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-1);
      justify-content: flex-end;
    }
    .levels span {
      padding: 1px var(--space-2);
      border-radius: var(--radius-pill);
      background: var(--surface-container-highest);
      color: var(--text-secondary);
      font-size: var(--font-label);
    }
    :host ::ng-deep app-setting-row.disabled strong {
      color: var(--text-secondary);
      text-decoration: line-through;
    }
  `,
})
export class AiModelCatalogComponent implements OnDestroy {
  private readonly api = inject(AiToolsService);
  private readonly notifications = inject(NotificationService);
  protected readonly isAdmin = inject(AuthService).isAdmin;
  protected readonly catalog = signal<AiClusterModelCatalog | null>(null);
  protected readonly loading = signal(false);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  private readonly pageShell = inject(PageShellContextService);
  private readonly localQuery = signal('');
  protected readonly query = computed(
    () => this.pageShell.current()?.query?.() ?? this.localQuery(),
  );
  protected updateQuery(value: string): void {
    this.localQuery.set(value);
    this.pageShell.current()?.onSearchQueryChange?.(value);
  }
  protected readonly filter = signal<ModelFilter>('all');
  protected readonly collapsed = signal<ReadonlySet<string>>(new Set());
  protected readonly effortLabel = aiEffortLabel;
  protected readonly groups = computed(() => this.buildGroups(this.catalog()));
  protected readonly visibleGroups = computed(() => {
    const query = this.query().trim().toLowerCase();
    const filter = this.filter();
    return this.groups().map((group) => ({
      ...group,
      models: group.models.filter(
        (entry) =>
          (filter === 'all' || (filter === 'enabled') === entry.enabled) &&
          (!query ||
            entry.model.label.toLowerCase().includes(query) ||
            entry.model.id.toLowerCase().includes(query)),
      ),
    }));
  });
  private pollTimer: ReturnType<typeof setTimeout> | null = null;
  constructor() {
    this.refresh();
  }
  ngOnDestroy(): void {
    if (this.pollTimer !== null) clearTimeout(this.pollTimer);
  }
  protected refresh(): void {
    if (this.pollTimer !== null) clearTimeout(this.pollTimer);
    this.pollTimer = null;
    this.loading.set(true);
    this.error.set(null);
    this.api.getModelCatalog().subscribe({
      next: (catalog) => {
        this.catalog.set(catalog);
        const catalogLoading = catalog.nodes.some((node) =>
          node.providers.some((provider) => provider.state === 'loading'),
        );
        this.loading.set(catalogLoading);
        if (catalogLoading) this.pollTimer = window.setTimeout(() => this.refresh(), 750);
      },
      error: () => {
        this.loading.set(false);
        this.error.set('Impossible de lire les modèles annoncés par les nœuds.');
      },
    });
  }
  protected enabled(provider: string, model: string): boolean {
    return !this.catalog()?.policy.disabledModels.some(
      (item) => item.provider === provider && item.model === model,
    );
  }
  protected toggleCollapsed(id: string): void {
    this.collapsed.update((value) => {
      const next = new Set(value);
      if (!next.delete(id)) next.add(id);
      return next;
    });
  }
  protected setAll(providerId: string, enabled: boolean): void {
    const group = this.groups().find((item) => item.id === providerId);
    const pending = group?.models.filter((entry) => entry.enabled !== enabled) ?? [];
    if (!pending.length) return;
    this.saving.set(true);
    concat(
      ...pending.map((entry) => this.api.updateModelPolicy(providerId, entry.model.id, enabled)),
    )
      .pipe(last())
      .subscribe({
        next: (policy) => {
          this.catalog.update((value) => (value ? { ...value, policy } : value));
          this.saving.set(false);
          this.notifications.show(enabled ? 'Modèles activés.' : 'Modèles désactivés.');
        },
        error: () => {
          this.saving.set(false);
          this.notifications.show('Impossible de modifier ces modèles.');
          this.refresh();
        },
      });
  }
  private buildGroups(catalog: AiClusterModelCatalog | null): readonly ProviderGroup[] {
    if (!catalog) return [];
    const nodeCount = catalog.nodes.length;
    const order: string[] = [];
    const byProvider = new Map<
      string,
      {
        models: Map<string, { model: AiDiscoveredModel; nodes: string[] }>;
        available: string[];
        problems: { node: string; state: string; message: string | null }[];
      }
    >();
    for (const node of catalog.nodes) {
      for (const provider of node.providers) {
        let entry = byProvider.get(provider.provider);
        if (!entry) {
          entry = { models: new Map(), available: [], problems: [] };
          byProvider.set(provider.provider, entry);
          order.push(provider.provider);
        }
        if (provider.state === 'available' || provider.state === 'stale') {
          entry.available.push(node.name);
          for (const model of provider.models) {
            const known = entry.models.get(model.id);
            if (known) known.nodes.push(node.name);
            else entry.models.set(model.id, { model, nodes: [node.name] });
          }
        }
        if (provider.state !== 'available')
          entry.problems.push({
            node: node.name,
            state: provider.state,
            message: provider.message ?? null,
          });
      }
    }
    return order.map((id) => {
      const entry = byProvider.get(id)!;
      const loading = entry.problems.some((problem) => problem.state === 'loading');
      const models = [...entry.models.values()].map(({ model, nodes }) => ({
        model,
        enabled: this.enabled(id, model.id),
        nodes:
          nodeCount > 1 && nodes.length < entry.available.length
            ? (nodes.length === 1 ? 'seulement ' : 'sur ') + nodes.join(', ')
            : '',
      }));
      const unavailable = entry.available.length === 0;
      const partial = !unavailable && entry.available.length < nodeCount;
      const message = entry.problems
        .filter((problem) => problem.state !== 'loading')
        .map((problem) => `${problem.node} : ${problem.message ?? 'catalogue indisponible'}`)
        .join(' · ');
      return {
        id,
        name: aiTypeLabel(id),
        stateLabel: loading
          ? 'Lecture en cours'
          : unavailable
            ? 'Indisponible'
            : partial
              ? 'Partiellement disponible'
              : 'Disponible',
        stateTone: loading || unavailable ? 'muted' : partial ? 'warning' : 'ok',
        nodeSummary:
          nodeCount > 1 && !loading
            ? `${entry.available.length} ${entry.available.length > 1 ? 'nœuds' : 'nœud'} sur ${nodeCount}`
            : '',
        message: message || null,
        total: models.length,
        models,
        allEnabled: models.every((entry) => entry.enabled),
      } satisfies ProviderGroup;
    });
  }
  protected setEnabled(provider: string, model: string, enabled: boolean): void {
    this.saving.set(true);
    this.api.updateModelPolicy(provider, model, enabled).subscribe({
      next: (policy) => {
        this.catalog.update((value) => (value ? { ...value, policy } : value));
        this.saving.set(false);
        this.notifications.show(enabled ? 'Modèle activé.' : 'Modèle désactivé.');
      },
      error: () => {
        this.saving.set(false);
        this.notifications.show('Impossible de modifier ce modèle.');
        this.refresh();
      },
    });
  }
}
