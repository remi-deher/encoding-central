import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink } from '@angular/router';
import { filter, map } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { FlowEditorAssistant } from '../../shared/services/flow-editor-assistant.service';
import { AuthService } from '../auth/auth.service';
import type { AiAssistantPageComponent as AssistantPage } from '../../features/ai/ai-assistant-page.component';
import type { FlowAiAssistantPanelComponent as FlowPanel } from '../../features/flows/editor/flow-ai-assistant-panel.component';
import { AiAssistantPageComponent } from '../../features/ai/ai-assistant-page.component';
import { FlowAiAssistantPanelComponent } from '../../features/flows/editor/flow-ai-assistant-panel.component';

@Component({
  selector: 'app-assistant-host',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, RouterLink, AiAssistantPageComponent, FlowAiAssistantPanelComponent],
  templateUrl: './assistant-host.component.html',
  host: { '(window:resize)': 'onWindowResize()', '(document:keydown)': 'onKeydown($event)' },
})
export class AssistantHostComponent {
  readonly authMode = input(false);
  readonly workspaceMode = input(false);
  readonly isCompactNavigation = input(false);
  readonly navigationSheetOpen = input(false);
  readonly focusMobileMenu = output<void>();
  protected readonly flowAssistant = inject(FlowEditorAssistant);
  protected readonly generalAssistant = viewChild<AssistantPage>('generalAssistant');
  protected readonly flowWidget = viewChild<FlowPanel>('flowWidget');
  protected async applyFlowProposal(
    flow: import('../../shared/models/flow-validation').FlowDocument,
  ): Promise<void> {
    const widget = this.flowWidget();
    if (await this.flowAssistant.apply(flow)) {
      widget?.acknowledgeProposal();
      this.closeAssistantPanel();
    }
  }

  private static readonly assistantLauncherStorageKey = 'encoding.assistant-launcher.position';
  private static readonly assistantLauncherGrid = 24;
  private readonly auth = inject(AuthService);
  protected readonly currentUser = this.auth.user;
  private readonly router = inject(Router);
  private readonly currentUrl = toSignal(
    this.router.events.pipe(
      filter((event): event is NavigationEnd => event instanceof NavigationEnd),
      map((event) => event.urlAfterRedirects),
    ),
    { initialValue: this.router.url },
  );
  private readonly assistantRouteActive = computed(() =>
    this.currentUrl().startsWith('/ai/assistant'),
  );
  protected readonly showAssistantBubble = computed(
    () => Boolean(this.currentUser()) && !this.authMode() && !this.assistantRouteActive(),
  );

  constructor() {
    afterNextRender(() => this.onWindowResize());
    effect(() => {
      if (this.flowAssistant.session()) {
        this.assistantWidgetMounted.set(true);
        this.assistantPanelOpen.set(true);
        this.updateAssistantPanelGeometry();
      }
    });
    effect(() => {
      if (this.assistantRouteActive()) this.assistantPanelOpen.set(false);
    });
  }
  protected onKeydown(event: KeyboardEvent): void {
    if (event.defaultPrevented || event.key !== 'Escape' || !this.assistantPanelOpen()) return;
    event.preventDefault();
    this.closeAssistantPanel();
  }
  protected readonly assistantWidgetMounted = signal(false);
  protected readonly generalAssistantMounted = signal(false);
  protected readonly assistantPanelOpen = signal(false);
  private readonly assistantPanelOpening = signal(false);
  protected readonly assistantPanelGeometry = signal<{
    left: number;
    top: number;
    originX: number;
    originY: number;
    scaleX: number;
    scaleY: number;
  } | null>(null);
  protected readonly assistantLauncherPosition = signal(this.readAssistantLauncherPosition());
  protected readonly assistantLauncherDragging = signal(false);
  private readonly assistantLauncher = viewChild('assistantLauncher', {
    read: ElementRef<HTMLButtonElement>,
  });
  private readonly assistantCloseButton = viewChild('assistantCloseButton', {
    read: ElementRef<HTMLButtonElement>,
  });
  private focusReturnElement: HTMLElement | null = null;
  private assistantDrag: {
    readonly pointerId: number;
    readonly pointerX: number;
    readonly pointerY: number;
    readonly left: number;
    readonly top: number;
    moved: boolean;
  } | null = null;
  private suppressAssistantClick = false;

  protected startAssistantLauncherDrag(event: PointerEvent): void {
    if (event.button !== 0 || !this.assistantLauncher()) return;
    const rect = this.assistantLauncher()!.nativeElement.getBoundingClientRect();
    this.assistantDrag = {
      pointerId: event.pointerId,
      pointerX: event.clientX,
      pointerY: event.clientY,
      left: rect.left,
      top: rect.top,
      moved: false,
    };
    this.assistantLauncher()!.nativeElement.setPointerCapture(event.pointerId);
  }

  protected moveAssistantLauncher(event: PointerEvent): void {
    const drag = this.assistantDrag;
    if (!drag || drag.pointerId !== event.pointerId) return;
    const deltaX = event.clientX - drag.pointerX;
    const deltaY = event.clientY - drag.pointerY;
    if (!drag.moved && Math.hypot(deltaX, deltaY) < 5) return;
    drag.moved = true;
    this.assistantLauncherDragging.set(true);
    event.preventDefault();
    this.setAssistantLauncherPosition(drag.left + deltaX, drag.top + deltaY, false);
  }

  protected finishAssistantLauncherDrag(event: PointerEvent): void {
    const drag = this.assistantDrag;
    if (!drag || drag.pointerId !== event.pointerId) return;
    this.assistantDrag = null;
    this.assistantLauncherDragging.set(false);
    if (drag.moved) {
      this.saveAssistantLauncherPosition();
      this.suppressAssistantClick = true;
      setTimeout(() => (this.suppressAssistantClick = false));
    }
    const button = this.assistantLauncher()?.nativeElement;
    if (button?.hasPointerCapture(event.pointerId)) button.releasePointerCapture(event.pointerId);
  }

  protected onAssistantLauncherClick(event: MouseEvent): void {
    if (this.suppressAssistantClick) {
      event.preventDefault();
      event.stopPropagation();
      this.suppressAssistantClick = false;
      return;
    }
    if (this.flowAssistant.hasEditor() && !this.assistantPanelOpen()) {
      if (this.flowAssistant.session()) {
        this.updateAssistantPanelGeometry();
        this.assistantPanelOpen.set(true);
      } else void this.flowAssistant.open();
      return;
    }
    this.toggleAssistantPanel();
  }

  protected onAssistantLauncherKeydown(event: KeyboardEvent): void {
    if (!event.altKey || !event.key.startsWith('Arrow')) return;
    const rect = this.assistantLauncher()?.nativeElement.getBoundingClientRect();
    if (!rect) return;
    const step = AssistantHostComponent.assistantLauncherGrid;
    const delta = {
      ArrowLeft: [-step, 0],
      ArrowRight: [step, 0],
      ArrowUp: [0, -step],
      ArrowDown: [0, step],
    }[event.key];
    if (!delta) return;
    event.preventDefault();
    this.setAssistantLauncherPosition(rect.left + delta[0], rect.top + delta[1], true);
  }

  protected onWindowResize(): void {
    const position = this.assistantLauncherPosition();
    if (position) this.setAssistantLauncherPosition(position.left, position.top, true);
    if (this.assistantPanelOpen()) this.updateAssistantPanelGeometry();
  }

  private setAssistantLauncherPosition(left: number, top: number, persist: boolean): void {
    const button = this.assistantLauncher()?.nativeElement;
    const margin = 16;
    const maxLeft = Math.max(margin, window.innerWidth - (button?.offsetWidth ?? 56) - margin);
    const bottomClearance =
      this.isCompactNavigation() && !this.authMode() && !this.workspaceMode() ? 84 : margin;
    const maxTop = Math.max(
      margin,
      window.innerHeight - (button?.offsetHeight ?? 56) - bottomClearance,
    );
    const grid = AssistantHostComponent.assistantLauncherGrid;
    const snap = (value: number, max: number) =>
      Math.min(max, Math.max(margin, Math.round((value - margin) / grid) * grid + margin));
    this.assistantLauncherPosition.set({ left: snap(left, maxLeft), top: snap(top, maxTop) });
    if (persist) this.saveAssistantLauncherPosition();
  }

  private saveAssistantLauncherPosition(): void {
    try {
      const position = this.assistantLauncherPosition();
      if (position)
        localStorage.setItem(
          AssistantHostComponent.assistantLauncherStorageKey,
          JSON.stringify(position),
        );
    } catch {
      // Position persistence is optional when storage is unavailable.
    }
  }

  private readAssistantLauncherPosition(): { left: number; top: number } | null {
    try {
      const value = localStorage.getItem(AssistantHostComponent.assistantLauncherStorageKey);
      if (!value) return null;
      const position: unknown = JSON.parse(value);
      if (
        typeof position === 'object' &&
        position !== null &&
        'left' in position &&
        'top' in position &&
        typeof position.left === 'number' &&
        Number.isFinite(position.left) &&
        typeof position.top === 'number' &&
        Number.isFinite(position.top)
      )
        return { left: position.left, top: position.top };
    } catch {
      // Fall back to the default bottom-right position.
    }
    return null;
  }

  toggleAssistantPanel(): void {
    if (this.assistantPanelOpening()) return;
    if (this.assistantPanelOpen()) {
      this.closeAssistantPanel();
      return;
    }
    if (!this.flowAssistant.session()) this.generalAssistantMounted.set(true);
    this.focusReturnElement =
      document.activeElement instanceof HTMLElement ? document.activeElement : null;
    this.updateAssistantPanelGeometry();
    if (!this.assistantWidgetMounted()) {
      this.assistantPanelOpening.set(true);
      this.assistantWidgetMounted.set(true);
      requestAnimationFrame(() => requestAnimationFrame(() => this.activateAssistantPanel()));
      return;
    }
    this.activateAssistantPanel();
  }

  private activateAssistantPanel(): void {
    this.assistantPanelOpening.set(false);
    this.assistantPanelOpen.set(true);
    setTimeout(() => this.assistantCloseButton()?.nativeElement.focus());
  }

  private updateAssistantPanelGeometry(): void {
    const button = this.assistantLauncher()?.nativeElement;
    if (!button) return;

    const viewportWidth = window.innerWidth;
    const viewportHeight = window.innerHeight;
    const panelWidth = Math.min(
      this.flowAssistant.session() ? 640 : 380,
      Math.max(1, viewportWidth - 32),
    );
    const panelHeight = Math.min(
      this.flowAssistant.session() ? 840 : 520,
      Math.max(1, viewportHeight - 112),
    );
    const margin = 16;
    if (this.isCompactNavigation()) {
      const dockClearance = 84;
      const left = Math.max(margin, viewportWidth - panelWidth - margin);
      const top = Math.max(margin, viewportHeight - panelHeight - dockClearance);
      const anchorSize = 48;
      this.assistantPanelGeometry.set({
        left,
        top,
        originX: Math.max(0, panelWidth - anchorSize),
        originY: Math.max(0, panelHeight - anchorSize),
        scaleX: Math.min(1, anchorSize / panelWidth),
        scaleY: Math.min(1, anchorSize / panelHeight),
      });
      return;
    }

    const launcher = button.getBoundingClientRect();
    const gap = 16;
    const clamp = (value: number, min: number, max: number) => Math.min(max, Math.max(min, value));
    const maxLeft = Math.max(margin, viewportWidth - panelWidth - margin);
    const maxTop = Math.max(margin, viewportHeight - panelHeight - margin);
    const left = clamp(launcher.right - panelWidth, margin, maxLeft);
    let top = launcher.top - panelHeight - gap;
    if (top < margin) {
      const below = launcher.bottom + gap;
      top = below <= maxTop ? below : clamp(top, margin, maxTop);
    }
    const originX = clamp(launcher.left + launcher.width / 2 - left, 0, panelWidth);
    const originY = clamp(launcher.top + launcher.height / 2 - top, 0, panelHeight);

    this.assistantPanelGeometry.set({
      left,
      top,
      originX,
      originY,
      scaleX: Math.min(1, launcher.width / panelWidth),
      scaleY: Math.min(1, launcher.height / panelHeight),
    });
  }

  protected closeAssistantPanel(): void {
    this.assistantPanelOpen.set(false);
    setTimeout(() => {
      if (this.focusReturnElement?.isConnected) {
        this.focusReturnElement.focus();
      } else if (this.isCompactNavigation()) {
        this.focusMobileMenu.emit();
      } else {
        this.assistantLauncher()?.nativeElement.focus();
      }
      this.focusReturnElement = null;
    });
  }
}
