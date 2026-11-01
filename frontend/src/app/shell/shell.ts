import { BreakpointObserver } from '@angular/cdk/layout';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterOutlet } from '@angular/router';
import { filter, map } from 'rxjs';
import { Appearance } from '../core/appearance';
import { AuthService } from '../core/auth.service';
import { PrimaryNavigation } from './navigation';

@Component({
  selector: 'app-shell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PrimaryNavigation, RouterLink, RouterOutlet],
  templateUrl: './shell.html',
})
export class Shell {
  private readonly router = inject(Router);
  private readonly auth = inject(AuthService);
  protected readonly appearance = inject(Appearance);
  protected readonly user = this.auth.user;

  private readonly url = toSignal(
    this.router.events.pipe(
      filter((event): event is NavigationEnd => event instanceof NavigationEnd),
      map(() => this.router.url),
    ),
    { initialValue: this.router.url },
  );

  private readonly compact = toSignal(
    inject(BreakpointObserver)
      .observe('(max-width: 760px)')
      .pipe(map((state) => state.matches)),
    { initialValue: window.matchMedia('(max-width: 760px)').matches },
  );
  private readonly tablet = toSignal(
    inject(BreakpointObserver)
      .observe('(min-width: 761px) and (max-width: 1100px)')
      .pipe(map((state) => state.matches)),
    { initialValue: window.matchMedia('(min-width: 761px) and (max-width: 1100px)').matches },
  );

  protected readonly collapsed = signal(localStorage.getItem('encoding-nav-collapsed') === 'true');
  protected readonly authMode = computed(() => {
    const path = this.url().split('?')[0] ?? '';
    return path.startsWith('/connexion') || path.startsWith('/mise-en-service');
  });
  protected readonly rail = computed(() => !this.authMode() && (this.tablet() || this.collapsed()));
  protected readonly showSidebar = computed(() => !this.authMode() && !this.compact());
  protected readonly showDock = computed(() => !this.authMode() && this.compact());

  protected tabletHidden(): boolean {
    return this.tablet();
  }

  protected paletteMark(): string {
    const palette = this.appearance.palette();
    if (palette === 'ocean') return 'O';
    if (palette === 'iris') return 'I';
    if (palette === 'rose') return 'R';
    return 'F';
  }

  protected toggleCollapsed(): void {
    if (this.compact() || this.tablet()) return;
    this.collapsed.update((value) => {
      const next = !value;
      localStorage.setItem('encoding-nav-collapsed', String(next));
      return next;
    });
  }

  protected async logout(): Promise<void> {
    await this.auth.logout();
    await this.router.navigateByUrl('/connexion');
  }
}
