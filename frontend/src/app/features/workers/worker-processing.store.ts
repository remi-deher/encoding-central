import { computed, effect, inject, Injectable, signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import {
  catchError,
  exhaustMap,
  firstValueFrom,
  of,
  startWith,
  switchMap,
} from 'rxjs';
import { LiveUpdates } from '../../shared/services/live-updates';
import { CentralApi } from '../../shared/services/central-api';
import { NotificationService } from '../../shared/services/notification.service';
import { WorkerProcessingSettings } from '../../shared/models/central';
import { WorkerSchedule } from '../../shared/models/worker-schedule';
import type { DetailCard } from '../../shared/ui/detail-card-grid.component';
import type { RecordFormData } from '../../shared/ui/record-form.component';
import { WorkerDataStore } from './worker-data.store';
import { processingLimitForm } from './worker-catalog.utils';

/**
 * Onglet « Traitement » de la fiche worker : limite globale et calendrier,
 * sondés tant que la fiche est ouverte.
 */
@Injectable()
export class WorkerProcessingStore {
  private readonly clusterApi = inject(CentralApi);
  private readonly live = inject(LiveUpdates);
  private readonly notifications = inject(NotificationService);
  readonly data = inject(WorkerDataStore);

  private readonly rawNode = computed(
    () => this.data.nodes().find((node) => node.id === this.data.selectedNode()?.id) ?? null,
  );

  readonly nodeProcessingState = toSignal(
    toObservable(computed(() => this.data.selectedNode()?.id ?? null)).pipe(
      switchMap((id) =>
        id
          ? this.live.drive(['executions', 'workers']).pipe(
              exhaustMap(() => this.clusterApi.nodeProcessing(id).pipe(catchError(() => of(null)))),
              startWith(null),
            )
          : of(null),
      ),
    ),
    { initialValue: null },
  );

  readonly processingForm = signal<RecordFormData | null>(null);
  readonly processingBusy = signal(false);
  readonly scheduleBusy = signal(false);
  private processingSettings: WorkerProcessingSettings | null = null;
  private processingFormNodeId: string | null = null;

  readonly processingCards = computed<readonly DetailCard[]>(() => {
    const node = this.rawNode();
    if (!node) return [];
    const settings = this.nodeProcessingState();
    const maximum = settings?.maxConcurrentTasks ?? null;
    const effectiveMaximum = settings?.effectiveMaxConcurrentTasks ?? node.maxConcurrentTasks;
    const schedule = settings?.schedule;
    const allowedPercent = schedule?.enabled
      ? Math.round(((schedule.slots.split('1').length - 1) * 100) / schedule.slots.length)
      : null;
    return [
      {
        id: 'node-processing-limit',
        title: 'Limite globale',
        subtitle: 'Traitements simultanés, toutes bibliothèques',
        icon: 'activity',
        status: {
          label: maximum == null ? `${effectiveMaximum} · annoncée` : `${maximum} · définie`,
          tone: maximum == null ? 'neutral' : 'accent',
        },
        tags: [],
        sections: [],
      },
      {
        id: 'node-processing-calendar',
        title: 'Calendrier',
        subtitle: 'Plages autorisées de démarrage',
        icon: 'system',
        status: {
          label: !schedule ? 'Chargement' : schedule.enabled ? 'Actif' : 'Désactivé',
          tone: schedule?.enabled ? 'success' : 'neutral',
        },
        tags: allowedPercent == null ? [] : [{ label: `${allowedPercent} % autorisé` }],
        sections: [],
      },
    ];
  });

  constructor() {
    effect(() => {
      const node = this.rawNode();
      const settings = this.nodeProcessingState();
      if (!node || !settings) return;
      this.processingSettings = settings;
      if (this.processingFormNodeId === node.id) return;
      this.processingFormNodeId = node.id;
      this.processingForm.set(processingLimitForm(node, settings));
    });
  }

  /** Réinitialise l’état du tiroir de fiche à la fermeture. */
  resetOnDetailClosed(): void {
    this.processingFormNodeId = null;
    this.processingForm.set(null);
  }

  async saveNodeSchedule(schedule: WorkerSchedule): Promise<void> {
    const node = this.rawNode();
    const settings = this.processingSettings;
    if (!node || !settings || this.scheduleBusy()) return;
    this.scheduleBusy.set(true);
    try {
      await firstValueFrom(
        this.clusterApi.saveNodeProcessing(
          node.id,
          settings.maxConcurrentTasks,
          settings.revision,
          schedule,
        ),
      );
      this.processingSettings = {
        ...settings,
        schedule,
        revision: settings.revision + 1,
      };
      this.notifications.show('Calendrier du worker enregistré.');
    } catch {
      this.notifications.show('Calendrier invalide ou configuration modifiée ailleurs.');
    } finally {
      this.scheduleBusy.set(false);
    }
  }

  async saveNodeProcessing(values: Record<string, string>): Promise<void> {
    const node = this.rawNode();
    const settings = this.processingSettings;
    if (!node || !settings || this.processingBusy()) return;
    this.processingBusy.set(true);
    try {
      await firstValueFrom(
        this.clusterApi.saveNodeProcessing(
          node.id,
          values['maximum'] ? Number(values['maximum']) : null,
          settings.revision,
          settings.schedule,
        ),
      );
      const maximum = values['maximum'] ? Number(values['maximum']) : null;
      const updated: WorkerProcessingSettings = {
        ...settings,
        maxConcurrentTasks: maximum,
        effectiveMaxConcurrentTasks: maximum ?? node.maxConcurrentTasks,
        revision: settings.revision + 1,
      };
      this.processingSettings = updated;
      this.processingForm.set(processingLimitForm(node, updated));
      this.notifications.show('Limite globale du worker enregistrée.');
    } catch {
      this.notifications.show('Limite invalide ou configuration modifiée ailleurs.');
    } finally {
      this.processingBusy.set(false);
    }
  }
}
