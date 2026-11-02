import { ResourceListItem } from '../../shared/ui/resource-list.component';
import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { firstValueFrom, map } from 'rxjs';
import { DataTableRow } from '../../shared/ui/data-table.component';
import {
  ResourceSheetComponent,
  ResourceSheetSection,
} from '../../shared/ui/resource-sheet.component';
import { ResourceCatalogTemplateComponent } from '../../shared/templates/resource-catalog-template.component';
import { LibrarySectionTabsComponent } from './library-section-tabs.component';
import { FlowComponentPacks } from '../../shared/services/flow-component-packs';
import { FlowComponentPackSummary } from '../../shared/models/flow-component-packs';
import { FlowComponentPackDetails } from '../../shared/models/flow-component-packs';
import { MEDIA_TYPE_OPTIONS } from '../../shared/models/media-type';
import { PageShellContextService } from '../../core/layout/page-shell-context';

interface PackRow extends DataTableRow {
  readonly id: string;
  readonly name: string;
  readonly mediaType: string;
  readonly components: string;
  readonly source: string;
  readonly version: string;
  readonly description: string;
}

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-component-pack-library-page',
  imports: [ResourceCatalogTemplateComponent, LibrarySectionTabsComponent, ResourceSheetComponent],
  template: `
    <app-resource-catalog-template
      [title]="title()"
      [items]="items()"
      [showToolbar]="false"
      resourceIcon="pack"
      listEmptyMessage="Aucun pack. Créez-en un depuis une version publiée dans Flows."
      openLabel="Ouvrir"
      (open)="openPack({ id: $event })"
      [catalogState]="catalogState()"
      [catalogMessage]="catalogMessage()"
      [detailOpen]="selectedPack() !== null"
      [detailTitle]="selectedPack()?.name ?? 'Pack de composants'"
      [detailDescription]="selectedPack()?.description ?? ''"
      detailLayout="drawer"
      (detailClosed)="closeDetail()"
    >
      <ng-template #catalogueNavigation>
        <app-library-section-tabs catalogue-navigation selected="packs" />
      </ng-template>
      <ng-template #detailContent catalogue-detail>
        @if (selectedPack(); as pack) {
          <app-resource-sheet
            ariaLabel="Résumé, origine et interface du pack"
            [title]="pack.name"
            [subtitle]="packSubtitle()"
            [sections]="packSections()"
          />
        }
      </ng-template>
    </app-resource-catalog-template>
  `,
})
export class ComponentPackLibraryPageComponent {
  private readonly api = inject(FlowComponentPacks);
  private readonly destroyRef = inject(DestroyRef);
  private readonly pageShellContext = inject(PageShellContextService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly routeData = toSignal(this.route.data, {
    initialValue: this.route.snapshot.data,
  });
  protected readonly title = computed(
    () => (this.routeData()['title'] as string) ?? 'Packs de composants',
  );
  protected readonly catalogState = signal<'ready' | 'loading' | 'error'>('loading');
  protected readonly catalogMessage = signal('Chargement des packs…');
  protected readonly packs = signal<readonly FlowComponentPackSummary[]>([]);
  protected readonly selectedPack = signal<FlowComponentPackSummary | null>(null);
  private readonly selectedDetails = signal<FlowComponentPackDetails | null>(null);
  private readonly selectedDetailsLoadState = signal<'idle' | 'loading' | 'ready' | 'error'>(
    'idle',
  );
  protected readonly query = signal('');
  protected readonly rows = computed<readonly PackRow[]>(() =>
    this.packs().map((pack) => ({
      id: pack.id,
      name: pack.name,
      mediaType:
        MEDIA_TYPE_OPTIONS.find((item) => item.value === pack.mediaType)?.label ?? pack.mediaType,
      components: String(pack.componentCount),
      source: pack.sourceFlowName,
      version: `v${pack.sourceVersion}`,
      description: pack.description,
    })),
  );
  protected readonly filteredRows = computed(() => {
    const query = this.query().trim().toLocaleLowerCase();
    return query
      ? this.rows().filter((row) =>
          [row.name, row.mediaType, row.source, row.description].some((value) =>
            value.toLocaleLowerCase().includes(query),
          ),
        )
      : this.rows();
  });
  protected readonly items = computed<readonly ResourceListItem[]>(() =>
    this.filteredRows().map((row) => ({
      id: row.id,
      name: row.name,
      subtitle: row.description || undefined,
      tags: [
        { label: row.mediaType },
        { label: `${row.components} composant(s)` },
        { label: `${row.source} · ${row.version}`, tone: 'ok' as const },
      ],
    })),
  );
  protected readonly packSubtitle = computed(() => {
    const pack = this.selectedPack();
    if (!pack) return '';
    return (
      MEDIA_TYPE_OPTIONS.find((item) => item.value === pack.mediaType)?.label ?? pack.mediaType
    );
  });
  protected readonly packSections = computed<readonly ResourceSheetSection[]>(() => {
    const pack = this.selectedPack();
    if (!pack) return [];

    const details = this.selectedDetails();
    const inputNode = details?.definition.nodes.find((node) => node.id === details.inputNodeId);
    const inputLabel = inputNode?.name || inputNode?.component.componentId || 'Premier composant';
    const loadState = this.selectedDetailsLoadState();
    const interfaceStatus =
      loadState === 'ready'
        ? { label: 'Détails chargés', tone: 'success' as const }
        : loadState === 'error'
          ? { label: 'Indisponible', tone: 'warning' as const }
          : { label: 'Chargement', tone: 'neutral' as const };
    const mediaType =
      MEDIA_TYPE_OPTIONS.find((item) => item.value === pack.mediaType)?.label ?? pack.mediaType;

    return [
      {
        id: 'pack-summary',
        title: 'Pack de composants',
        caption: mediaType,
        items: [
          {
            label: pack.name,
            detail: pack.description || 'Aucune description.',
            badges: [
              { label: 'Disponible', tone: 'success' },
              { label: `${pack.componentCount} composant(s)` },
              { label: new Date(pack.createdAt).toLocaleDateString('fr-FR'), tone: 'neutral' },
            ],
          },
        ],
      },
      {
        id: 'pack-source',
        title: 'Flow source',
        caption: `Version publiée v${pack.sourceVersion}`,
        fields: [
          { label: 'Flow', value: pack.sourceFlowName },
          { label: 'Identifiant', value: pack.sourceFlowId, mono: true },
          { label: 'Version', value: `v${pack.sourceVersion}` },
        ],
      },
      {
        id: 'pack-interface',
        title: 'Interface du pack',
        caption: 'Entrée et sorties exposées',
        items: [
          {
            label: details ? inputLabel : 'Entrée du pack',
            detail: details
              ? 'Point d’entrée public du fragment'
              : loadState === 'error'
                ? 'Impossible de charger la définition du pack.'
                : 'Chargement de la définition…',
            badges: [interfaceStatus],
          },
          ...(details?.outputs.length
            ? details.outputs.map((output) => ({
                label: output.label,
                detail: 'Sortie exposée',
              }))
            : [
                {
                  label: 'Interface',
                  detail: details
                    ? 'Aucune sortie exposée'
                    : loadState === 'error'
                      ? 'Détail indisponible'
                      : 'Chargement…',
                },
              ]),
        ],
      },
    ];
  });

  private readonly itemParam = toSignal(
    this.route.queryParamMap.pipe(map((params) => params.get('pack'))),
    { initialValue: this.route.snapshot.queryParamMap.get('pack') },
  );

  constructor() {
    this.destroyRef.onDestroy(
      this.pageShellContext.register({
        searchPlaceholder: 'Rechercher dans les packs de composants…',
        query: this.query,
        onSearchQueryChange: (query) => this.query.set(query),
      }),
    );
    void this.loadPacks();
    // La fiche est portée par l'URL (?pack=<id>) : le menu latéral et le bouton précédent
    // du navigateur ramènent à la liste.
    effect(() => {
      const id = this.itemParam();
      const items = this.packs();
      untracked(() => {
        if (!id) {
          if (this.selectedPack()) this.clearDetailState();
          return;
        }
        if (this.selectedPack()?.id === id) return;
        const item = items.find((candidate) => candidate.id === id);
        if (item) void this.showPack(item);
      });
    });
  }

  protected openPack(row: DataTableRow): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { pack: String(row['id']) },
    });
  }

  private async showPack(pack: FlowComponentPackSummary): Promise<void> {
    this.selectedPack.set(pack);
    this.selectedDetails.set(null);
    this.selectedDetailsLoadState.set('loading');
    try {
      const details = await this.api.get(pack.id);
      if (this.selectedPack()?.id === pack.id) {
        this.selectedDetails.set(details);
        this.selectedDetailsLoadState.set('ready');
      }
    } catch {
      if (this.selectedPack()?.id === pack.id) {
        this.selectedDetails.set(null);
        this.selectedDetailsLoadState.set('error');
      }
    }
  }

  protected closeDetail(): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { pack: null },
      queryParamsHandling: 'merge',
    });
  }

  private clearDetailState(): void {
    this.selectedPack.set(null);
    this.selectedDetails.set(null);
    this.selectedDetailsLoadState.set('idle');
  }

  private async loadPacks(): Promise<void> {
    try {
      this.packs.set(await firstValueFrom(this.api.list()));
      this.catalogState.set('ready');
      this.catalogMessage.set('');
    } catch (error) {
      this.catalogState.set('error');
      this.catalogMessage.set(
        error instanceof HttpErrorResponse
          ? (error.error?.title ?? 'Les packs ne sont pas disponibles.')
          : 'Les packs ne sont pas disponibles.',
      );
    }
  }
}
