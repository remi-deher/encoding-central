import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnDestroy,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatMenuModule } from '@angular/material/menu';
import { MatSelectModule } from '@angular/material/select';
import { retry, Subscription, tap } from 'rxjs';
import { SystemLogEntry, SystemLogLevel } from '../../models/system-logs';
import {
  LOG_LEVEL_LABELS,
  LOG_LEVEL_RANK,
  SystemLogGroup,
  formatSystemLog,
  mergeSystemLogs,
  serializeSystemLogs,
  summarizeSystemLogs,
  systemLogsCsv,
} from '../../models/system-log-presentation';
import { ClipboardService } from '../../services/clipboard.service';
import { FileDownload } from '../../services/file-download';
import { NotificationService } from '../../services/notification.service';
import { SystemLogsService } from '../../services/system-logs.service';
import { SearchFieldComponent } from '../search-field.component';

@Component({
  selector: 'app-system-log-viewer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    MatButtonModule,
    MatButtonToggleModule,
    MatCheckboxModule,
    MatFormFieldModule,
    MatMenuModule,
    MatSelectModule,
    SearchFieldComponent,
  ],
  templateUrl: './system-log-viewer.component.html',
  styleUrl: './system-log-viewer.component.scss',
})
export class SystemLogViewerComponent implements OnDestroy {
  private readonly systemLogsService = inject(SystemLogsService);
  private readonly clipboard = inject(ClipboardService);
  private readonly fileDownload = inject(FileDownload);
  private readonly notifications = inject(NotificationService);
  readonly nodeId = input<string | null>(null);
  readonly title = input('Journaux système');
  readonly showToolbar = input(true);
  readonly maxBufferSize = input(1000);
  protected readonly terminalContainer = viewChild<ElementRef<HTMLElement>>('terminalContainer');
  protected readonly view = signal<'summary' | 'console'>('summary');
  protected readonly selectedLevel = signal<SystemLogLevel | 'All'>('All');
  protected readonly selectedCategory = signal('');
  protected readonly searchText = signal('');
  protected readonly activeFilterCount = computed(
    () => (this.selectedLevel() !== 'All' ? 1 : 0) + (this.selectedCategory() ? 1 : 0),
  );
  protected readonly availableCategories = signal<readonly string[]>([]);
  protected readonly displayedEntries = signal<readonly SystemLogEntry[]>([]);
  protected readonly isPaused = signal(false);
  protected readonly pendingCount = signal(0);
  protected readonly autoScrollEnabled = signal(true);
  protected readonly wrapLines = signal(true);
  protected readonly isConnected = signal(false);
  protected readonly isLoadingHistory = signal(true);
  protected readonly historyError = signal(false);
  protected readonly streamFailed = signal(false);
  protected readonly consoleSelection = signal('');
  protected readonly focusedGroup = signal<string | null>(null);
  protected readonly levelLabels = LOG_LEVEL_LABELS;
  protected readonly filteredEntries = computed(() => {
    const query = this.searchText().trim().toLocaleLowerCase('fr');
    const level = this.selectedLevel();
    const category = this.selectedCategory();
    return this.displayedEntries().filter(
      (entry) =>
        (level === 'All' || LOG_LEVEL_RANK[entry.level] >= LOG_LEVEL_RANK[level]) &&
        (!category || entry.category === category) &&
        (!query ||
          [
            entry.message,
            entry.exception,
            entry.category,
            entry.nodeName,
            entry.id,
            entry.eventId,
          ].some(
            (value) => value != null && String(value).toLocaleLowerCase('fr').includes(query),
          )),
    );
  });
  protected readonly groups = computed(() => summarizeSystemLogs(this.filteredEntries()));
  protected readonly errorCount = computed(
    () =>
      this.filteredEntries().filter((entry) => LOG_LEVEL_RANK[entry.level] >= LOG_LEVEL_RANK.Error)
        .length,
  );
  protected readonly warningCount = computed(
    () => this.filteredEntries().filter((entry) => entry.level === 'Warning').length,
  );
  protected readonly categories = computed(() =>
    [
      ...new Set([
        ...this.availableCategories(),
        ...this.displayedEntries().map((entry) => entry.category),
      ]),
    ].sort(),
  );
  private readonly focusedIds = computed(
    () =>
      new Set(
        this.groups()
          .find((group) => group.key === this.focusedGroup())
          ?.entries.map((entry) => entry.id) ?? [],
      ),
  );
  private pendingBuffer: SystemLogEntry[] = [];
  private streamSub: Subscription | null = null;
  private historySub: Subscription | null = null;
  private categorySub: Subscription | null = null;
  private scrollTimer: ReturnType<typeof setTimeout> | null = null;
  protected readonly bufferLimit = computed(() =>
    Math.max(1, Math.floor(this.maxBufferSize()) || 1),
  );
  readonly levelOptions: readonly { value: SystemLogLevel | 'All'; label: string }[] = [
    { value: 'All', label: 'Tous les niveaux' },
    { value: 'Trace', label: 'Trace et plus' },
    { value: 'Debug', label: 'Débogage et plus' },
    { value: 'Information', label: 'Information et plus' },
    { value: 'Warning', label: 'Avertissement et plus' },
    { value: 'Error', label: 'Erreur et plus' },
    { value: 'Critical', label: 'Critique' },
  ];
  constructor() {
    effect(() => {
      const nodeId = this.nodeId();
      untracked(() => {
        this.categorySub?.unsubscribe();
        this.availableCategories.set([]);
        this.categorySub = this.systemLogsService.getCategories(nodeId).subscribe({
          next: (categories) => this.availableCategories.set(categories),
          error: () => {},
        });
      });
    });
    effect(() => {
      const nodeId = this.nodeId() || undefined;
      const minLevel =
        this.selectedLevel() === 'All' ? undefined : (this.selectedLevel() as SystemLogLevel);
      const category = this.selectedCategory() || undefined;
      const limit = this.bufferLimit();
      untracked(() => this.reloadLogsAndStream({ nodeId, minLevel, category }, limit));
    });
  }
  ngOnDestroy(): void {
    this.closeStream();
    this.historySub?.unsubscribe();
    this.categorySub?.unsubscribe();
    if (this.scrollTimer) clearTimeout(this.scrollTimer);
  }
  setLevel(level: SystemLogLevel | 'All'): void {
    this.selectedLevel.set(level);
    this.clearFocus();
  }
  setCategory(category: string): void {
    this.selectedCategory.set(category);
    this.clearFocus();
  }
  onCategoryChange(event: Event): void {
    this.setCategory((event.target as HTMLSelectElement).value);
  }
  onSearchText(value: string): void {
    this.searchText.set(value);
    this.clearFocus();
  }
  clearSearch(): void {
    this.searchText.set('');
    this.clearFocus();
  }
  resetFilters(): void {
    this.selectedLevel.set('All');
    this.selectedCategory.set('');
    this.clearSearch();
  }
  private clearFocus(): void {
    this.consoleSelection.set('');
    this.focusedGroup.set(null);
  }
  changeView(value: 'summary' | 'console'): void {
    this.view.set(value);
    this.consoleSelection.set('');
    if (value === 'console') this.scheduleScroll();
  }
  showGroup(group: SystemLogGroup): void {
    this.view.set('console');
    this.focusedGroup.set(group.key);
    this.consoleSelection.set('');
    this.autoScrollEnabled.set(false);
    this.scheduleScroll(group.entries[0]?.id);
  }
  isFocused(entry: SystemLogEntry): boolean {
    return this.focusedIds().has(entry.id);
  }
  private scheduleScroll(id?: string): void {
    if (this.scrollTimer) clearTimeout(this.scrollTimer);
    this.scrollTimer = setTimeout(() => {
      const element = this.terminalContainer()?.nativeElement;
      if (!element) return;
      if (id) {
        const target = [...element.querySelectorAll<HTMLElement>('[data-log-id]')].find(
          (item) => item.dataset['logId'] === id,
        );
        if (target) element.scrollTop = target.offsetTop - element.offsetTop;
      } else if (this.autoScrollEnabled() && !this.activeTextSelection()) this.scrollToBottom();
    }, 0);
  }
  togglePause(): void {
    if (this.isPaused()) {
      this.displayedEntries.set(
        mergeSystemLogs([...this.displayedEntries(), ...this.pendingBuffer], this.bufferLimit()),
      );
      this.pendingBuffer = [];
      this.pendingCount.set(0);
      this.isPaused.set(false);
      this.scheduleScroll();
    } else this.isPaused.set(true);
  }
  clearLogs(): void {
    this.displayedEntries.set([]);
    this.pendingBuffer = [];
    this.pendingCount.set(0);
    this.clearFocus();
  }
  private activeTextSelection(): string {
    const selection = this.terminalContainer()?.nativeElement.ownerDocument.getSelection();
    const element = this.terminalContainer()?.nativeElement;
    return selection?.rangeCount &&
      element?.contains(selection.getRangeAt(0).commonAncestorContainer)
      ? selection.toString()
      : '';
  }
  captureSelection(): void {
    this.consoleSelection.set(this.activeTextSelection());
  }
  async copySelection(): Promise<void> {
    const text = this.consoleSelection();
    if (!text) return;
    this.notifications.show(
      (await this.clipboard.copy(text))
        ? 'Extrait copié dans le presse-papier.'
        : 'Impossible de copier l’extrait.',
    );
  }
  async copyLogs(): Promise<void> {
    const entries = this.filteredEntries();
    if (!entries.length) return;
    const copied = await this.clipboard.copy(serializeSystemLogs(entries));
    this.notifications.show(
      copied
        ? `${entries.length} log(s) copié(s) dans le presse-papier.`
        : 'Impossible de copier les logs dans le presse-papier.',
    );
  }
  exportTxt(): void {
    this.export('txt', serializeSystemLogs(this.filteredEntries()), 'text/plain');
  }
  exportJson(): void {
    this.export('json', JSON.stringify(this.filteredEntries(), null, 2), 'application/json');
  }
  exportCsv(): void {
    this.export('csv', systemLogsCsv(this.filteredEntries()), 'text/csv');
  }
  private export(extension: string, content: string, mime: string): void {
    if (!this.filteredEntries().length) return;
    this.fileDownload.save(
      new Blob([content], { type: `${mime};charset=utf-8` }),
      `system-logs-${new Date().toISOString().replace(/[:.]/g, '-')}.${extension}`,
    );
    this.notifications.show(`${this.filteredEntries().length} événement(s) exporté(s).`);
  }
  onConsoleScroll(): void {
    const el = this.terminalContainer()?.nativeElement;
    if (el) this.autoScrollEnabled.set(el.scrollHeight - el.scrollTop - el.clientHeight <= 40);
  }
  scrollToBottom(): void {
    const el = this.terminalContainer()?.nativeElement;
    if (el) {
      el.scrollTop = el.scrollHeight;
      this.autoScrollEnabled.set(true);
    }
  }
  consoleLine(entry: SystemLogEntry): string {
    return formatSystemLog(entry);
  }
  formatTime(timestamp: string): string {
    const date = new Date(timestamp);
    return Number.isNaN(date.getTime()) ? timestamp : date.toLocaleString('fr-FR');
  }
  retryHistory(): void {
    this.reloadLogsAndStream(
      {
        nodeId: this.nodeId() || undefined,
        minLevel:
          this.selectedLevel() === 'All' ? undefined : (this.selectedLevel() as SystemLogLevel),
        category: this.selectedCategory() || undefined,
      },
      this.bufferLimit(),
    );
  }
  private reloadLogsAndStream(
    filter: { nodeId?: string; minLevel?: SystemLogLevel; category?: string },
    limit: number,
  ): void {
    this.closeStream();
    this.historySub?.unsubscribe();
    this.clearLogs();
    this.historyError.set(false);
    this.streamFailed.set(false);
    this.isLoadingHistory.set(true);
    this.historySub = this.systemLogsService
      .getLogs({ ...filter, limit: Math.min(200, limit) })
      .subscribe({
        next: (res) => {
          this.displayedEntries.set(
            mergeSystemLogs([...res.entries, ...this.displayedEntries()], limit),
          );
          this.isLoadingHistory.set(false);
          this.scheduleScroll();
        },
        error: () => {
          this.historyError.set(true);
          this.isLoadingHistory.set(false);
          this.notifications.show('Impossible de charger l’historique des journaux système.');
        },
      });
    this.streamSub = this.systemLogsService
      .createLogStream(filter, (connected) => this.isConnected.set(connected))
      .pipe(
        tap({
          error: () => {
            this.isConnected.set(false);
            this.streamFailed.set(true);
          },
        }),
        retry({ delay: 3000 }),
      )
      .subscribe({
        next: (entry) => {
          this.isConnected.set(true);
          this.streamFailed.set(false);
          if (this.isPaused()) {
            if (this.displayedEntries().some((item) => item.id === entry.id)) return;
            this.pendingBuffer = [...mergeSystemLogs([...this.pendingBuffer, entry], limit)];
            this.pendingCount.set(this.pendingBuffer.length);
          } else {
            this.displayedEntries.set(mergeSystemLogs([...this.displayedEntries(), entry], limit));
            this.scheduleScroll();
          }
        },
      });
  }
  private closeStream(): void {
    this.streamSub?.unsubscribe();
    this.streamSub = null;
    this.isConnected.set(false);
  }
}
