import { MatButtonModule } from '@angular/material/button';
import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  TemplateRef,
  effect,
  inject,
  computed,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { DrawerModalState } from './drawer-modal-state.service';
import { MatDrawer, MatSidenavModule } from '@angular/material/sidenav';

export type DrawerVariant = 'neutral' | 'info' | 'danger' | 'success';

const VARIANT_GLYPHS: Record<DrawerVariant, string | null> = {
  neutral: null,
  info: 'i',
  danger: '!',
  success: '✓',
};

@Component({
  selector: 'app-detail-drawer',
  host: {
    '[class.drawer-fill]': 'fill()',
    '[class.drawer-body-fill]': "bodyLayout() === 'fill'",
    '(document:keydown.escape)': 'onEscape($event)',
  },
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatSidenavModule, NgTemplateOutlet],
  template: `
    <mat-drawer-container
      class="detail-drawer-container"
      [class.drawer-layer-active]="opened() && !fill()"
      [style.overflow]="mainScroll() === 'parent' ? 'visible' : null"
      [hasBackdrop]="modal()"
      (backdropClick)="requestClose()"
    >
      <mat-drawer-content [style.overflow]="mainScroll() === 'parent' ? 'visible' : null">
        <ng-content select="[drawer-main]" />
      </mat-drawer-content>
      <mat-drawer
        class="detail-drawer-panel"
        [class.detail-drawer-panel_standard]="size() === 'standard'"
        position="end"
        [mode]="mode()"
        [opened]="opened()"
        [disableClose]="true"
        [attr.role]="modal() ? 'dialog' : 'complementary'"
        [attr.aria-modal]="modal() ? 'true' : null"
        [attr.aria-label]="title()"
        (closed)="closed.emit()"
      >
        <section class="drawer-panel">
          <div class="drawer-grab" aria-hidden="true"></div>
          <header
            class="drawer-header"
            [class.drawer-header_compact]="headerDensity() === 'compact'"
            [class.drawer-header_info]="variant() === 'info'"
            [class.drawer-header_danger]="variant() === 'danger'"
            [class.drawer-header_success]="variant() === 'success'"
          >
            @if (glyph(); as glyphText) {
              <span class="drawer-icon" aria-hidden="true">{{ glyphText }}</span>
            }
            <div class="drawer-heading">
              @if (eyebrow()) {
                <p class="drawer-eyebrow">{{ eyebrow() }}</p>
              }
              <h2>{{ title() }}</h2>
              @if (description()) {
                <p>{{ description() }}</p>
              }
            </div>
            <div class="drawer-header-controls">
              <ng-container [ngTemplateOutlet]="headerActions() ?? null" />
              <button
                mat-icon-button
                class="drawer-close"
                type="button"
                aria-label="Fermer le panneau"
                [disabled]="!dismissible() || closePending()"
                (click)="requestClose()"
              >
                <span aria-hidden="true">×</span>
              </button>
            </div>
          </header>
          @if (step(); as progress) {
            <div
              class="drawer-progress"
              role="progressbar"
              [attr.aria-label]="'Étape ' + progress.current + ' sur ' + progress.total"
              aria-valuemin="1"
              [attr.aria-valuemax]="progress.total"
              [attr.aria-valuenow]="progress.current"
            >
              <i [style.width.%]="(progress.current / progress.total) * 100"></i>
            </div>
          }
          <div class="drawer-body">
            <ng-container [ngTemplateOutlet]="content() ?? null" />
          </div>
          @if (footerActions(); as footerTemplate) {
            <footer class="drawer-footer">
              <ng-container [ngTemplateOutlet]="footerTemplate" />
            </footer>
          }
        </section>
      </mat-drawer>
    </mat-drawer-container>
  `,
  styles: `
    :host {
      display: block;
      width: 100%;
    }

    :host(.drawer-fill) {
      height: 100%;
      min-height: 0;
    }
    :host(.drawer-fill) .detail-drawer-container,
    :host(.drawer-fill) mat-drawer-content {
      height: 100%;
    }
    :host(.drawer-fill) .detail-drawer-container.mat-drawer-container {
      position: relative;
      overflow: hidden;
      z-index: auto;
    }
    :host(.drawer-fill) .detail-drawer-panel.mat-drawer {
      position: absolute;
      width: min(560px, 100%);
      max-width: 100%;
    }
    :host(.drawer-fill) .drawer-panel {
      height: 100%;
    }
    .detail-drawer-container {
      width: 100%;
    }

    .detail-drawer-panel.mat-drawer {
      position: fixed;
      inset-block: 0;
      inset-inline-end: 0;
      width: min(1000px, 82vw);
      max-width: 100vw;
      height: 100%;
      border-radius: var(--radius-2xl) 0 0 var(--radius-2xl);
      background: var(--surface-container-lowest);
      box-shadow: var(--elevation-3);
      overflow: hidden;
      z-index: 1000;
    }
    .detail-drawer-panel_standard.mat-drawer {
      width: min(560px, 100vw);
    }

    .drawer-panel {
      --drawer-tone: var(--accent-soft);
      --drawer-tone-ink: var(--accent-strong);
      height: 100%;
      min-height: 0;
      max-height: 100%;
      display: flex;
      flex-direction: column;
      box-sizing: border-box;
      background: var(--surface-container-lowest);
    }

    .drawer-header {
      flex: 0 0 auto;
      min-height: 64px;
      padding: var(--space-4) var(--space-5);
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 16px;
      background: var(--drawer-tone);
    }

    .drawer-header_info {
      --drawer-tone: var(--tertiary-soft);
      --drawer-tone-ink: var(--tertiary);
    }

    .drawer-header_danger {
      --drawer-tone: var(--danger-soft);
      --drawer-tone-ink: var(--danger);
    }

    .drawer-header_success {
      --drawer-tone: var(--success-soft);
      --drawer-tone-ink: var(--success);
    }

    .drawer-header_compact {
      min-height: 52px;
      padding: var(--space-2) var(--space-4);
      gap: var(--space-3);
    }

    .drawer-header_compact .drawer-heading h2 {
      font-size: var(--font-title);
      line-height: 1.2;
    }

    .drawer-header_compact .drawer-heading p {
      margin-top: 2px;
      font-size: var(--font-label);
      line-height: 1.25;
    }

    .drawer-icon {
      width: 40px;
      height: 40px;
      flex: 0 0 40px;
      display: grid;
      place-items: center;
      border-radius: var(--radius-pill);
      background: var(--surface);
      color: var(--drawer-tone-ink);
      font-size: var(--font-title-md);
      font-weight: 700;
    }

    .drawer-progress {
      height: 4px;
      flex: 0 0 auto;
      background: var(--surface-container-highest);
    }

    .drawer-progress i {
      display: block;
      height: 100%;
      background: var(--accent);
      border-radius: 0 2px 2px 0;
      transition: width 0.2s ease;
    }

    .drawer-grab {
      display: none;
    }

    .drawer-heading {
      flex: 1;
      min-width: 0;
      overflow-wrap: break-word;
    }

    .drawer-heading h2 {
      margin: 0;
      color: var(--text-primary);
      font-size: clamp(18px, 2.2vw, 24px);
      font-weight: 700;
      letter-spacing: -0.02em;
      line-height: 1.25;
      word-break: normal;
      overflow-wrap: break-word;
    }

    .drawer-heading p {
      margin: 5px 0 0;
      color: var(--text-muted);
      font-size: var(--font-body);
      line-height: 1.45;
    }

    .drawer-heading .drawer-eyebrow {
      margin: 0 0 5px;
      color: var(--accent-strong);
      font-size: var(--font-label);
      font-weight: 700;
      letter-spacing: 0.08em;
      text-transform: uppercase;
    }

    .drawer-header-controls {
      display: flex;
      align-items: center;
      gap: var(--space-2);
      flex: 0 0 auto;
      margin-inline-start: auto;
    }

    .drawer-close {
      flex: 0 0 auto;
      color: var(--text-secondary);
    }

    .drawer-body {
      min-height: 0;
      padding: var(--space-5);
      flex: 1 1 0;
      overscroll-behavior: contain;
      overflow: auto;
    }

    :host(.drawer-body-fill) .drawer-body {
      display: flex;
      min-height: 0;
      padding: 0;
      overflow: hidden;
    }

    .drawer-footer:empty {
      display: none;
    }

    .drawer-footer {
      flex: 0 0 auto;
      z-index: 1;
      padding: var(--space-3) var(--space-5);
      display: flex;
      align-items: center;
      justify-content: flex-end;
      gap: var(--space-2);
      background: var(--surface-container-low);
    }

    @media (max-width: 700px) {
      .detail-drawer-panel,
      .detail-drawer-panel_standard {
        width: 100vw;
        max-width: 100vw;
      }

      .detail-drawer-panel.mat-drawer {
        width: 100vw;
        border-start-start-radius: var(--radius-2xl);
        border-start-end-radius: var(--radius-2xl);
        border-end-start-radius: 0;
      }

      :host(:not(.drawer-fill)) .detail-drawer-panel.mat-drawer-end {
        top: 48px;
        bottom: 0;
        height: calc(100dvh - 48px);
      }

      :host(:not(.drawer-fill)) .detail-drawer-panel.mat-drawer-end:not(.mat-drawer-opened) {
        transform: translate3d(0, 100%, 0);
      }

      .drawer-panel {
        height: 100%;
      }

      .drawer-grab {
        display: block;
        width: 36px;
        height: 4px;
        margin: 8px auto 0;
        flex: 0 0 auto;
        border-radius: 2px;
        background: var(--text-muted);
        opacity: 0.6;
      }

      .drawer-footer > * {
        flex: 1 1 0;
      }

      .drawer-header {
        min-height: 72px;
        padding: var(--space-3) var(--space-4);
        gap: var(--space-3);
      }

      .drawer-header_compact {
        min-height: 48px;
        padding: var(--space-2) var(--space-4);
      }

      .drawer-header-controls {
        align-self: flex-start;
      }

      .drawer-body {
        padding: var(--space-4);
      }

      .drawer-footer {
        padding: var(--space-3) var(--space-4);
        padding-bottom: max(var(--space-3), env(safe-area-inset-bottom));
      }
    }
  `,
})
export class DetailDrawerComponent {
  private readonly modalState = inject(DrawerModalState);

  /** `standard` : tiroir de 560 px pour une fiche ; `wide` : jusqu'à 1 000 px pour un éditeur. */
  readonly fill = input(false);
  /** `fill` laisse la vue imbriquée remplir le corps et gérer son propre défilement. */
  readonly bodyLayout = input<'scroll' | 'fill'>('scroll');
  readonly mode = input<'side' | 'over'>('over');
  readonly size = input<'standard' | 'wide'>('wide');
  readonly headerDensity = input<'standard' | 'compact'>('standard');
  /** Avec un voile, un clic extérieur ferme le tiroir si celui-ci est dismissible. */
  readonly modal = input(false);
  /** Autoriser les demandes de fermeture par croix, Escape et voile. */
  readonly dismissible = input(true);
  /** Contrôle asynchrone facultatif, par exemple pour confirmer l’abandon d’une saisie. */
  readonly closeGuard = input<(() => boolean | Promise<boolean>) | undefined>();
  /** La feature confirme avant de modifier opened ; aucun panneau ne se ferme entre-temps. */
  readonly closeGuarded = input(false);
  readonly closeRequested = output<void>();
  readonly mainScroll = input<'auto' | 'parent'>('auto');
  readonly opened = input(false);
  /** Teinte du bandeau : même vocabulaire que les modales (`DialogFrameComponent`). */
  readonly variant = input<DrawerVariant>('neutral');
  /** Glyphe de la pastille ; par défaut celui de la variante, aucune pastille en neutre. */
  readonly icon = input<string>();
  /** Avancement d'un parcours en plusieurs étapes (« Étape 1 sur 2 »). */
  readonly step = input<{ readonly current: number; readonly total: number } | null>(null);
  protected readonly glyph = computed(() => this.icon() ?? VARIANT_GLYPHS[this.variant()]);
  readonly eyebrow = input('');
  readonly title = input('Détails');
  readonly description = input('');
  readonly content = input<TemplateRef<unknown> | undefined>(undefined);
  /** Pied fixe : à fournir pour les actions d'un formulaire (Annuler / Enregistrer). */
  readonly footerActions = input<TemplateRef<unknown> | undefined>(undefined);
  readonly headerActions = input<TemplateRef<unknown> | undefined>(undefined);
  readonly closed = output<void>();
  protected readonly closePending = signal(false);
  private readonly drawer = viewChild(MatDrawer);

  constructor() {
    effect((onCleanup) => {
      if (this.modal() && this.opened()) onCleanup(this.modalState.acquire(this));
    });
  }

  protected onEscape(event: Event): void {
    if (!this.opened() || event.defaultPrevented) return;
    event.preventDefault();
    event.stopPropagation();
    void this.requestClose();
  }

  protected async requestClose(): Promise<void> {
    if (!this.opened() || !this.dismissible() || this.closePending()) return;
    this.closePending.set(true);
    try {
      const guard = this.closeGuard();
      if (guard && !(await guard())) return;
      if (this.closeGuarded()) this.closeRequested.emit();
      else void this.drawer()?.close();
    } finally {
      this.closePending.set(false);
    }
  }
}
