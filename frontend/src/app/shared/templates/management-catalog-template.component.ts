import {
  ChangeDetectionStrategy,
  Component,
  TemplateRef,
  computed,
  contentChild,
  input,
  output,
} from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { DetailDrawerComponent } from '../ui/detail-drawer/detail-drawer';
import { SearchFieldComponent } from '../ui/search-field.component';
import { PageHeaderComponent } from '../ui/page-header.component';
import { MatButtonModule } from '@angular/material/button';

export interface CatalogEmptyState {
  readonly icon: string;
  readonly eyebrow: string;
  readonly title: string;
  readonly description: string;
  readonly chips?: readonly string[];
  readonly actionLabel?: string;
  readonly footnote?: string;
}

@Component({
  selector: 'app-management-catalog-template',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    DetailDrawerComponent,
    MatButtonModule,
    NgTemplateOutlet,
    PageHeaderComponent,
    SearchFieldComponent,
  ],
  template: `
    <app-detail-drawer
      mainScroll="parent"
      [opened]="actionOpen() || (detailOpen() && detailLayout() === 'drawer')"
      [size]="drawerSize()"
      [headerDensity]="drawerHeaderDensity()"
      [bodyLayout]="actionOpen() ? 'scroll' : detailBodyLayout()"
      [modal]="drawerModal()"
      [title]="actionOpen() ? actionTitle() : detailTitle()"
      [description]="actionOpen() ? actionDescription() : detailDescription()"
      [content]="
        actionOpen() ? actionContentTemplate() : detailOpen() ? detailContentTemplate() : undefined
      "
      [headerActions]="
        actionOpen() || detailLayout() === 'page' ? undefined : detailHeaderActionsTemplate()
      "
      [footerActions]="
        actionOpen()
          ? actionFooterActionsTemplate()
          : detailOpen()
            ? detailFooterActionsTemplate()
            : undefined
      "
      [step]="actionOpen() ? actionStep() : null"
      [closeGuarded]="actionOpen() && actionCloseGuarded()"
      (closeRequested)="actionCloseRequested.emit()"
      (closed)="onDrawerClosed()"
    >
      <div drawer-main>
        @if (detailOpen() && detailLayout() === 'page') {
          <app-page-header
            [title]="detailTitle()"
            [description]="detailDescription()"
            [actions]="detailHeaderActionsTemplate()"
          />
          <ng-container [ngTemplateOutlet]="detailContentTemplate()" />
        } @else {
          <app-page-header
            [title]="title()"
            [actions]="pageActionsTemplate()"
            [visuallyHidden]="titleVisuallyHidden()"
          />
          @if (catalogueNavigationTemplate(); as navigation) {
            <ng-container [ngTemplateOutlet]="navigation" />
          } @else {
            <ng-content select="[catalogue-navigation]" />
          }
          <div class="catalog-summary">
            @if (catalogueSummaryTemplate(); as summary) {
              <ng-container [ngTemplateOutlet]="summary" />
            } @else {
              <ng-content select="[catalogue-summary]" />
            }
          </div>

          <section class="catalog-panel" [attr.aria-label]="title()">
            @if (showToolbar()) {
              <ng-template #filtersContent>
                @if (catalogueFiltersTemplate(); as filters) {
                  <ng-container [ngTemplateOutlet]="filters" />
                } @else {
                  <ng-content select="[catalogue-filters]" />
                }
              </ng-template>
              <div class="catalog-toolbar">
                <div class="catalog-filters">
                  @if (filterLabel() && searchLayout() === 'end') {
                    <span class="catalog-filter-label">{{ filterLabel() }}</span>
                  }
                  <div
                    class="catalog-toolbar-row"
                    [class.is-centered]="searchLayout() !== 'end'"
                    [class.has-actions]="toolbarActionsTemplate() !== undefined"
                  >
                    @if (searchLayout() === 'end') {
                      <ng-container [ngTemplateOutlet]="filtersContent" />
                    }
                    @if (searchLabel()) {
                      <app-search-field
                        class="catalog-search"
                        variant="bar"
                        [label]="searchLabel()"
                        [value]="searchQuery()"
                        (valueChange)="searchQueryChange.emit($event)"
                        [filterCount]="searchLayout() === 'centered-sheet' ? filterCount() : null"
                      >
                        @if (searchLayout() === 'centered-sheet') {
                          <div search-filters class="catalog-sheet-filters">
                            <ng-container [ngTemplateOutlet]="filtersContent" />
                          </div>
                        }
                      </app-search-field>
                    }
                    @if (searchLayout() === 'centered') {
                      <ng-container [ngTemplateOutlet]="filtersContent" />
                    }
                    @if (searchLayout() === 'centered-sheet') {
                      @if (catalogueActiveFiltersTemplate(); as activeFilters) {
                        <ng-container [ngTemplateOutlet]="activeFilters" />
                      } @else {
                        <ng-content select="[catalogue-active-filters]" />
                      }
                    }
                    @if (toolbarActionsTemplate(); as actions) {
                      <div class="catalog-toolbar-actions">
                        <ng-container [ngTemplateOutlet]="actions" />
                      </div>
                    }
                  </div>
                </div>
              </div>
            }
            @if (catalogState() === 'loading') {
              <p class="catalog-state" role="status">{{ catalogMessage() || 'Chargement…' }}</p>
            } @else if (catalogState() === 'error') {
              <p class="catalog-state" role="alert">
                {{ catalogMessage() || 'Les données ne sont pas disponibles.' }}
              </p>
            } @else if (emptyState(); as empty) {
              <section class="catalog-empty" aria-labelledby="catalog-empty-title">
                <div class="catalog-empty-content">
                  <div class="catalog-empty-icon" aria-hidden="true">{{ empty.icon }}</div>
                  <p class="catalog-empty-eyebrow">{{ empty.eyebrow }}</p>
                  <h2 id="catalog-empty-title">{{ empty.title }}</h2>
                  <p class="catalog-empty-description">{{ empty.description }}</p>
                  @if (empty.chips?.length) {
                    <div class="catalog-empty-chips" aria-label="Options disponibles">
                      @for (chip of empty.chips; track chip) {
                        <span>{{ chip }}</span>
                      }
                    </div>
                  }
                  @if (empty.actionLabel) {
                    <button matButton="filled" type="button" (click)="emptyStateAction.emit()">
                      {{ empty.actionLabel }}
                    </button>
                  }
                  @if (empty.footnote) {
                    <p class="catalog-empty-footnote">{{ empty.footnote }}</p>
                  }
                </div>
              </section>
            } @else {
              @if (catalogueTableTemplate(); as table) {
                <ng-container [ngTemplateOutlet]="table" />
              } @else {
                <ng-content select="[catalogue-table]" />
              }
            }
          </section>

          @if (note()) {
            <p class="catalog-note" role="note">{{ note() }}</p>
          }
        }
      </div>

      <div drawer-content>
        <ng-content select="[catalogue-detail]" />
      </div>
    </app-detail-drawer>
  `,
  styleUrl: './management-catalog-template.component.scss',
})
export class ManagementCatalogTemplateComponent {
  readonly title = input.required<string>();
  readonly titleVisuallyHidden = input(false);
  readonly filterLabel = input('Filtres');
  readonly searchLabel = input('');
  readonly searchQuery = input('');
  readonly searchQueryChange = output<string>();
  /**
   * `end` : barre à droite, filtres à côté. `centered` : barre centrée, filtres dessous.
   * `centered-sheet` : barre centrée, filtres dans une feuille ouverte par le bouton Filtres.
   */
  readonly searchLayout = input<'end' | 'centered' | 'centered-sheet'>('end');
  /** Nombre de filtres actifs, affiché sur le bouton Filtres (`centered-sheet`). */
  readonly filterCount = input(0);
  readonly note = input('');
  readonly showToolbar = input(true);
  readonly emptyState = input<CatalogEmptyState | null>(null);
  readonly emptyStateAction = output<void>();
  readonly catalogState = input<'ready' | 'loading' | 'error'>('ready');
  readonly catalogMessage = input('');
  /**
   * `drawer` : la fiche s'ouvre dans un tiroir. `page` : la fiche remplace la liste dans la page ;
   * le tiroir ne sert alors qu'aux formulaires d'action.
   */
  readonly detailLayout = input<'drawer' | 'page'>('drawer');
  readonly detailOpen = input(false);
  readonly detailTitle = input('Détails');
  readonly detailDescription = input('');
  readonly actionOpen = input(false);
  readonly actionCloseGuarded = input(false);
  readonly actionCloseRequested = output<void>();
  readonly actionTitle = input('Ajouter');
  readonly actionDescription = input('');
  /** Avancement d'un formulaire en plusieurs étapes dans le tiroir d'action. */
  readonly actionStep = input<{ readonly current: number; readonly total: number } | null>(null);
  /** Largeur du tiroir : `standard` (560 px) pour une fiche, `wide` pour un éditeur. */
  readonly drawerSize = input<'standard' | 'wide'>('wide');
  /** Densité de l'en-tête des fiches ; reste normale pour les formulaires d'action. */
  readonly drawerHeaderDensity = input<'standard' | 'compact'>('standard');
  /** Le mode `fill` réserve le défilement aux vues imbriquées, comme une fiche à onglets. */
  readonly detailBodyLayout = input<'scroll' | 'fill'>('scroll');
  /** Voile derrière le tiroir ; un clic dessus le ferme. */
  readonly drawerModal = input(true);
  readonly pageActionsInput = input<TemplateRef<unknown> | undefined>();
  readonly toolbarActionsInput = input<TemplateRef<unknown> | undefined>();
  readonly detailContentInput = input<TemplateRef<unknown> | undefined>();
  readonly detailFooterActionsInput = input<TemplateRef<unknown> | undefined>();
  readonly detailHeaderActionsInput = input<TemplateRef<unknown> | undefined>();
  readonly actionFooterActionsInput = input<TemplateRef<unknown> | undefined>();
  readonly actionContentInput = input<TemplateRef<unknown> | undefined>();
  readonly catalogueNavigationInput = input<TemplateRef<unknown> | undefined>();
  readonly catalogueSummaryInput = input<TemplateRef<unknown> | undefined>();
  readonly catalogueFiltersInput = input<TemplateRef<unknown> | undefined>();
  readonly catalogueActiveFiltersInput = input<TemplateRef<unknown> | undefined>();
  readonly catalogueTableInput = input<TemplateRef<unknown> | undefined>();
  readonly detailClosed = output<void>();
  readonly actionClosed = output<void>();
  private readonly pageActionsContent = contentChild('pageActions', { read: TemplateRef });
  private readonly toolbarActionsContent = contentChild('catalogueToolbarActions', {
    read: TemplateRef,
  });
  private readonly detailContentContent = contentChild('detailContent', { read: TemplateRef });
  private readonly detailFooterActionsContent = contentChild('detailFooterActions', {
    read: TemplateRef,
  });
  private readonly detailHeaderActionsContent = contentChild('detailHeaderActions', {
    read: TemplateRef,
  });
  private readonly actionFooterActionsContent = contentChild('actionFooterActions', {
    read: TemplateRef,
  });
  private readonly actionContentContent = contentChild('actionContent', { read: TemplateRef });
  private readonly catalogueNavigationContent = contentChild('catalogueNavigation', {
    read: TemplateRef,
  });
  private readonly catalogueSummaryContent = contentChild('catalogueSummary', {
    read: TemplateRef,
  });
  private readonly catalogueFiltersContent = contentChild('catalogueFilters', {
    read: TemplateRef,
  });
  private readonly catalogueActiveFiltersContent = contentChild('catalogueActiveFilters', {
    read: TemplateRef,
  });
  private readonly catalogueTableContent = contentChild('catalogueTable', {
    read: TemplateRef,
  });

  protected readonly pageActionsTemplate = computed(
    () => this.pageActionsInput() ?? this.pageActionsContent(),
  );
  protected readonly toolbarActionsTemplate = computed(
    () => this.toolbarActionsInput() ?? this.toolbarActionsContent(),
  );
  protected readonly detailContentTemplate = computed(
    () => this.detailContentInput() ?? this.detailContentContent(),
  );
  protected readonly detailFooterActionsTemplate = computed(
    () => this.detailFooterActionsInput() ?? this.detailFooterActionsContent(),
  );
  protected readonly detailHeaderActionsTemplate = computed(
    () => this.detailHeaderActionsInput() ?? this.detailHeaderActionsContent(),
  );
  protected readonly actionFooterActionsTemplate = computed(
    () => this.actionFooterActionsInput() ?? this.actionFooterActionsContent(),
  );
  protected readonly actionContentTemplate = computed(
    () => this.actionContentInput() ?? this.actionContentContent(),
  );
  protected readonly catalogueNavigationTemplate = computed(
    () => this.catalogueNavigationInput() ?? this.catalogueNavigationContent(),
  );
  protected readonly catalogueSummaryTemplate = computed(
    () => this.catalogueSummaryInput() ?? this.catalogueSummaryContent(),
  );
  protected readonly catalogueFiltersTemplate = computed(
    () => this.catalogueFiltersInput() ?? this.catalogueFiltersContent(),
  );
  protected readonly catalogueActiveFiltersTemplate = computed(
    () => this.catalogueActiveFiltersInput() ?? this.catalogueActiveFiltersContent(),
  );
  protected readonly catalogueTableTemplate = computed(
    () => this.catalogueTableInput() ?? this.catalogueTableContent(),
  );

  protected onDrawerClosed(): void {
    if (this.actionOpen()) this.actionClosed.emit();
    else this.detailClosed.emit();
  }
}
