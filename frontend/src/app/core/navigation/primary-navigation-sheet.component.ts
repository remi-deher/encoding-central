import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MAT_BOTTOM_SHEET_DATA, MatBottomSheetRef } from '@angular/material/bottom-sheet';
import { MatTooltipModule } from '@angular/material/tooltip';
import { IconComponent, IconName } from '../../shared/ui/icon.component';
import { AppearancePaletteComponent } from '../theme/appearance-palette.component';
import { UserMenuComponent } from '../auth/user-menu.component';
import type { NavigationGroup } from './primary-navigation.component';

export interface PrimaryNavigationSheetData {
  readonly groups: readonly NavigationGroup[];
  readonly activeExecutionCount: number;
}

/** `'assistant'` demande l'ouverture de l'assistant rapide après fermeture. */
export type PrimaryNavigationSheetResult = 'assistant' | undefined;

/** Icône du registre `app-icon` pour une destination ; `undefined` = glyphe de la route. */
export function navigationIconName(path: string): IconName | undefined {
  switch (path) {
    case '/':
      return 'home';
    case '/flows':
      return 'flow';
    case '/activity':
      return 'history';
    case '/libraries':
      return 'library';
    case '/workers':
      return 'server';
    case '/plugins':
      return 'pack';
    case '/settings':
      return 'settings';
    default:
      return undefined;
  }
}

@Component({
  selector: 'app-primary-navigation-sheet',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    IconComponent,
    MatButtonModule,
    MatTooltipModule,
    RouterLink,
    RouterLinkActive,
    AppearancePaletteComponent,
    UserMenuComponent,
  ],
  template: `
    <header class="mobile-navigation-sheet-header">
      <h2>Navigation</h2>
      <button
        matIconButton
        type="button"
        class="mobile-navigation-close"
        aria-label="Fermer la navigation"
        matTooltip="Fermer la navigation"
        (click)="sheetRef.dismiss()"
      >
        <app-icon name="close" />
      </button>
    </header>

    <button class="mobile-assistant-shortcut" type="button" (click)="sheetRef.dismiss('assistant')">
      <span class="mobile-assistant-shortcut-icon" aria-hidden="true">
        <app-icon name="auto_awesome" />
      </span>
      <span class="mobile-assistant-shortcut-copy">
        <strong>Assistant rapide</strong>
        <small>Poser une question sans quitter cette page</small>
      </span>
    </button>

    <nav class="mobile-navigation-menu" aria-label="Toutes les destinations">
      @for (group of data.groups; track group.label) {
        <section class="mobile-navigation-group">
          <p class="mobile-navigation-group-label">{{ group.label }}</p>
          @for (item of group.items; track item.path) {
            <a
              class="mobile-navigation-link"
              [routerLink]="item.path"
              routerLinkActive="is-active"
              [routerLinkActiveOptions]="{ exact: item.exact ?? false }"
              ariaCurrentWhenActive="page"
              (click)="sheetRef.dismiss()"
            >
              <span class="mobile-navigation-menu-icon" aria-hidden="true">
                @if (iconFor(item.path); as icon) {
                  <app-icon [name]="icon" />
                } @else {
                  <span>{{ item.glyph }}</span>
                }
              </span>
              <span>{{ item.label }}</span>
              @if (item.path === '/activity' && data.activeExecutionCount > 0) {
                <span class="navigation-badge-pill" aria-hidden="true">
                  {{ data.activeExecutionCount }}
                </span>
              }
            </a>
          }
        </section>
      }
    </nav>

    <footer class="mobile-navigation-sheet-footer" aria-label="Compte et apparence">
      <app-user-menu [collapsed]="true" />
      <app-appearance-palette />
    </footer>
  `,
  styleUrl: './primary-navigation-sheet.component.scss',
})
export class PrimaryNavigationSheetComponent {
  protected readonly data = inject<PrimaryNavigationSheetData>(MAT_BOTTOM_SHEET_DATA);
  protected readonly sheetRef =
    inject<MatBottomSheetRef<PrimaryNavigationSheetComponent, PrimaryNavigationSheetResult>>(
      MatBottomSheetRef,
    );
  protected readonly iconFor = navigationIconName;
}
