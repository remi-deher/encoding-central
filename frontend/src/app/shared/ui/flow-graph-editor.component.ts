import {
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
  output,
  signal,
  afterRenderEffect,
  untracked,
} from '@angular/core';
import { FlowPaletteDrop } from './flow-palette-drag';
import { FlowCanvasComponent } from './flow-canvas/flow-canvas.component';
import { FlowGraphPosition } from '../models/flow-component';
import { MediaType } from '../models/media-type';
import {
  FlowComponentOutput,
  FlowConnectionRequest,
  FlowGraphConnection,
  FlowGraphNode,
} from '../models/flow-component';
import { FlowComponentPackInstance } from '../models/flow-validation';
import { projectFlowPacks } from '../models/flow-pack-projection';
import { layoutFlowCanvas, estimateFlowCanvasNodeSize } from '../models/flow-canvas';
import { SimulationResult } from '../models/flow-simulation';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-flow-graph-editor',
  host: { '[class.read-only]': 'readOnly()' },
  imports: [FlowCanvasComponent],
  templateUrl: './flow-graph-editor.component.html',
  styleUrl: './flow-graph-editor.component.scss',
})
export class FlowGraphEditorComponent {
  readonly componentDropped = output<FlowPaletteDrop>();
  readonly simulationResult = input<SimulationResult | null>(null);
  readonly simulationDetailsRequested = output<void>();
  readonly autoLayoutRequest = input(0);
  readonly readOnly = input(false);
  readonly layoutKey = input('flow');
  readonly duplicateRequested = output<string>();
  readonly aiRequested = output<string>();
  readonly paletteRequested = output<void>();
  readonly mediaType = input.required<MediaType>();
  readonly nodes = input.required<readonly FlowGraphNode[]>();
  readonly connections = input.required<readonly FlowGraphConnection[]>();
  readonly showConnections = input(true);
  readonly componentPacks = input<readonly FlowComponentPackInstance[]>([]);
  readonly configurationRequested = output<string>();
  readonly branchComponentRequested = output<{
    readonly sourceNodeId: string;
    readonly output: FlowComponentOutput;
  }>();
  readonly connectionRequested = output<FlowConnectionRequest>();
  readonly nodeRemoved = output<string>();
  readonly connectionRemoved = output<string>();
  private readonly selectedNodeId = signal<string | null>(null);
  private readonly canvasPositions = signal<ReadonlyMap<string, FlowGraphPosition>>(new Map());
  private readonly expandedPacks = signal<ReadonlySet<string>>(new Set());
  protected readonly projection = computed(() =>
    projectFlowPacks(this.nodes(), this.connections(), this.componentPacks(), this.expandedPacks()),
  );
  protected readonly visualNodes = computed(() => this.projection().nodes);
  protected readonly visualConnections = computed(() => this.projection().connections);
  protected readonly expandedPackList = computed(() =>
    this.componentPacks()
      .filter((pack) => this.expandedPacks().has(pack.id))
      .map((pack) => ({ id: pack.id, label: pack.name })),
  );

  constructor() {
    afterRenderEffect(() => {
      const key = this.layoutKey();
      try {
        const data = JSON.parse(localStorage.getItem('encoding.flow-layout.' + key) ?? '[]');
        this.canvasPositions.set(
          new Map(
            data.filter(
              (entry: unknown) =>
                Array.isArray(entry) &&
                typeof entry[0] === 'string' &&
                Number.isFinite(entry[1]?.x) &&
                Number.isFinite(entry[1]?.y),
            ),
          ),
        );
      } catch {
        this.canvasPositions.set(new Map());
      }
    });
    afterRenderEffect(() => {
      this.visualNodes();
      untracked(() => this.ensureCanvasPositions());
    });
  }

  setCanvasPosition(event: { nodeId: string; position: FlowGraphPosition }): void {
    this.setCanvasPositions(new Map(this.canvasPositions()).set(event.nodeId, event.position));
  }

  setCanvasPositions(positions: ReadonlyMap<string, FlowGraphPosition>): void {
    this.canvasPositions.set(positions);
    try {
      localStorage.setItem(
        'encoding.flow-layout.' + this.layoutKey(),
        JSON.stringify([...positions]),
      );
    } catch {
      /* Layout remains available in memory. */
    }
  }

  protected requestConfiguration(nodeId: string): void {
    if (this.readOnly()) return;
    this.selectedNodeId.set(nodeId);
    this.configurationRequested.emit(nodeId);
  }

  protected onCanvasConnect(request: FlowConnectionRequest): void {
    const source = this.visualNodes().find((node) => node.id === request.sourceNodeId);
    const target = this.visualNodes().find((node) => node.id === request.targetNodeId);
    const output = source?.component.outputs.find(
      (candidate) => candidate.id === request.sourceOutputId,
    );
    if (source && target && output) {
      const sourcePack = this.packForVisualNode(source);
      const pack = this.packForVisualNode(target);
      const sourceBinding = sourcePack?.outputs.find((candidate) => candidate.id === output.id);
      this.connectionRequested.emit({
        ...request,
        sourceNodeId: sourceBinding?.sourceNodeId ?? request.sourceNodeId,
        sourceOutputId: sourceBinding?.sourceOutputId ?? request.sourceOutputId,
        targetNodeId: pack?.inputNodeId ?? target.id,
      });
    }
  }

  protected onCanvasDisconnect(id: string): void {
    this.connectionRemoved.emit(id);
  }

  protected onCanvasInsert(event: { sourceNodeId: string; output: FlowComponentOutput }): void {
    const node = this.visualNodes().find((candidate) => candidate.id === event.sourceNodeId);
    if (!node) return;
    const pack = this.packForVisualNode(node);
    const binding = pack?.outputs.find((candidate) => candidate.id === event.output.id);
    const sourceNodeId = binding?.sourceNodeId ?? node.id;
    const sourceOutputId = binding?.sourceOutputId ?? event.output.id;
    const source = this.nodes().find((candidate) => candidate.id === sourceNodeId);
    const output = source?.component.outputs.find((candidate) => candidate.id === sourceOutputId);
    if (source && output) this.branchComponentRequested.emit({ sourceNodeId, output });
  }

  protected togglePack(id: string): void {
    const instance =
      this.projection().instancesByVisualId.get(id) ??
      this.componentPacks().find((pack) => pack.id === id);
    if (!instance) return;
    this.expandedPacks.update((current) => {
      const next = new Set(current);
      if (next.has(instance.id)) next.delete(instance.id);
      else next.add(instance.id);
      return next;
    });
  }

  protected packForVisualNode(node: FlowGraphNode): FlowComponentPackInstance | undefined {
    return this.projection().instancesByVisualId.get(node.id);
  }

  private ensureCanvasPositions(): void {
    const nodes = this.visualNodes();
    const positions = new Map(this.canvasPositions());
    const layout = layoutFlowCanvas(nodes, this.visualConnections());
    let changed = false;
    for (const node of nodes) {
      if (positions.has(node.id)) continue;
      let position = layout.positions.get(node.id)!;
      const size = estimateFlowCanvasNodeSize(node);
      while (
        nodes.some((other) => {
          const occupied = positions.get(other.id);
          const otherSize = estimateFlowCanvasNodeSize(other);
          return (
            occupied &&
            position.x < occupied.x + otherSize.width + 24 &&
            position.x + size.width + 24 > occupied.x &&
            position.y < occupied.y + otherSize.height + 32 &&
            position.y + size.height + 32 > occupied.y
          );
        })
      )
        position = { x: position.x, y: position.y + size.height + 88 };
      positions.set(node.id, position);
      changed = true;
    }
    if (changed) this.setCanvasPositions(positions);
  }
}
