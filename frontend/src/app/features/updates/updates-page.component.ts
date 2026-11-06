import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { catchError, exhaustMap, of, timer } from 'rxjs';
import { ConfigurationSectionComponent } from '../../shared/ui/configuration-section.component';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';
import { EmptyStateComponent } from '../../shared/ui/empty-state/empty-state.component';
import { AppDatePipe } from '../../shared/pipes/app-date.pipe';
import { UpdatesService, UpdateStatus } from '../../shared/services/updates.service';
import {
  PageShellContextService,
  PageShellSearchResult,
} from '../../core/layout/page-shell-context';

@Component({
  selector: 'app-updates-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    AppDatePipe,
    ConfigurationSectionComponent,
    PageHeaderComponent,
    EmptyStateComponent,
    MatProgressBarModule,
  ],
  template: `
    <app-page-header title="Mises à jour" />
    <app-configuration-section title="Version installée" [badge]="data()?.installedVersion ?? ''">
      <div role="status" aria-live="polite">
        @if (loading()) {
          <mat-progress-bar mode="indeterminate" aria-label="Vérification des mises à jour" />
        }
        @if (error()) {
          <p>
            Impossible de charger les informations de version. Une vérification sera retentée
            automatiquement.
          </p>
        }
        @if (data(); as status) {
          @switch (status.status) {
            @case ('available') {
              <p>Une nouvelle version est disponible : {{ status.latestVersion }}.</p>
            }
            @case ('current') {
              <p>Ce worker est à jour.</p>
            }
            @case ('development') {
              <p>Version de développement : recherche de mises à jour désactivée.</p>
            }
            @case ('unconfigured') {
              <p>Aucune source de mises à jour configurée.</p>
            }
            @default {
              <p>Vérification indisponible. Les notes embarquées restent consultables.</p>
            }
          }
          @if (status.checkedAt) {
            <p>Dernière vérification : {{ status.checkedAt | appDate: 'short' }}.</p>
          }
        }
      </div>
      <p>Pour installer une version, mettez à jour l’image Docker du worker.</p>
    </app-configuration-section>
    @if (data(); as status) {
      @for (release of filteredReleases(); track release.version) {
        <app-configuration-section
          [title]="'Version ' + release.version"
          [badge]="release.version === status.installedVersion ? 'Installée sur ce worker' : ''"
          [attr.id]="'release-' + release.version"
        >
          <p>{{ release.publishedAt | appDate: 'date' }}</p>
          <p class="release-notes">{{ release.notes }}</p>
        </app-configuration-section>
      } @empty {
        <app-empty-state
          icon="search"
          [title]="searchQuery().trim() ? 'Aucun résultat' : 'Aucune note de version'"
          [message]="
            searchQuery().trim()
              ? 'Aucune note ne correspond à « ' + searchQuery() + ' ». '
              : 'Aucune note de version embarquée.'
          "
        />
      }
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--space-4);
      min-width: 0;
    }
    .release-notes {
      white-space: pre-wrap;
      overflow-wrap: anywhere;
      line-height: 1.6;
    }
  `,
})
export class UpdatesPageComponent {
  private readonly destroyRef = inject(DestroyRef);
  private readonly pageShellContext = inject(PageShellContextService);
  protected readonly data = signal<UpdateStatus | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal(false);
  protected readonly searchQuery = signal('');
  protected readonly filteredReleases = computed(() => {
    const query = this.searchQuery().trim().toLocaleLowerCase('fr-FR');
    return (this.data()?.releases ?? []).filter(
      (release) =>
        !query ||
        [release.version, release.notes, release.publishedAt].some((value) =>
          value.toLocaleLowerCase('fr-FR').includes(query),
        ),
    );
  });
  private readonly service = inject(UpdatesService);

  constructor() {
    this.destroyRef.onDestroy(
      this.pageShellContext.register({
        searchPlaceholder: 'Rechercher dans les notes de version…',
        query: this.searchQuery,
        onSearchQueryChange: (query) => this.searchQuery.set(query),
        searchResults: (query) => this.releaseSearchResults(query),
      }),
    );
    timer(0, 15 * 60_000)
      .pipe(
        exhaustMap(() => {
          this.loading.set(true);
          this.error.set(false);
          return this.service.getUpdates().pipe(catchError(() => of(null)));
        }),
        takeUntilDestroyed(),
      )
      .subscribe((status) => {
        this.loading.set(false);
        this.error.set(status === null);
        if (status) this.data.set(status);
      });
  }

  private releaseSearchResults(query: string): readonly PageShellSearchResult[] {
    const normalized = query.trim().toLocaleLowerCase('fr-FR');
    if (!normalized) return [];
    return (this.data()?.releases ?? [])
      .filter((release) =>
        [release.version, release.notes, release.publishedAt].some((value) =>
          value.toLocaleLowerCase('fr-FR').includes(normalized),
        ),
      )
      .slice(0, 6)
      .map((release) => ({
        id: `release:${release.version}`,
        label: `Version ${release.version}`,
        description: release.notes.split('\n').find((line) => line.trim()) ?? release.publishedAt,
        group: 'Notes de version',
        icon: 'refresh' as const,
        run: () =>
          document.getElementById(`release-${release.version}`)?.scrollIntoView({
            behavior: 'smooth',
            block: 'start',
          }),
      }));
  }
}
