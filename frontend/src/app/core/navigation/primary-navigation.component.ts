import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { CdkTrapFocus } from '@angular/cdk/a11y';
import { NgTemplateOutlet } from '@angular/common';
import { NavigationEnd, Route, Router, RouterLink, RouterLinkActive } from '@angular/router';
import { MatTooltipModule } from '@angular/material/tooltip';
import { filter, map } from 'rxjs';
import { AuthService } from '../auth/auth.service';
import { AppearancePaletteComponent } from '../theme/appearance-palette.component';
import { UserMenuComponent } from '../auth/user-menu.component';

export interface NavigationItem {
  readonly label: string;
  readonly path: string;
  readonly glyph: string;
  readonly group?: string;
  readonly exact?: boolean;
  readonly adminOnly?: boolean;
  readonly visible?: boolean;
  readonly keywords?: readonly string[];
}

export interface NavigationGroup {
  readonly label: string;
  readonly items: readonly NavigationItem[];
}

interface RouteNavigationMetadata {
  readonly group: string;
  readonly label: string;
  readonly glyph: string;
  readonly order?: number;
  readonly exact?: boolean;
  readonly adminOnly?: boolean;
  readonly visible?: boolean;
  readonly keywords?: readonly string[];
}

const NAVIGATION_GROUP_ORDER = [
  'Vue générale',
  'Traitements',
  'Intelligence artificielle',
  'Ressources',
  'Infrastructure',
  'Administration',
];

const MOBILE_DOCK_PATHS = ['/', '/flows', '/activity', '/libraries'];

export function getPrimaryNavigationGroups(routes: Route[]): readonly NavigationGroup[] {
  const grouped = new Map<string, { item: NavigationItem; order: number }[]>();

  for (const route of routes) {
    const metadata = route.data?.['navigation'] as RouteNavigationMetadata | undefined;
    if (!metadata || route.redirectTo !== undefined || route.path === undefined) continue;

    const path = route.path ? `/${route.path}` : '/';
    const items = grouped.get(metadata.group) ?? [];
    items.push({
      item: {
        label: metadata.label,
        path,
        glyph: metadata.glyph,
        group: metadata.group,
        exact: metadata.exact,
        adminOnly: metadata.adminOnly,
        visible: metadata.visible,
        keywords: metadata.keywords,
      },
      order: metadata.order ?? 0,
    });
    grouped.set(metadata.group, items);
  }

  return [...grouped.entries()]
    .sort(([left], [right]) => {
      const leftOrder = NAVIGATION_GROUP_ORDER.indexOf(left);
      const rightOrder = NAVIGATION_GROUP_ORDER.indexOf(right);
      return (
        (leftOrder < 0 ? Number.MAX_SAFE_INTEGER : leftOrder) -
        (rightOrder < 0 ? Number.MAX_SAFE_INTEGER : rightOrder)
      );
    })
    .map(([label, items]) => ({
      label,
      items: items.sort((left, right) => left.order - right.order).map(({ item }) => item),
    }));
}

export function getSearchableNavigationItems(routes: Route[]): readonly NavigationItem[] {
  const baseItems = getPrimaryNavigationGroups(routes).flatMap((group) => group.items);
  const extraDestinations: readonly NavigationItem[] = [
    {
      label: 'Ce nœud (Machine locale)',
      path: '/nodes?tab=node',
      glyph: '▣',
      group: 'Infrastructure',
      keywords: ['local', 'worker', 'serveur', 'matériel', 'gpu', 'agent'],
    },
    {
      label: 'Cluster & Membres',
      path: '/nodes?tab=cluster',
      glyph: '▣',
      group: 'Infrastructure',
      keywords: ['cluster', 'membres', 'enrôlement', 'appairage', 'synchronisation'],
    },
    {
      label: 'Groupes de nœuds',
      path: '/nodes?tab=groups',
      glyph: '▣',
      group: 'Infrastructure',
      keywords: ['groupes', 'étiquettes', 'labels', 'critères'],
    },
  ];
  return [...baseItems, ...extraDestinations];
}

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-primary-navigation',
  imports: [
    CdkTrapFocus,
    MatTooltipModule,
    NgTemplateOutlet,
    RouterLink,
    RouterLinkActive,
    AppearancePaletteComponent,
    UserMenuComponent,
  ],
  templateUrl: './primary-navigation.component.html',
  styleUrl: './primary-navigation.component.scss',
  host: {
    '(document:keydown)': 'onDocumentKeydown($event)',
  },
})
export class PrimaryNavigationComponent {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly currentUrl = toSignal(
    this.router.events.pipe(
      filter((event): event is NavigationEnd => event instanceof NavigationEnd),
      map((event) => event.urlAfterRedirects),
    ),
    { initialValue: this.router.url },
  );
  private readonly moreButton = viewChild<ElementRef<HTMLButtonElement>>('moreButton');
  private readonly closeMenuButton = viewChild<ElementRef<HTMLButtonElement>>('closeMenuButton');

  readonly collapsed = input(false);
  readonly mobileDock = input(false);
  readonly activeExecutionCount = input(0);
  readonly mobileMenuOpenChange = output<boolean>();
  readonly openAssistant = output<void>();
  protected readonly mobileMenuOpen = signal(false);

  protected readonly groups = computed(() =>
    getPrimaryNavigationGroups(this.router.config)
      .map((group) => ({
        ...group,
        items: group.items.filter(
          (item) => item.visible !== false && (!item.adminOnly || this.auth.isAdmin()),
        ),
      }))
      .filter((group) => group.items.length > 0),
  );

  protected readonly dockItems = computed(() => {
    const items = this.groups().flatMap((group) => group.items);
    return MOBILE_DOCK_PATHS.map((path) => items.find((item) => item.path === path)).filter(
      (item): item is NavigationItem => item !== undefined,
    );
  });

  protected readonly activeOutsideDock = computed(() => {
    const path = this.currentUrl().split(/[?#]/, 1)[0] || '/';
    return !MOBILE_DOCK_PATHS.some((dockPath) =>
      dockPath === '/' ? path === '/' : path === dockPath || path.startsWith(`${dockPath}/`),
    );
  });

  protected dockLabel(item: NavigationItem): string {
    if (item.path === '/') return 'Accueil';
    if (item.path === '/libraries') return 'Biblios';
    return item.label;
  }

  protected toggleMobileMenu(): void {
    if (this.mobileMenuOpen()) {
      this.closeMobileMenu();
      return;
    }
    this.mobileMenuOpen.set(true);
    this.mobileMenuOpenChange.emit(true);
    setTimeout(() => this.closeMenuButton()?.nativeElement.focus(), 0);
  }

  protected closeMobileMenu(): void {
    if (!this.mobileMenuOpen()) return;
    this.mobileMenuOpen.set(false);
    this.mobileMenuOpenChange.emit(false);
    setTimeout(() => this.moreButton()?.nativeElement.focus(), 0);
  }

  protected openQuickAssistant(): void {
    this.closeMobileMenu();
    this.openAssistant.emit();
  }

  protected onDocumentKeydown(event: KeyboardEvent): void {
    if (event.key !== 'Escape' || !this.mobileMenuOpen()) return;
    event.preventDefault();
    this.closeMobileMenu();
  }

  focusMoreButton(): void {
    this.moreButton()?.nativeElement.focus();
  }
}
