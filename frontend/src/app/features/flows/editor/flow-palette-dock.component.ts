import { model } from '@angular/core';
import { IconComponent } from '../../../shared/ui/icon.component';
import { CdkDrag } from '@angular/cdk/drag-drop';
import { FlowPaletteDrag } from '../../../shared/ui/flow-palette-drag';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatTooltipModule } from '@angular/material/tooltip';
import {
  FLOW_COMPONENT_CATEGORIES,
  FlowComponentCategory,
  FlowComponentDefinition,
  isFlowComponentCompatible,
} from '../../../shared/models/flow-component';
import { FlowComponentPackSummary } from '../../../shared/models/flow-component-packs';
import { flowComponentColors } from '../../../shared/models/flow-presentation';
import { MediaType } from '../../../shared/models/media-type';

@Component({
  selector: 'app-flow-palette-dock',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent, CdkDrag, MatButtonModule, MatTooltipModule],
  template: `
    <aside
      class="palette-dock"
      [class.palette-dock_collapsed]="!opened()"
      [class.palette-dock_dragging]="paletteDrag.component() !== null"
      aria-label="Palette des composants du flow"
    >
      @if (opened()) {
        <div class="dock-header">
          <div class="header-title-row">
            <span class="dock-glyph" aria-hidden="true">▦</span>
            <h2 class="dock-title">Composants & Packs</h2>
            <button
              mat-icon-button
              type="button"
              [attr.aria-pressed]="pinned()"
              aria-label="Garder la palette ouverte après un ajout"
              matTooltip="Garder la palette ouverte"
              (click)="pinned.set(!pinned())"
            >
              <app-icon name="pin" />
            </button>
            <button
              type="button"
              class="dock-close-btn"
              (click)="openedChange.emit(false)"
              matTooltip="Masquer la palette (Ctrl+B)"
              aria-label="Masquer la palette"
            >
              <app-icon name="close" />
            </button>
          </div>

          @if (insertionPending()) {
            <div class="insertion-banner" role="status">
              <div class="banner-text">
                <span class="banner-indicator" aria-hidden="true">●</span>
                <span>
                  {{ destinationLabel() ?? 'Choisissez un composant à insérer sur la liaison' }}
                </span>
              </div>
              <button
                type="button"
                class="cancel-insertion-btn"
                (click)="cancelInsertion.emit()"
                aria-label="Annuler l’insertion"
              >
                Annuler
              </button>
            </div>
          }

          <div class="search-box">
            <span class="search-icon" aria-hidden="true">🔍</span>
            <input
              type="search"
              class="search-input"
              placeholder="Rechercher un composant… (/)"
              [value]="searchQuery()"
              (input)="onSearchInput($event)"
              aria-label="Rechercher un composant par nom ou description"
            />
            @if (searchQuery()) {
              <button
                type="button"
                class="clear-search-btn"
                (click)="clearSearch()"
                aria-label="Effacer la recherche"
              >
                ✕
              </button>
            }
          </div>

          <details class="plugin-filter">
            <summary>
              <span>Affiner</span>
              @if (activePlugin() !== 'all') {
                <span class="active-filter">{{ activePluginName() }}</span>
              } @else {
                <span class="catalog-summary">{{ pluginOptions().length }} plugins</span>
              }
            </summary>
            <label for="flow-palette-plugin">Plugin</label>
            <select
              id="flow-palette-plugin"
              class="plugin-select"
              [value]="activePlugin()"
              (change)="onPluginChange($event)"
            >
              <option value="all">Tous les plugins</option>
              @for (plugin of pluginOptions(); track plugin.id) {
                <option [value]="plugin.id">
                  {{ plugin.name }} ({{ plugin.available + plugin.other }})
                </option>
              }
            </select>
            @if (activePlugin() !== 'all') {
              <button type="button" class="clear-filter" (click)="activePlugin.set('all')">
                Effacer le filtre
              </button>
            }
          </details>
        </div>

        <div class="dock-body">
          @for (group of categoryGroups(); track group.id) {
            @if (group.components.length) {
              <details class="category-group" [open]="isExpanded(group.id)">
                <summary (click)="toggleCategory($event, group.id)">
                  <span>{{ group.label }}</span>
                  <span class="category-count">{{ group.components.length }}</span>
                </summary>
                <div class="catalog-list">
                  @for (component of group.components; track component.id) {
                    <div
                      class="component-card"
                      cdkDrag
                      [cdkDragDisabled]="isUnavailable(component) || component.kind === 'input'"
                      [cdkDragStartDelay]="{ mouse: 0, touch: 200 }"
                      (cdkDragStarted)="paletteDrag.component.set(component)"
                      (cdkDragMoved)="paletteDrag.move($event.pointerPosition)"
                      (cdkDragEnded)="paletteDrag.finish($event.dropPoint); $event.source.reset()"
                      tabindex="0"
                      role="button"
                      [style.--component-accent]="colors(component).color"
                      [class.component-card_unavailable]="isUnavailable(component)"
                      [attr.aria-disabled]="isUnavailable(component)"
                      (click)="selectComponent(component)"
                      (keydown.enter)="selectComponent(component)"
                      [matTooltip]="componentTooltip(component)"
                    >
                      <span class="component-kind">{{ kindLabel(component.kind) }}</span>
                      <span class="component-title">{{ component.name }}</span>
                      <span class="component-plugin">{{ component.pluginName }}</span>
                      <span class="add-hint" aria-hidden="true">+</span>
                    </div>
                  }
                </div>
              </details>
            }
          }
          @if (filteredComponents().length === 0) {
            <p class="empty-state">Aucun composant ne correspond à cette recherche.</p>
          }
          <details class="category-group pack-group" [open]="isExpanded('packs')">
            <summary (click)="toggleCategory($event, 'packs')">
              <span>Packs</span>
              <span class="category-count">{{ filteredPacks().length }}</span>
            </summary>
            @if (filteredPacks().length) {
              <div class="catalog-list">
                @for (pack of filteredPacks(); track pack.id) {
                  <button
                    type="button"
                    class="pack-card"
                    (click)="packSelected.emit(pack)"
                    matTooltip="Insérer ce pack dans le flow"
                  >
                    <span class="pack-name">{{ pack.name }}</span>
                    <span class="pack-badge"
                      >{{ pack.componentCount }} blocs · {{ pack.mediaType }}</span
                    >
                  </button>
                }
              </div>
            } @else {
              <p class="empty-state">Aucun pack disponible.</p>
            }
          </details>
        </div>
      }
    </aside>
    @if (paletteDrag.component(); as preview) {
      <div
        class="palette-drag-preview"
        [style.--component-accent]="colors(preview).color"
        [style.left.px]="paletteDrag.pointer().x + 16"
        [style.top.px]="paletteDrag.pointer().y + 16"
        aria-hidden="true"
      >
        <span class="component-kind">{{ kindLabel(preview.kind) }}</span>
        <span class="component-title">{{ preview.name }}</span>
        <span class="component-plugin">{{ preview.pluginName }}</span>
      </div>
    }
  `,
  styles: `
    :host {
      display: block;
      height: 100%;
    }
    .palette-drag-preview {
      position: fixed;
      z-index: 100;
      display: grid;
      grid-template-columns: 48px minmax(0, 1fr) auto;
      align-items: center;
      gap: 6px;
      width: 260px;
      min-height: 38px;
      padding: 5px 8px;
      border: 1px solid var(--accent);
      border-radius: var(--radius-sm);
      background: var(--surface-container-highest);
      box-shadow: var(--elevation-3);
      opacity: 0.96;
      pointer-events: none;
      transform: translate(-50%, -50%);
    }
    .palette-dock {
      width: 320px;
      height: 100%;
      background: var(--surface-container-low);
      border-right: 1px solid var(--border-subtle);
      display: flex;
      flex-direction: column;
      transition: width 0.22s var(--motion-emphasized);
      overflow: hidden;
      &_collapsed {
        width: 0;
        border-right: none;
      }
    }
    @media (max-width: 768px) {
      .palette-dock_dragging {
        width: 48px;
        transition: none;
      }
    }
    .dock-header {
      padding: 12px 12px 8px;
      border-bottom: 1px solid var(--border-subtle);
      display: flex;
      flex-direction: column;
      gap: 8px;
      background: var(--surface-container);
    }
    .header-title-row {
      display: flex;
      align-items: center;
      gap: 8px;
    }
    .dock-glyph {
      font-size: 16px;
      color: var(--accent);
    }
    .dock-title {
      margin: 0;
      font-size: var(--font-title);
      font-weight: 700;
      color: var(--text-primary);
      flex: 1;
    }
    .dock-close-btn {
      width: 28px;
      height: 28px;
      border-radius: var(--radius-xs);
      border: none;
      background: transparent;
      color: var(--text-secondary);
      font-size: 20px;
      line-height: 1;
      display: grid;
      place-items: center;
      cursor: pointer;
      &:hover {
        background: var(--surface-container-highest);
        color: var(--text-primary);
      }
    }
    .insertion-banner {
      background: var(--accent-soft);
      border-radius: var(--radius-sm);
      padding: 6px 10px;
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 8px;
      font-size: 11px;
      color: var(--accent);
      font-weight: 600;
    }
    .banner-text {
      display: flex;
      align-items: center;
      gap: 6px;
      min-width: 0;
      line-height: 1.3;
    }
    .banner-indicator {
      font-size: 8px;
      animation: blink 1.2s infinite ease-in-out;
    }
    @keyframes blink {
      0%,
      100% {
        opacity: 1;
      }
      50% {
        opacity: 0.3;
      }
    }
    .cancel-insertion-btn {
      border: none;
      background: transparent;
      color: var(--text-primary);
      font-size: 11px;
      font-weight: 700;
      cursor: pointer;
      text-decoration: underline;
      white-space: nowrap;
    }
    .search-box {
      position: relative;
      display: flex;
      align-items: center;
    }
    .search-icon {
      position: absolute;
      left: 10px;
      font-size: 12px;
      color: var(--text-muted);
      pointer-events: none;
    }
    .search-input {
      width: 100%;
      height: 32px;
      padding: 0 28px 0 30px;
      background: var(--surface);
      border: 1px solid var(--border-subtle);
      border-radius: var(--radius-pill);
      color: var(--text-primary);
      font-size: 12px;
      outline: none;
      &:focus {
        border-color: var(--accent);
      }
    }
    .clear-search-btn {
      position: absolute;
      right: 8px;
      border: none;
      background: transparent;
      color: var(--text-muted);
      cursor: pointer;
      font-size: 12px;
    }
    .plugin-filter {
      display: grid;
      gap: 8px;
      color: var(--text-secondary);
      font-size: 12px;
    }
    .plugin-filter summary,
    .category-group summary {
      min-height: 34px;
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 8px;
      cursor: pointer;
      list-style: none;
    }
    .plugin-filter summary::-webkit-details-marker,
    .category-group summary::-webkit-details-marker {
      display: none;
    }
    .plugin-filter summary::before,
    .category-group summary::before {
      content: '›';
      font-size: 18px;
      color: var(--text-muted);
      transform: rotate(0deg);
      transition: transform 0.15s ease;
    }
    .plugin-filter[open] summary::before,
    .category-group[open] summary::before {
      transform: rotate(90deg);
    }
    .plugin-filter summary > span:first-of-type,
    .category-group summary > span:first-of-type {
      flex: 1;
    }
    .active-filter,
    .category-count {
      padding: 2px 8px;
      border-radius: var(--radius-pill);
      background: var(--surface-container-highest);
      color: var(--text-secondary);
      font-size: 11px;
    }
    .clear-filter {
      justify-self: start;
      border: 0;
      padding: 0;
      background: transparent;
      color: var(--accent);
      font: inherit;
      cursor: pointer;
    }
    .plugin-filter label {
      color: var(--text-secondary);
      font-size: 12px;
      font-weight: 700;
      letter-spacing: 0.03em;
      text-transform: uppercase;
    }
    .plugin-select {
      width: 100%;
      min-width: 0;
      height: 34px;
      padding: 0 12px;
      border: 1px solid var(--border-subtle);
      border-radius: var(--radius-pill);
      background: var(--surface);
      color: var(--text-primary);
      font: inherit;
      font-size: 12px;
    }
    .plugin-select:focus-visible {
      outline: 2px solid var(--accent);
      outline-offset: 2px;
    }
    .catalog-summary {
      margin: 0;
      color: var(--text-muted);
      font-size: 12px;
    }
    .dock-body {
      flex: 1;
      overflow-y: auto;
      padding: 10px;
    }
    .catalog-list {
      display: flex;
      flex-direction: column;
      gap: 2px;
      padding-bottom: 4px;
    }
    .category-group {
      border-bottom: 1px solid var(--border-subtle);
      padding: 0 4px;
    }
    .category-group summary {
      min-height: 40px;
      color: var(--text-primary);
      font-size: 13px;
      font-weight: 600;
    }
    .pack-group {
      margin-top: 8px;
    }
    .component-card {
      min-height: 38px;
      padding: 5px 8px;
      background: transparent;
      border-radius: var(--radius-sm);
      cursor: pointer;
      display: grid;
      grid-template-columns: 48px minmax(0, 1fr) auto 18px;
      align-items: center;
      gap: 6px;
      transition: background 0.15s ease;
      &:hover {
        background: var(--surface-container-high);
      }
      &:focus-visible {
        outline: 2px solid var(--accent);
      }
    }
    .component-card {
      border-left: 2px solid var(--component-accent, var(--accent));
    }
    .component-card.cdk-drag:not(.cdk-drag-disabled) {
      cursor: grab;
    }
    .component-card.cdk-drag-preview {
      box-shadow: var(--elevation-3);
      cursor: grabbing;
    }
    .component-card_unavailable {
      opacity: 0.68;
      border-style: dashed;
      cursor: not-allowed;
      &:hover {
        transform: none;
        box-shadow: none;
      }
    }
    .component-kind {
      font-size: 12px;
      font-weight: 600;
      color: var(--component-accent, var(--accent));
    }
    .component-plugin {
      color: var(--text-muted);
      font-size: 12px;
    }
    .component-plugin {
      white-space: nowrap;
    }
    .component-title {
      font-size: 12px;
      font-weight: 500;
      color: var(--text-primary);
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .add-hint {
      color: var(--accent);
      font-weight: 600;
      text-align: center;
    }
    .unavailable-tag {
      color: var(--text-muted);
      font-size: 12px;
      font-weight: 600;
    }
    .pack-card {
      width: 100%;
      min-height: 38px;
      padding: 6px 8px;
      border: 1px dashed var(--accent);
      border-radius: var(--radius-sm);
      background: var(--surface);
      color: var(--text-primary);
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 8px;
      text-align: left;
      cursor: pointer;
    }
    .pack-name {
      font-size: 12px;
      font-weight: 600;
    }
    .pack-badge {
      font-size: 12px;
      padding: 2px 6px;
      border-radius: var(--radius-pill);
      background: var(--accent-soft);
      color: var(--accent);
      font-weight: 600;
    }
    .empty-state {
      padding: 24px 12px;
      text-align: center;
      color: var(--text-muted);
      font-size: 12px;
      line-height: 1.5;
    }
  `,
})
export class FlowPaletteDockComponent {
  protected readonly paletteDrag = inject(FlowPaletteDrag);
  readonly opened = input(true);
  readonly components = input<readonly FlowComponentDefinition[]>([]);
  readonly otherMediaComponents = input<readonly FlowComponentDefinition[]>([]);
  readonly flowMediaType = input<MediaType>('video');
  readonly packCatalog = input<readonly FlowComponentPackSummary[]>([]);
  readonly insertionPending = input(false);
  readonly destinationLabel = input<string | null>(null);

  readonly openedChange = output<boolean>();
  readonly pinned = model(false);
  readonly componentSelected = output<FlowComponentDefinition>();
  readonly packSelected = output<FlowComponentPackSummary>();
  readonly cancelInsertion = output<void>();

  protected readonly searchQuery = signal('');
  protected readonly expandedCategories = signal<ReadonlySet<string>>(new Set(['video']));
  protected readonly activePlugin = signal('all');
  protected readonly pluginOptions = computed(() => {
    const counts = new Map<string, { name: string; available: number; other: number }>();
    for (const component of this.components()) {
      const current = counts.get(component.pluginId) ?? {
        name: component.pluginName,
        available: 0,
        other: 0,
      };
      counts.set(component.pluginId, {
        name: component.pluginName,
        available: current.available + 1,
        other: current.other,
      });
    }
    for (const component of this.otherMediaComponents()) {
      const current = counts.get(component.pluginId) ?? {
        name: component.pluginName,
        available: 0,
        other: 0,
      };
      counts.set(component.pluginId, {
        name: component.pluginName,
        available: current.available,
        other: current.other + 1,
      });
    }
    return [...counts.entries()]
      .map(([id, plugin]) => ({ id, ...plugin }))
      .sort((left, right) => left.name.localeCompare(right.name, 'fr'));
  });

  protected readonly activePluginName = computed(
    () => this.pluginOptions().find((plugin) => plugin.id === this.activePlugin())?.name ?? '',
  );

  protected readonly filteredComponents = computed(() => {
    const query = this.searchQuery().trim().toLowerCase();
    const plugin = this.activePlugin();
    return [...this.components(), ...this.otherMediaComponents()].filter((comp) => {
      if (plugin !== 'all' && comp.pluginId !== plugin) return false;
      if (!query) return true;
      return (
        comp.id.toLowerCase().includes(query) ||
        comp.name.toLowerCase().includes(query) ||
        comp.description.toLowerCase().includes(query) ||
        comp.pluginName.toLowerCase().includes(query) ||
        comp.pluginId.toLowerCase().includes(query)
      );
    });
  });

  protected readonly categoryGroups = computed(() => {
    const components = this.filteredComponents();
    return (Object.entries(FLOW_COMPONENT_CATEGORIES) as [FlowComponentCategory, string][])
      .map(([id, label]) => ({
        id,
        label,
        components: components.filter((component) => (component.category ?? 'utilities') === id),
      }))
      .filter((group) => group.components.length > 0);
  });

  protected isExpanded(id: string): boolean {
    return this.searchQuery().trim().length > 0 || this.expandedCategories().has(id);
  }

  protected toggleCategory(event: MouseEvent, id: string): void {
    event.preventDefault();
    this.expandedCategories.update((current) => {
      const next = new Set(current);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  protected onPluginChange(event: Event): void {
    this.activePlugin.set((event.target as HTMLSelectElement).value);
  }

  protected isUnavailable(component: FlowComponentDefinition): boolean {
    return !isFlowComponentCompatible(component, this.flowMediaType());
  }

  protected componentTooltip(component: FlowComponentDefinition): string {
    if (!this.isUnavailable(component)) return component.description;
    const supported = component.supportedFlowMediaTypes?.length
      ? component.supportedFlowMediaTypes.map((type) => this.mediaTypeLabel(type)).join(', ')
      : this.mediaTypeLabel(component.mediaType as MediaType);
    return `Non disponible dans un flow ${this.mediaTypeLabel(this.flowMediaType())}. Compatible avec : ${supported}.`;
  }

  protected selectComponent(component: FlowComponentDefinition): void {
    if (this.isUnavailable(component)) return;
    this.componentSelected.emit(component);
  }

  private mediaTypeLabel(type: MediaType): string {
    return (
      {
        video: 'vidéo',
        image: 'image',
        music: 'musique',
        subtitles: 'sous-titres',
      }[type] ?? 'générique'
    );
  }

  protected readonly filteredPacks = computed(() => {
    const query = this.searchQuery().trim().toLowerCase();
    return this.packCatalog().filter((pack) => {
      if (!query) return true;
      return pack.name.toLowerCase().includes(query);
    });
  });

  protected colors(component: FlowComponentDefinition) {
    return flowComponentColors(component);
  }

  protected kindLabel(kind: string): string {
    switch (kind) {
      case 'action':
        return 'Action';
      case 'condition':
        return 'Condition';
      case 'tool':
        return 'Outil';
      case 'variable':
        return 'Variable';
      case 'input':
        return 'Entrée';
      default:
        return kind;
    }
  }

  protected onSearchInput(event: Event): void {
    const target = event.target as HTMLInputElement;
    this.searchQuery.set(target.value);
  }

  protected clearSearch(): void {
    this.searchQuery.set('');
  }
}
