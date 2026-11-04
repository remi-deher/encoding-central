import { HttpClient } from '@angular/common/http';
import { computed, inject, Injectable, signal } from '@angular/core';
import { catchError, EMPTY, finalize } from 'rxjs';
import { CentralApi } from '../../shared/services/central-api';
import { NotificationService } from '../../shared/services/notification.service';
import { LocalWorkerController } from '../../shared/services/local-worker-controller';
import { EnrollmentWindow, FirstLinkAcceptance } from '../../shared/models/auth';
import { WorkerResponse, NODE_ROLE_LABELS } from '../../shared/models/central';
import { formatWorkerLabel, WorkerRole } from '../../shared/models/central';
import { DataTableColumn, DataTableLayoutOption, DataTableRow } from '../../shared/ui/data-table.component';
import type { GuidedStep } from '../../shared/ui/guided-setup.component';
import { WorkerDataStore } from './worker-data.store';
import { WorkerDetailStore } from './worker-detail.store';
import {
  buildEnrollmentSteps,
  capabilitiesLabel,
  ENROLLMENT_FORM_DATA,
  formatExpiry,
  formatExpiryTime,
  PendingWorkerEnrollmentRecord,
  PendingWorkerEnrollmentResponse,
} from './worker-catalog.utils';
import { WorkerMembersStore } from './worker-members.store';

/**
 * Enrôlement des workers : demandes en attente, fenêtre d’enrôlement direct,
 * première liaison et approbation par clé de jumelage.
 */
@Injectable()
export class WorkerEnrollmentStore {
  private readonly http = inject(HttpClient);
  private readonly clusterApi = inject(CentralApi);
  private readonly notifications = inject(NotificationService);
  readonly data = inject(WorkerDataStore);
  readonly members = inject(WorkerMembersStore);
  readonly detail = inject(WorkerDetailStore);
  readonly local = inject(LocalWorkerController);

  readonly enrollmentWindow = signal<EnrollmentWindow>({
    isOpen: false,
    openUntil: null,
  });
  readonly firstLink = signal<FirstLinkAcceptance>({ isActive: false, expiresAt: null });
  readonly enrollmentView = signal<'requests' | 'key' | 'review'>('requests');
  readonly selectedEnrollment = signal<PendingWorkerEnrollmentResponse | null>(null);
  readonly enrollmentStep = signal(0);
  readonly enrollmentCodeConfirmed = signal(false);
  readonly enrollmentApprovalBusy = signal(false);

  readonly createFormData = ENROLLMENT_FORM_DATA;

  readonly enrollmentColumns: readonly DataTableColumn[] = [
    { key: 'name', label: 'Worker détecté', sortable: true },
    { key: 'verificationCode', label: 'Code à comparer', sortable: false },
    { key: 'rolesLabel', label: 'Rôle annoncé', sortable: true },
    { key: 'capabilitiesLabel', label: 'Capacités', sortable: true },
    { key: 'expiresAtLabel', label: 'Expiration', sortable: true },
  ];
  readonly enrollmentLayoutOptions: readonly DataTableLayoutOption[] = [
    { value: 'resources', label: 'Cartes' },
    { value: 'table', label: 'Tableau' },
  ];

  readonly enrollmentSteps = computed<readonly GuidedStep[]>(() => {
    const request = this.selectedEnrollment();
    if (!request) return [];
    return buildEnrollmentSteps(request, {
      clusterAddress: this.members.clusterAddress(),
      clusterName: this.members.clusterName(),
    });
  });

  readonly enrollmentRows = computed<readonly PendingWorkerEnrollmentRecord[]>(() =>
    this.data.pendingEnrollments().map((request) => ({
      id: request.id,
      name: formatWorkerLabel(request.name, request.displayName),
      verificationCode: request.verificationCode ?? '—',
      rolesLabel: request.roles.map((role: WorkerRole) => NODE_ROLE_LABELS[role]).join(' · '),
      capabilitiesLabel: capabilitiesLabel(request.capabilities),
      expiresAtLabel: formatExpiry(request.expiresAt),
    })),
  );

  addNode(): void {
    this.data.actionType.set('enrollment');
    this.enrollmentView.set('requests');
    this.selectedEnrollment.set(null);
    this.enrollmentStep.set(0);
    this.enrollmentCodeConfirmed.set(false);
    this.data.actionOpen.set(true);
    this.refreshEnrollmentWindow();
  }

  refreshEnrollmentWindow(): void {
    this.clusterApi
      .enrollmentWindow()
      .pipe(catchError(() => EMPTY))
      .subscribe((window) => this.enrollmentWindow.set(window));
    this.clusterApi
      .firstLinkAcceptance()
      .pipe(catchError(() => EMPTY))
      .subscribe((firstLink) => this.firstLink.set(firstLink));
  }

  enableFirstLink(): void {
    this.clusterApi
      .enableFirstLinkAcceptance(10)
      .pipe(
        catchError(() => {
          this.notifications.show('Le mode « première liaison » n’a pas pu être activé.');
          return EMPTY;
        }),
      )
      .subscribe((firstLink) => {
        this.firstLink.set(firstLink);
        this.notifications.show(
          'La prochaine demande de rattachement sera acceptée sans vérification (10 minutes).',
        );
      });
  }

  disableFirstLink(): void {
    this.clusterApi
      .disableFirstLinkAcceptance()
      .pipe(
        catchError(() => {
          this.notifications.show('Le mode « première liaison » n’a pas pu être désactivé.');
          return EMPTY;
        }),
      )
      .subscribe(() => {
        this.firstLink.set({ isActive: false, expiresAt: null });
        this.notifications.show('Le mode « première liaison » est désactivé.');
      });
  }

  openEnrollmentWindow(): void {
    this.clusterApi
      .openEnrollmentWindow(10)
      .pipe(
        catchError(() => {
          this.notifications.show('La fenêtre d’enrôlement n’a pas pu être ouverte.');
          return EMPTY;
        }),
      )
      .subscribe((window) => {
        this.enrollmentWindow.set(window);
        this.notifications.show('L’enrôlement direct est ouvert pendant 10 minutes.');
      });
  }

  closeEnrollmentWindow(): void {
    this.clusterApi
      .closeEnrollmentWindow()
      .pipe(
        catchError(() => {
          this.notifications.show('La fenêtre d’enrôlement n’a pas pu être fermée.');
          return EMPTY;
        }),
      )
      .subscribe(() => {
        this.enrollmentWindow.set({ isOpen: false, openUntil: null });
        this.notifications.show('L’enrôlement direct est fermé.');
      });
  }

  closeCreateNode(): void {
    if (this.local.flow()) {
      this.local.closeFlow();
      return;
    }
    this.data.actionOpen.set(false);
    this.selectedEnrollment.set(null);
    this.enrollmentStep.set(0);
    this.enrollmentCodeConfirmed.set(false);
    this.detail.selectedNodeLibrary.set(null);
  }

  showPairingKey(): void {
    this.selectedEnrollment.set(null);
    this.enrollmentView.set('key');
  }

  showPendingRequests(): void {
    this.enrollmentView.set('requests');
    this.selectedEnrollment.set(null);
    this.enrollmentStep.set(0);
    this.enrollmentCodeConfirmed.set(false);
  }

  onEnrollmentOpen(row: DataTableRow): void {
    const request = this.data.pendingEnrollments().find((item) => item.id === row['id']) ?? null;
    this.selectedEnrollment.set(request);
    if (request) {
      this.enrollmentView.set('review');
      this.enrollmentStep.set(0);
      this.enrollmentCodeConfirmed.set(false);
      this.data.actionType.set('enrollment');
      this.data.actionOpen.set(true);
    }
  }

  onEnrollmentSetupAction(id: string): void {
    if (id === 'cancel') {
      this.closeCreateNode();
    } else if (id === 'back') {
      this.enrollmentStep.update((index) => Math.max(0, index - 1));
    } else if (id === 'continue') {
      this.enrollmentStep.update((index) => Math.min(2, index + 1));
    } else if (id === 'approve') {
      this.approveEnrollment();
    }
  }

  approveEnrollment(): void {
    const request = this.selectedEnrollment();
    if (
      !request ||
      !request.verificationCode ||
      this.enrollmentApprovalBusy() ||
      !this.enrollmentCodeConfirmed()
    )
      return;
    this.enrollmentApprovalBusy.set(true);
    this.http
      .post<WorkerResponse>(`/api/v1/workers/enrollment-requests/${request.id}/approve`, {})
      .pipe(
        finalize(() => this.enrollmentApprovalBusy.set(false)),
        catchError((error: { status?: number }) => {
          this.notifications.show(
            error.status === 404
              ? 'Cette demande a expiré ou a déjà été traitée. Actualisez la liste.'
              : 'Le worker n’a pas pu être accepté. Vérifiez que le serveur est disponible.',
          );
          return EMPTY;
        }),
      )
      .subscribe((node) => {
        this.data.nodes.update((current) => [
          ...current.filter((existing) => existing.id !== node.id),
          node,
        ]);
        this.data.pendingEnrollments.update((current) =>
          current.filter((item) => item.id !== node.id),
        );
        this.selectedEnrollment.set(null);
        this.enrollmentView.set('requests');
        this.data.loadState.set('ready');
        this.notifications.show(`Le worker « ${node.name} » a rejoint le central.`);
      });
  }

  acceptNode(value: Record<string, string>): void {
    const pairingKey = value['pairingKey']?.trim().toUpperCase();
    if (!pairingKey) return;
    this.data.actionOpen.set(false);
    this.http
      .post<WorkerResponse>('/api/v1/workers/enrollment-requests/approve', { pairingKey })
      .pipe(
        catchError((error: { status?: number }) => {
          this.notifications.show(
            error.status === 404
              ? 'Cette clé est invalide, expirée ou déjà utilisée. Générez-en une nouvelle depuis le worker.'
              : 'Le worker n’a pas pu être accepté. Vérifiez que le serveur est disponible.',
          );
          return EMPTY;
        }),
      )
      .subscribe((node) => {
        this.data.nodes.update((current) => [
          ...current.filter((existing) => existing.id !== node.id),
          node,
        ]);
        this.data.pendingEnrollments.update((current) =>
          current.filter((item) => item.id !== node.id),
        );
        this.data.loadState.set('ready');
        this.notifications.show(`Le worker « ${node.name} » a rejoint le central.`);
      });
  }

  formatExpiryTimeLabel(value: string): string {
    return formatExpiryTime(value);
  }
}
