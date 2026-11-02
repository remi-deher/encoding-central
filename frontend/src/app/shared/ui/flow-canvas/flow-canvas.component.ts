import { IconComponent } from '../icon.component';
import { FlowSimulationStatusComponent } from '../flow-simulation-status.component';
import { SimulationResult } from '../../models/flow-simulation';
import { MediaType } from '../../models/media-type';
import { flowBlockSummary } from '../../models/flow-block-summary';
import { FlowComponentDrop, FlowPaletteDrop } from '../flow-palette-drag';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  signal,
  input,
  output,
  ElementRef,
  viewChild,
  viewChildren,
  inject,
  DestroyRef,
  afterRenderEffect,
  untracked,
} from '@angular/core';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatButtonModule } from '@angular/material/button';
import {
  FlowGraphNode,
  FlowGraphConnection,
  FlowConnectionRequest,
  FlowGraphPosition,
  FlowComponentOutput,
  FLOW_COMPONENT_KIND_LABELS,
} from '../../models/flow-component';
import {
  layoutFlowCanvas,
  FlowCanvasNodeSize,
  estimateFlowCanvasNodeSize,
} from '../../models/flow-canvas';
import { canConnectFlowGraph } from '../../models/flow-graph';
import { flowComponentColors, flowOutputPresentation } from '../../models/flow-presentation';
import { ContextActionsDirective, ContextActionPosition } from '../context-actions.directive';
import {
  ContextActionMenuComponent,
  ContextActionMenuData,
} from '../context-action-menu.component';
import { NotificationService } from '../../services/notification.service';
import { routeFlowLink } from '../flow-link-routing';
@Component({
  selector: 'app-flow-canvas',
  host: {
    '[class.read-only]': 'readOnly()',
    '(pointerdown)': 'beginNodeDrag($event)',
    '(mousedown)': 'beginNodeDrag($event)',
  },
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FlowComponentDrop,
    IconComponent,
    FlowSimulationStatusComponent,
    MatButtonModule,
    MatTooltipModule,
    ContextActionsDirective,
    ContextActionMenuComponent,
  ],
  templateUrl: './flow-canvas.component.html',
  styleUrl: './flow-canvas.component.scss',
})
export class FlowCanvasComponent {
  readonly readOnly = input(false);
  readonly mediaType = input<MediaType>('video');
  readonly simulationResult = input<SimulationResult | null>(null);
  readonly simulationDetailsRequested = output<void>();
  protected nodeTooltip(node: FlowGraphNode): string {
    return [node.name || node.component.name, flowBlockSummary(node, this.mediaType())]
      .filter(Boolean)
      .join(' · ');
  }
  protected configureWithKeyboard(event: Event, node: FlowGraphNode): void {
    if (event.target !== event.currentTarget) return;
    event.preventDefault();
    this.edit(node);
  }
  protected readonly outputPresentation = flowOutputPresentation;
  readonly componentDropped = output<FlowPaletteDrop>();
  protected dropComponent(event: FlowPaletteDrop): void {
    if (this.readOnly()) return;
    const view = this.viewport()?.nativeElement;
    if (!view) return;
    const rect = this.surface()?.nativeElement.getBoundingClientRect();
    if (!rect) return;
    this.componentDropped.emit({
      ...event,
      position: {
        x: Math.max(24, (event.point.x - rect.left) / this.scale()),
        y: Math.max(24, (event.point.y - rect.top) / this.scale()),
      },
    });
  }
  readonly nodes = input.required<readonly FlowGraphNode[]>();
  readonly connections = input.required<readonly FlowGraphConnection[]>();
  readonly showConnections = input(true);
  readonly autoLayoutRequest = input(0);
  readonly executedNodeIds = input<ReadonlySet<string> | null>(null);
  readonly executedConnectionIds = input<ReadonlySet<string> | null>(null);
  readonly nodeExecutionDetails = input<ReadonlyMap<
    string,
    { duration?: string; output?: string; status?: string }
  > | null>(null);
  readonly selectedNodeId = input<string | null>(null);
  readonly selectNode = output<string>();
  readonly configure = output<string>();
  readonly connect = output<FlowConnectionRequest>();
  readonly disconnect = output<string>();
  readonly remove = output<string>();
  readonly duplicate = output<string>();
  readonly askAi = output<string>();
  readonly paletteRequested = output<void>();
  readonly insert = output<{ sourceNodeId: string; output: FlowComponentOutput }>();
  readonly positionChanged = output<{ nodeId: string; position: FlowGraphPosition }>();
  readonly positionsChanged = output<ReadonlyMap<string, FlowGraphPosition>>();
  readonly packToggle = output<string>();
  readonly expandedPacks = input<readonly { readonly id: string; readonly label: string }[]>([]);
  readonly positions = input<ReadonlyMap<string, FlowGraphPosition>>(new Map());
  private readonly viewport = viewChild<ElementRef<HTMLElement>>('viewport');
  private readonly cards = viewChildren<ElementRef<HTMLElement>>('canvasCard');
  private readonly surface = viewChild<ElementRef<HTMLElement>>('canvasSurface');
  private readonly measurements = signal<ReadonlyMap<string, FlowCanvasNodeSize>>(new Map());
  private readonly anchors = signal<ReadonlyMap<string, FlowGraphPosition>>(new Map());
  private readonly previousLayout = signal<ReadonlyMap<string, FlowGraphPosition> | null>(null);
  protected readonly canUndoLayout = computed(() => this.previousLayout() !== null);
  private observer?: ResizeObserver;
  private readonly notifications = inject(NotificationService);
  protected readonly layout = computed(() =>
    layoutFlowCanvas(this.nodes(), this.connections(), this.measurements()),
  );
  protected readonly bounds = computed(() => {
    const nodes = this.nodes();
    return {
      left: Math.min(0, ...nodes.map((node) => this.position(node).x - 24)),
      top: Math.min(0, ...nodes.map((node) => this.position(node).y - 24)),
      right: Math.max(
        340,
        ...nodes.map((node) => this.position(node).x + this.nodeSize(node).width + 24),
      ),
      bottom: Math.max(
        200,
        ...nodes.map((node) => this.position(node).y + this.nodeSize(node).height + 24),
      ),
    };
  });
  protected readonly overview = signal({ x: 0, y: 0, width: 0, height: 0 });
  protected readonly miniViewBox = computed(
    () => `0 0 ${this.bounds().right} ${this.bounds().bottom}`,
  );
  private readonly obstacles = computed(() =>
    this.nodes().map((node) => {
      const p = this.wirePosition(node),
        size = this.nodeSize(node);
      return {
        left: p.x,
        right: p.x + size.width,
        top: p.y,
        bottom: p.y + size.height,
      };
    }),
  );
  protected readonly paths = computed(
    () =>
      new Map(
        this.connections().map((edge) => {
          const a = this.nodes().find((node) => node.id === edge.sourceNodeId),
            b = this.nodes().find((node) => node.id === edge.targetNodeId);
          return [
            edge.id,
            a && b
              ? routeFlowLink(
                  this.anchor(a, edge.sourceOutputId),
                  this.anchor(b, null),
                  this.obstacles(),
                  this.size().width,
                )
              : '',
          ];
        }),
      ),
  );
  protected readonly extent = signal({ width: 0, height: 0 });
  protected readonly size = computed(() => ({
    width: Math.max(this.bounds().right + 64, this.extent().width),
    height: Math.max(this.bounds().bottom + 64, this.extent().height),
  }));
  protected readonly scale = signal(1);
  protected readonly fitActive = signal(false);
  protected readonly liveDrag = signal<{ id: string; x: number; y: number } | null>(null);
  private wirePosition(node: FlowGraphNode): FlowGraphPosition {
    const p = this.position(node),
      drag = this.liveDrag();
    return drag?.id === node.id ? { x: p.x + drag.x, y: p.y + drag.y } : p;
  }
  protected dragPosition(node: FlowGraphNode): FlowGraphPosition {
    return this.wirePosition(node);
  }
  protected readonly pending = signal<{ node: FlowGraphNode; output: FlowComponentOutput } | null>(
    null,
  );
  protected readonly pointer = signal<FlowGraphPosition | null>(null);
  protected readonly context = signal<ContextActionMenuData | null>(null);
  private contextTarget: { node?: FlowGraphNode; edge?: FlowGraphConnection } = {};
  private stopDrag?: () => void;
  private stopNodeDrag?: () => void;
  protected beginNodeDrag(event: PointerEvent | MouseEvent): void {
    if (event.type === 'mousedown' && this.stopNodeDrag) return;
    const target = event.target as Element | null,
      handle = target?.closest('.canvas-icon.drag'),
      card = handle?.closest<HTMLElement>('.canvas-node'),
      nodeId = card?.dataset['nodeId'];
    if (this.readOnly() || !card || !nodeId) return;
    event.preventDefault();
    event.stopPropagation();
    this.stopNodeDrag?.();
    const start = {
        x: Number.parseFloat(card.style.left) || 24,
        y: Number.parseFloat(card.style.top) || 24,
      },
      startX = event.clientX,
      startY = event.clientY;
    this.selectNode.emit(nodeId);
    this.liveDrag.set({ id: nodeId, x: 0, y: 0 });
    const mouse = event.type === 'mousedown';
    const move = (pointer: PointerEvent | MouseEvent) =>
      this.liveDrag.set({
        id: nodeId,
        x: (pointer.clientX - startX) / this.scale(),
        y: (pointer.clientY - startY) / this.scale(),
      });
    const end = () => {
      const drag = this.liveDrag();
      if (drag?.id === nodeId)
        this.positionChanged.emit({
          nodeId,
          position: {
            x: Math.max(24, start.x + drag.x),
            y: Math.max(24, start.y + drag.y),
          },
        });
      this.liveDrag.set(null);
      this.stopNodeDrag?.();
    };
    const moveEvent = mouse ? 'mousemove' : 'pointermove';
    const endEvent = mouse ? 'mouseup' : 'pointerup';
    document.addEventListener(moveEvent, move as EventListener);
    document.addEventListener(endEvent, end, { once: true });
    if (!mouse) document.addEventListener('pointercancel', end, { once: true });
    this.stopNodeDrag = () => {
      document.removeEventListener(moveEvent, move as EventListener);
      document.removeEventListener(endEvent, end);
      document.removeEventListener('pointercancel', end);
      this.stopNodeDrag = undefined;
    };
  }
  protected readonly colors = flowComponentColors;
  protected readonly kindLabels = FLOW_COMPONENT_KIND_LABELS;
  protected nodeSize(node: FlowGraphNode): FlowCanvasNodeSize {
    return this.measurements().get(node.id) ?? estimateFlowCanvasNodeSize(node);
  }
  constructor() {
    let handledAutoLayoutRequest = 0;
    afterRenderEffect(() => {
      const request = this.autoLayoutRequest();
      if (!request || request === handledAutoLayoutRequest) return;
      handledAutoLayoutRequest = request;
      untracked(() => this.autoLayout());
    });
    inject(DestroyRef).onDestroy(() => {
      this.stopDrag?.();
      this.stopNodeDrag?.();
      this.observer?.disconnect();
    });
    afterRenderEffect(() => {
      this.cards();
      this.nodes();
      const surface = this.surface()?.nativeElement;
      this.observer?.disconnect();
      if (!surface) return;
      this.observer = new ResizeObserver(() => this.measure());
      for (const card of this.cards()) this.observer.observe(card.nativeElement);
      this.measure();
    });
    afterRenderEffect(() => {
      const id = this.selectedNodeId(),
        view = this.viewport()?.nativeElement;
      if (!id || !view) return;
      untracked(() => {
        const node = this.nodes().find((n) => n.id === id);
        if (!node) return;
        const p = this.position(node),
          left = p.x * this.scale(),
          top = p.y * this.scale();
        if (
          left < view.scrollLeft ||
          left + this.nodeSize(node).width * this.scale() > view.scrollLeft + view.clientWidth ||
          top < view.scrollTop ||
          top + this.nodeSize(node).height * this.scale() > view.scrollTop + view.clientHeight
        )
          view.scrollTo({
            left: Math.max(
              0,
              left - view.clientWidth / 2 + (this.nodeSize(node).width / 2) * this.scale(),
            ),
            top: Math.max(
              0,
              top - view.clientHeight / 2 + (this.nodeSize(node).height / 2) * this.scale(),
            ),
          });
      });
    });
  }
  private measure(): void {
    const sizes = new Map<string, FlowCanvasNodeSize>();
    const anchors = new Map<string, FlowGraphPosition>();
    for (const ref of this.cards()) {
      const card = ref.nativeElement;
      const id = card.dataset['nodeId']!;
      sizes.set(id, { width: card.offsetWidth, height: card.offsetHeight });
      for (const port of card.querySelectorAll<HTMLElement>('[data-port]')) {
        const bounds = card.getBoundingClientRect(),
          point = port.getBoundingClientRect();
        anchors.set(id + ':' + port.dataset['port'], {
          x: (point.left + point.width / 2 - bounds.left) / this.scale(),
          y: (point.top + point.height / 2 - bounds.top) / this.scale(),
        });
      }
    }
    if (JSON.stringify([...sizes]) !== JSON.stringify([...this.measurements()]))
      this.measurements.set(sizes);
    if (JSON.stringify([...anchors]) !== JSON.stringify([...this.anchors()]))
      this.anchors.set(anchors);
    this.updateOverview();
  }
  protected autoLayout(): void {
    this.previousLayout.set(new Map(this.nodes().map((node) => [node.id, this.position(node)])));
    this.positionsChanged.emit(this.layout().positions);
    this.extent.set({ width: 0, height: 0 });
    requestAnimationFrame(() => this.fit());
  }
  protected undoLayout(): void {
    const previous = this.previousLayout();
    if (previous) this.positionsChanged.emit(previous);
    this.previousLayout.set(null);
  }
  protected wheel(event: WheelEvent): void {
    if (!event.ctrlKey) return;
    event.preventDefault();
    const view = this.viewport()?.nativeElement;
    if (!view) return;
    const rect = view.getBoundingClientRect(),
      before = this.scale();
    const x = event.clientX - rect.left,
      y = event.clientY - rect.top;
    const next = Math.max(0.2, Math.min(1.75, before * Math.exp(-event.deltaY * 0.002)));
    const surfaceLeft = this.surface()!.nativeElement.getBoundingClientRect().left;
    const left =
      ((event.clientX - surfaceLeft) / before) * next +
      Math.max(0, (view.clientWidth - this.size().width * next) / 2) -
      x;
    const top = ((view.scrollTop + y) / before) * next - y;
    this.scale.set(next);
    requestAnimationFrame(() => {
      view.scrollTo(left, top);
      this.updateOverview();
    });
  }
  private updateOverview(): void {
    const view = this.viewport()?.nativeElement;
    if (view)
      this.overview.set({
        x: view.scrollLeft / this.scale(),
        y: view.scrollTop / this.scale(),
        width: view.clientWidth / this.scale(),
        height: view.clientHeight / this.scale(),
      });
  }
  protected miniNavigate(event: MouseEvent): void {
    this.navigateMiniTo(
      event.currentTarget as HTMLButtonElement,
      event.clientX,
      event.clientY,
      event.detail === 0,
    );
  }
  protected miniPanStart(event: PointerEvent): void {
    if (event.button !== 0 || this.pending()) return;
    const button = event.currentTarget as HTMLButtonElement;
    event.preventDefault();
    button.setPointerCapture(event.pointerId);
    this.navigateMiniTo(button, event.clientX, event.clientY);
    const move = (pointer: PointerEvent) =>
      this.navigateMiniTo(button, pointer.clientX, pointer.clientY);
    const finish = () => {
      button.removeEventListener('pointermove', move);
      button.removeEventListener('pointerup', finish);
      button.removeEventListener('pointercancel', finish);
    };
    button.addEventListener('pointermove', move);
    button.addEventListener('pointerup', finish, { once: true });
    button.addEventListener('pointercancel', finish, { once: true });
  }
  private navigateMiniTo(
    button: HTMLButtonElement,
    clientX: number,
    clientY: number,
    center = false,
  ): void {
    const rect = button.querySelector('svg')?.getBoundingClientRect();
    const view = this.viewport()?.nativeElement;
    if (!rect || !view) return;
    const x = center ? 0.5 : Math.max(0, Math.min(1, (clientX - rect.left) / rect.width));
    const y = center ? 0.5 : Math.max(0, Math.min(1, (clientY - rect.top) / rect.height));
    view.scrollTo(
      x * this.bounds().right * this.scale() - view.clientWidth / 2,
      y * this.bounds().bottom * this.scale() - view.clientHeight / 2,
    );
  }
  protected edit(node: FlowGraphNode): void {
    if (node.component.kind === 'pack') this.packToggle.emit(node.id);
    else if (!this.readOnly()) this.configure.emit(node.id);
  }
  protected position(node: FlowGraphNode): FlowGraphPosition {
    return (
      this.positions().get(node.id) ?? this.layout().positions.get(node.id) ?? { x: 80, y: 80 }
    );
  }
  protected zoom(delta: number): void {
    this.fitActive.set(false);
    this.scale.update((s) => Math.max(0.2, Math.min(1.75, s + delta)));
  }
  protected resetOrFit(): void {
    if (this.fitActive()) {
      this.fitActive.set(false);
      this.scale.set(1);
      this.viewport()?.nativeElement.scrollTo(0, 0);
      requestAnimationFrame(() => this.updateOverview());
      return;
    }
    if (this.scale() === 1) {
      this.fit();
      return;
    }
    this.scale.set(1);
    this.viewport()?.nativeElement.scrollTo(0, 0);
    requestAnimationFrame(() => this.updateOverview());
  }
  protected fit(): void {
    const view = this.viewport()?.nativeElement;
    if (!view) return;
    this.extent.set({ width: 0, height: 0 });
    this.scale.set(
      Math.max(
        0.2,
        Math.min(
          1,
          (view.clientWidth - 40) / this.bounds().right,
          (view.clientHeight - 40) / this.bounds().bottom,
        ),
      ),
    );
    this.fitActive.set(true);
    view.scrollTo(0, 0);
    requestAnimationFrame(() => this.updateOverview());
  }
  protected navigate(): void {
    this.updateOverview();
    const view = this.viewport()?.nativeElement;
    if (!view) return;
    if (
      view.scrollLeft > 0 &&
      view.scrollLeft + view.clientWidth > this.size().width * this.scale() - 180
    )
      this.extent.update((s) => ({ ...s, width: this.size().width + 900 }));
    if (
      view.scrollTop > 0 &&
      view.scrollTop + view.clientHeight > this.size().height * this.scale() - 180
    )
      this.extent.update((s) => ({ ...s, height: this.size().height + 700 }));
  }
  protected pan(event: PointerEvent): void {
    if (
      (event.button !== 0 && event.button !== 1) ||
      !(event.target instanceof Element) ||
      event.target.closest('article,button,path')
    )
      return;
    const view = this.viewport()!.nativeElement,
      x = event.clientX,
      y = event.clientY,
      left = view.scrollLeft,
      top = view.scrollTop;
    event.preventDefault();
    view.setPointerCapture(event.pointerId);
    const move = (e: PointerEvent) => {
      view.scrollLeft = left - (e.clientX - x);
      view.scrollTop = top - (e.clientY - y);
    };
    const end = () => {
      view.removeEventListener('pointermove', move);
      view.removeEventListener('pointerup', end);
      view.removeEventListener('pointercancel', end);
    };
    view.addEventListener('pointermove', move);
    view.addEventListener('pointerup', end, { once: true });
    view.addEventListener('pointercancel', end, { once: true });
  }
  protected path(edge: FlowGraphConnection): string {
    return this.paths().get(edge.id) ?? '';
  }
  private anchor(node: FlowGraphNode, port: string | null): FlowGraphPosition {
    const p = this.wirePosition(node),
      offset = this.anchors().get(node.id + ':' + (port === null ? 'input' : 'output:' + port));
    const size = this.nodeSize(node);
    const index = node.component.outputs.findIndex((output) => output.id === port);
    const x =
      port === null
        ? size.width / 2
        : 8 + ((size.width - 16) * (index + 0.5)) / Math.max(1, node.component.outputs.length);
    return { x: p.x + (offset?.x ?? x), y: p.y + (offset?.y ?? (port === null ? 0 : size.height)) };
  }
  protected ghost(): string {
    const pending = this.pending(),
      p = this.pointer();
    if (!pending || !p) return '';
    const a = this.anchor(pending.node, pending.output.id),
      middle = (a.y + p.y) / 2;
    return `M ${a.x} ${a.y} V ${middle} H ${p.x} V ${p.y}`;
  }
  protected begin(node: FlowGraphNode, output: FlowComponentOutput, event?: PointerEvent): void {
    if (this.readOnly()) return;
    event?.stopPropagation();
    if (
      this.connections().some((e) => e.sourceNodeId === node.id && e.sourceOutputId === output.id)
    ) {
      this.notifications.show('Déconnectez cette sortie avant de la relier à une autre entrée.');
      return;
    }
    this.pending.set({ node, output });
    if (!event) return;
    const move = (e: PointerEvent) => {
      const root = this.viewport()?.nativeElement;
      if (!root) return;
      const rect = this.surface()?.nativeElement.getBoundingClientRect();
      if (!rect) return;
      this.pointer.set({
        x: (e.clientX - rect.left) / this.scale(),
        y: (e.clientY - rect.top) / this.scale(),
      });
    };
    const up = (e: PointerEvent) => {
      const element = document
        .elementFromPoint(e.clientX, e.clientY)
        ?.closest<HTMLElement>('[data-canvas-input]');
      if (element) this.finish(element.dataset['canvasInput']!);
      this.pointer.set(null);
      this.stopDrag?.();
    };
    this.stopDrag?.();
    document.addEventListener('pointermove', move);
    document.addEventListener('pointerup', up, { once: true });
    const cancel = () => {
      this.pending.set(null);
      this.pointer.set(null);
      this.stopDrag?.();
    };
    document.addEventListener('pointercancel', cancel, { once: true });
    this.stopDrag = () => {
      document.removeEventListener('pointermove', move);
      document.removeEventListener('pointerup', up);
      document.removeEventListener('pointercancel', cancel);
      this.stopDrag = undefined;
    };
  }
  protected canFinish(node: FlowGraphNode): boolean {
    const p = this.pending();
    return (
      !!p &&
      canConnectFlowGraph(this.nodes(), this.connections(), {
        sourceNodeId: p.node.id,
        sourceOutputId: p.output.id,
        targetNodeId: node.id,
      })
    );
  }
  protected finish(id: string): void {
    const node = this.nodes().find((n) => n.id === id);
    if (!node || !this.canFinish(node)) return;
    const p = this.pending()!;
    this.connect.emit({ sourceNodeId: p.node.id, sourceOutputId: p.output.id, targetNodeId: id });
    this.pending.set(null);
    this.pointer.set(null);
  }
  protected menu(
    position: ContextActionPosition,
    node?: FlowGraphNode,
    edge?: FlowGraphConnection,
  ): void {
    if (this.readOnly()) return;
    this.contextTarget = { node, edge };
    this.context.set({
      ...position,
      items: node
        ? [
            { id: 'configure', label: 'Configurer', disabled: node.component.kind === 'pack' },
            {
              id: 'duplicate',
              label: 'Dupliquer',
              disabled: node.component.kind === 'input' || node.component.kind === 'pack',
            },
            ...(node.component.kind === 'pack'
              ? [{ id: 'expand-pack', label: 'Déplier les composants' }]
              : []),
            { id: 'ai', label: 'Demander à l’IA' },
            { id: 'remove', label: 'Supprimer', disabled: node.component.kind === 'input' },
          ]
        : edge
          ? [
              { id: 'insert', label: 'Insérer une étape' },
              { id: 'disconnect', label: 'Supprimer la liaison' },
              { id: 'ai-edge', label: 'Expliquer cette liaison avec l’IA' },
            ]
          : [
              { id: 'palette', label: 'Ajouter une étape' },
              { id: 'fit', label: 'Tout afficher' },
              { id: 'zoom', label: 'Zoom à 100 %' },
            ],
    });
  }
  protected menuButton(event: MouseEvent, node: FlowGraphNode): void {
    event.stopPropagation();
    const origin = event.currentTarget as HTMLElement;
    const r = origin.getBoundingClientRect();
    this.menu({ x: r.left, y: r.bottom, origin }, node);
  }
  protected action(id: string): void {
    if (this.readOnly()) return;
    const { node, edge } = this.contextTarget;
    this.context.set(null);
    if (id === 'configure' && node) this.configure.emit(node.id);
    if (id === 'duplicate' && node) this.duplicate.emit(node.id);
    if (id === 'remove' && node) this.remove.emit(node.id);
    if (id === 'expand-pack' && node) this.packToggle.emit(node.id);
    if (id === 'ai' && node) this.askAi.emit(node.id);
    if (id === 'ai-edge' && edge) this.askAi.emit(edge.sourceNodeId);
    if (id === 'disconnect' && edge) this.disconnect.emit(edge.id);
    if (id === 'insert' && edge) {
      const source = this.nodes().find((n) => n.id === edge.sourceNodeId),
        output = source?.component.outputs.find((o) => o.id === edge.sourceOutputId);
      if (source && output) this.insert.emit({ sourceNodeId: source.id, output });
    }
    if (id === 'fit') this.fit();
    if (id === 'zoom') this.scale.set(1);
    if (id === 'palette') this.paletteRequested.emit();
  }
}
