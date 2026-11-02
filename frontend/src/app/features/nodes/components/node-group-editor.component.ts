import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  input,
  output,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { StatusBadgeComponent } from '../../../shared/ui/status-badge.component';
import {
  NodeGroupCriteria,
  NodeGroupMode,
  NodeGroupResponse,
  NodeResponse,
  formatNodeLabel,
} from '../../../shared/models/cluster';
import {
  extractAvailableArchitectures,
  extractAvailableOperatingSystems,
  extractAvailableTools,
  filterNodesByCriteria,
  validateLabelKey,
} from '../../../shared/models/node-group-rules';
import { SearchFieldComponent } from '../../../shared/ui/search-field.component';

export interface SaveNodeGroupPayload {
  readonly name: string;
  readonly description: string;
  readonly mode: NodeGroupMode;
  readonly criteria: NodeGroupCriteria;
  readonly labels: Record<string, string | null>;
  readonly nodeIds: readonly string[];
}

@Component({
  selector: 'app-node-group-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FormsModule,
    MatButtonModule,
    MatCheckboxModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    SearchFieldComponent,
    StatusBadgeComponent,
  ],
  templateUrl: './node-group-editor.component.html',
  styleUrl: './node-group-editor.component.scss',
})
export class NodeGroupEditorComponent {
  readonly group = input<NodeGroupResponse | null>(null);
  readonly nodes = input<readonly NodeResponse[]>([]);
  readonly busy = input(false);

  readonly saved = output<SaveNodeGroupPayload>();
  readonly cancelled = output<void>();

  protected readonly Math = Math;

  // Form state
  protected readonly name = signal('');
  protected readonly description = signal('');
  protected readonly mode = signal<NodeGroupMode>('manual');

  // Manual mode state
  protected readonly selectedNodeIds = signal<Set<string>>(new Set());
  protected readonly nodeSearchQuery = signal('');

  // Automatic criteria state
  protected readonly selectedRole = signal<'all' | 'worker' | 'server'>('all');
  protected readonly selectedOs = signal<string>('');
  protected readonly selectedArchitecture = signal<string>('');
  protected readonly minMemoryGb = signal<number | null>(null);
  protected readonly minProcessorCount = signal<number | null>(null);
  protected readonly minConcurrentTasks = signal<number | null>(null);
  protected readonly selectedTools = signal<Set<string>>(new Set());
  protected readonly selectedEncoders = signal<Set<string>>(new Set());
  protected readonly customTool = signal('');

  // Labels state
  protected readonly labels = signal<Record<string, string | null>>({});
  protected readonly newLabelKey = signal('');
  protected readonly newLabelValue = signal('');
  protected readonly labelError = signal<string | null>(null);

  // Available options inferred from cluster nodes
  protected readonly availableOs = computed(() => extractAvailableOperatingSystems(this.nodes()));
  protected readonly availableArchitectures = computed(() =>
    extractAvailableArchitectures(this.nodes()),
  );
  protected readonly availableTools = computed(() => extractAvailableTools(this.nodes()));
  protected readonly availableEncoderOptions = computed(() => [
    { id: 'av1', label: 'AV1' },
    { id: 'hevc', label: 'HEVC / H.265' },
    { id: 'h264', label: 'H.264' },
  ]);

  // Computed criteria for automatic group
  protected readonly automaticCriteria = computed<NodeGroupCriteria>(() => {
    const role = this.selectedRole();
    return {
      roles: role === 'all' ? [] : [role],
      operatingSystem: this.selectedOs().trim() || null,
      architecture: this.selectedArchitecture().trim() || null,
      minimumMemoryMb:
        this.minMemoryGb() != null && this.minMemoryGb()! > 0
          ? Math.round(this.minMemoryGb()! * 1024)
          : null,
      minimumProcessorCount: this.minProcessorCount(),
      minimumConcurrentTasks: this.minConcurrentTasks(),
      requiredToolIds: Array.from(this.selectedTools()),
      requiredEncoders: Array.from(this.selectedEncoders()),
    };
  });

  // Real-time matched nodes for automatic criteria
  protected readonly matchedNodes = computed(() =>
    filterNodesByCriteria(this.automaticCriteria(), this.nodes()),
  );

  // Filtered nodes for manual selection list
  protected readonly filteredNodes = computed(() => {
    const query = this.nodeSearchQuery().trim().toLowerCase();
    const all = this.nodes();
    if (!query) return all;
    return all.filter((n) => {
      const display = formatNodeLabel(n.name, n.displayName).toLowerCase();
      const os = n.inventory?.operatingSystem?.toLowerCase() ?? '';
      const arch = n.inventory?.architecture?.toLowerCase() ?? '';
      return display.includes(query) || os.includes(query) || arch.includes(query);
    });
  });

  // Validation rules
  protected readonly hasAtLeastOneCriterion = computed(() => {
    const c = this.automaticCriteria();
    return (
      (c.roles && c.roles.length > 0) ||
      !!c.operatingSystem ||
      !!c.architecture ||
      c.minimumMemoryMb != null ||
      c.minimumProcessorCount != null ||
      c.minimumConcurrentTasks != null ||
      (c.requiredToolIds && c.requiredToolIds.length > 0) ||
      (c.requiredEncoders && c.requiredEncoders.length > 0)
    );
  });

  protected readonly canSave = computed(() => {
    const nameValid = this.name().trim().length > 0 && this.name().trim().length <= 128;
    if (!nameValid) return false;
    if (this.mode() === 'automatic') {
      return this.hasAtLeastOneCriterion();
    }
    return true;
  });

  private lastLoadedGroupId: string | null | undefined = undefined;

  constructor() {
    effect(() => {
      const g = this.group();
      const currentId = g ? g.id : null;
      if (this.lastLoadedGroupId === currentId) {
        return;
      }
      this.lastLoadedGroupId = currentId;

      if (g) {
        this.name.set(g.name);
        this.description.set(g.description ?? '');
        this.mode.set(g.mode);
        this.selectedNodeIds.set(new Set(g.nodeIds));
        this.labels.set({ ...g.labels });

        const c = g.criteria;
        this.selectedRole.set(c.roles?.[0] ?? 'all');
        this.selectedOs.set(c.operatingSystem ?? '');
        this.selectedArchitecture.set(c.architecture ?? '');
        this.minMemoryGb.set(
          c.minimumMemoryMb != null ? Math.round(c.minimumMemoryMb / 1024) : null,
        );
        this.minProcessorCount.set(c.minimumProcessorCount ?? null);
        this.minConcurrentTasks.set(c.minimumConcurrentTasks ?? null);
        this.selectedTools.set(new Set(c.requiredToolIds ?? []));
        this.selectedEncoders.set(new Set(c.requiredEncoders ?? []));
      } else {
        this.reset();
      }
    });
  }

  protected setMode(mode: NodeGroupMode): void {
    this.mode.set(mode);
  }

  protected toggleNode(nodeId: string): void {
    const set = new Set(this.selectedNodeIds());
    if (set.has(nodeId)) {
      set.delete(nodeId);
    } else {
      set.add(nodeId);
    }
    this.selectedNodeIds.set(set);
  }

  protected selectAllVisibleNodes(): void {
    const set = new Set(this.selectedNodeIds());
    for (const n of this.filteredNodes()) {
      set.add(n.id);
    }
    this.selectedNodeIds.set(set);
  }

  protected clearNodeSelection(): void {
    this.selectedNodeIds.set(new Set());
  }

  protected toggleTool(toolId: string): void {
    const set = new Set(this.selectedTools());
    if (set.has(toolId)) {
      set.delete(toolId);
    } else {
      set.add(toolId);
    }
    this.selectedTools.set(set);
  }

  protected toggleEncoder(encoderId: string): void {
    const set = new Set(this.selectedEncoders());
    if (set.has(encoderId)) {
      set.delete(encoderId);
    } else {
      set.add(encoderId);
    }
    this.selectedEncoders.set(set);
  }

  protected addCustomTool(): void {
    const tool = this.customTool().trim().toLowerCase();
    if (!tool) return;
    const set = new Set(this.selectedTools());
    set.add(tool);
    this.selectedTools.set(set);
    this.customTool.set('');
  }

  protected addLabel(): void {
    const rawKey = this.newLabelKey().trim();
    if (!rawKey) {
      this.labelError.set('La clé ne peut pas être vide.');
      return;
    }
    if (!validateLabelKey(rawKey)) {
      this.labelError.set(
        'Format de clé invalide (lettres, chiffres, tirets et points autorisés).',
      );
      return;
    }
    const val = this.newLabelValue().trim();
    const current = { ...this.labels() };
    current[rawKey] = val.length > 0 ? val : null;
    this.labels.set(current);
    this.newLabelKey.set('');
    this.newLabelValue.set('');
    this.labelError.set(null);
  }

  protected removeLabel(key: string): void {
    const current = { ...this.labels() };
    delete current[key];
    this.labels.set(current);
  }

  protected labelEntries = computed(() => Object.entries(this.labels()));

  protected submit(): void {
    if (!this.canSave() || this.busy()) return;

    const mode = this.mode();
    const payload: SaveNodeGroupPayload = {
      name: this.name().trim(),
      description: this.description().trim(),
      mode,
      criteria: mode === 'automatic' ? this.automaticCriteria() : {},
      labels: this.labels(),
      nodeIds: mode === 'manual' ? Array.from(this.selectedNodeIds()) : [],
    };

    this.saved.emit(payload);
  }

  protected formatNode(node: NodeResponse): string {
    return formatNodeLabel(node.name, node.displayName);
  }

  protected onNameInput(event: Event): void {
    this.name.set((event.target as HTMLInputElement).value);
  }

  protected onDescriptionInput(event: Event): void {
    this.description.set((event.target as HTMLTextAreaElement).value);
  }

  protected onCpuInput(event: Event): void {
    const val = (event.target as HTMLInputElement).value;
    this.minProcessorCount.set(val ? Number(val) : null);
  }

  protected onTasksInput(event: Event): void {
    const val = (event.target as HTMLInputElement).value;
    this.minConcurrentTasks.set(val ? Number(val) : null);
  }

  protected onLabelKeyInput(event: Event): void {
    this.newLabelKey.set((event.target as HTMLInputElement).value);
  }

  protected onLabelValueInput(event: Event): void {
    this.newLabelValue.set((event.target as HTMLInputElement).value);
  }

  private reset(): void {
    this.name.set('');
    this.description.set('');
    this.mode.set('manual');
    this.selectedNodeIds.set(new Set());
    this.nodeSearchQuery.set('');
    this.selectedRole.set('all');
    this.selectedOs.set('');
    this.selectedArchitecture.set('');
    this.minMemoryGb.set(null);
    this.minProcessorCount.set(null);
    this.minConcurrentTasks.set(null);
    this.selectedTools.set(new Set());
    this.selectedEncoders.set(new Set());
    this.customTool.set('');
    this.labels.set({});
    this.newLabelKey.set('');
    this.newLabelValue.set('');
    this.labelError.set(null);
  }
}
