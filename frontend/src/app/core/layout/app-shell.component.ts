import {
  ExecutionTracking,
  INITIAL_EXECUTION_TRACKING,
} from '../../shared/services/execution-tracking';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { toSignal, toObservable } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterOutlet } from '@angular/router';
import { BreakpointObserver } from '@angular/cdk/layout';
import { MatTooltipModule } from '@angular/material/tooltip';
import { filter, map, of, switchMap } from 'rxjs';
import { PrimaryNavigationComponent } from '../navigation/primary-navigation.component';
import { AuthService } from '../auth/auth.service';
import { AppearancePaletteComponent } from '../theme/appearance-palette.component';
import { AssistantHostComponent } from './assistant-host.component';
import { PageSystemActionsComponent } from '../../shared/ui/page-system-actions.component';
import { GlobalSearchBarComponent } from './global-search-bar.component';
import { UserMenuComponent } from '../auth/user-menu.component';
import { PageShellContextService } from './page-shell-context';

@Component({
  selector: 'app-shell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    MatTooltipModule,
    PrimaryNavigationComponent,
    RouterLink,
    RouterOutlet,
    AppearancePaletteComponent,
    AssistantHostComponent,
    PageSystemActionsComponent,
    GlobalSearchBarComponent,
    UserMenuComponent,
  ],
  templateUrl: './app-shell.component.html',
  host: {
    '(document:keydown)': 'onGlobalSearchShortcut($event)',
  },
})
export class AppShellComponent {
  private readonly mobileNavigation = viewChild<PrimaryNavigationComponent>('mobileNavigation');
  protected readonly assistantHost = viewChild<AssistantHostComponent>('assistantHost');
  protected focusMobileMenu(): void {
    this.mobileNavigation()?.focusMoreButton();
  }
  private readonly router = inject(Router);
  private readonly pageShellContext = inject(PageShellContextService);
  protected readonly globalSearch = viewChild(GlobalSearchBarComponent);
  protected readonly hasSearchControls = computed(() => {
    const context = this.pageShellContext.current();
    return (
      (context?.actions?.some((action) => !action.visibleWhen || action.visibleWhen()) ?? false) ||
      (context?.filter !== undefined &&
        (!context.filter.visibleWhen || context.filter.visibleWhen()))
    );
  });

  private readonly navigationEnd = toSignal(
    this.router.events.pipe(
      filter((event): event is NavigationEnd => event instanceof NavigationEnd),
      map(() => this.getCurrentPageContext()),
    ),
    { initialValue: this.getCurrentPageContext() },
  );

  private readonly compactNavigation = toSignal(
    inject(BreakpointObserver)
      .observe('(max-width: 760px)')
      .pipe(map((state) => state.matches)),
    { initialValue: false },
  );
  protected readonly isCompactNavigation = this.compactNavigation;
  protected readonly isTabletNavigation = toSignal(
    inject(BreakpointObserver)
      .observe('(min-width: 761px) and (max-width: 1100px)')
      .pipe(map((state) => state.matches)),
    { initialValue: false },
  );
  protected readonly navigationSheetOpen = signal(false);
  protected readonly navigationCollapsed = signal(
    typeof localStorage !== 'undefined' ? localStorage.getItem('nav_collapsed') === 'true' : false,
  );

  protected readonly railNavigation = computed(
    () => this.isTabletNavigation() || this.navigationCollapsed(),
  );

  protected toggleNavigationCollapsed(): void {
    if (!this.isCompactNavigation() && !this.isTabletNavigation()) {
      this.navigationCollapsed.update((v) => {
        const next = !v;
        try {
          localStorage.setItem('nav_collapsed', String(next));
        } catch {
          // ignore
        }
        return next;
      });
    }
  }

  protected readonly workspaceMode = computed(() => this.navigationEnd().workspaceMode);
  protected readonly authMode = computed(() => this.navigationEnd().authMode);
  protected readonly fullWidthContent = computed(
    () => this.navigationEnd().contentWidth === 'full',
  );
  constructor() {
    effect(() => {
      if (!this.isCompactNavigation()) this.navigationSheetOpen.set(false);
    });
  }

  private readonly auth = inject(AuthService);
  protected readonly currentUser = this.auth.user;
  // Aucun appel à l’API de gestion sans session.
  private readonly pollingDisabled = computed(() => this.authMode() || !this.auth.user());
  private readonly tracking = inject(ExecutionTracking);
  private readonly executionState = toSignal(
    toObservable(this.pollingDisabled).pipe(
      switchMap((disabled) => (disabled ? of(INITIAL_EXECUTION_TRACKING) : this.tracking.state$)),
    ),
    { initialValue: INITIAL_EXECUTION_TRACKING },
  );
  protected readonly activeExecutionCount = computed(
    () => this.executionState().data?.activeCount ?? 0,
  );

  protected onNavigationClick(): void {
    this.navigationSheetOpen.set(false);
  }

  protected onGlobalSearchShortcut(event: KeyboardEvent): void {
    if (event.defaultPrevented) return;
    if (event.key === 'Escape' && this.globalSearch()?.hasOpenPanel()) {
      event.preventDefault();
      this.globalSearch()?.closePanel();
      return;
    }
    if (
      event.key.toLocaleLowerCase() === 'k' &&
      (event.metaKey || event.ctrlKey) &&
      !this.authMode() &&
      !this.workspaceMode()
    ) {
      event.preventDefault();
      this.globalSearch()?.focusSearch();
    }
  }

  private getCurrentPageContext(): {
    url: string;
    workspaceMode: boolean;
    authMode: boolean;
    contentWidth: string;
  } {
    let route = this.router.routerState.snapshot.root;

    while (route.firstChild) {
      route = route.firstChild;
    }

    return {
      url: this.router.url,
      workspaceMode: route.data['layout'] === 'workspace',
      authMode: route.data['authPage'] !== undefined,
      contentWidth: route.data['contentWidth'] === 'full' ? 'full' : 'standard',
    };
  }
}
