import { computed, inject, Injectable, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { ActivatedRoute, Router } from '@angular/router';
import { toObservable } from '@angular/core/rxjs-interop';
import { catchError, from, of, switchMap, firstValueFrom } from 'rxjs';
import { LiveUpdates } from '../../../shared/services/live-updates';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FlowValidation } from '../../../shared/services/flow-validation';
import { FlowTemplates } from '../../../shared/services/flow-templates';
import { FlowComponentPacks } from '../../../shared/services/flow-component-packs';
import { NotificationService } from '../../../shared/services/notification.service';
import {
  FlowEditorAssistant,
  FlowAssistantSession,
} from '../../../shared/services/flow-editor-assistant.service';
import {
  flowDocumentKey,
  flowPublishedVersion,
  FlowDocument,
  FlowPluginCatalog,
} from '../../../shared/models/flow-validation';
import { FlowComponentPackSummary } from '../../../shared/models/flow-component-packs';
import { createUuid } from '../../../shared/utils/uuid';
import type { RecordFormData } from '../../../shared/ui/record-form.component';
import type { FlowValidationIssue } from './flow-simulation-tray.component';
import { FlowGraphStore } from './flow-graph.store';
import { FlowComponentsStore } from './flow-components.store';

/**
 * Persistance du flow édité : chargement (flow enregistré ou modèle), sauvegarde
 * brouillon/publiée, validation backend, statut de sauvegarde, propositions IA
 * et création de pack depuis une version publiée.
 */
@Injectable()
export class FlowPersistenceStore {
  private readonly route = inject(ActivatedRoute);
  private readonly editorRouter = inject(Router);
  private readonly notifications = inject(NotificationService);
  private readonly serverValidation = inject(FlowValidation);
  private readonly templates = inject(FlowTemplates);
  private readonly packApi = inject(FlowComponentPacks);
  private readonly flowAssistant = inject(FlowEditorAssistant);
  private readonly live = inject(LiveUpdates);
  readonly graph = inject(FlowGraphStore);
  readonly components = inject(FlowComponentsStore);

  readonly flowId = this.route.snapshot.paramMap.get('flowId');
  readonly validationFlowId =
    this.flowId && /^[0-9a-f]{8}-[0-9a-f-]{27}$/i.test(this.flowId) ? this.flowId : createUuid();

  readonly savedName = signal<string | null>(null);
  readonly storedVersion = signal(0);
  readonly draftRevision = signal<number | undefined>(undefined);
  readonly saving = signal(false);
  readonly loadState = signal<'ready' | 'loading' | 'error'>('ready');
  readonly backupStatus = signal<string | null>(null);
  readonly validating = signal(false);
  readonly validationIssues = signal<readonly FlowValidationIssue[]>([]);
  readonly validationSuccess = signal<boolean | null>(null);
  readonly packCatalog = signal<readonly FlowComponentPackSummary[]>([]);

  private readonly savedGraph = signal<string | null>(null);
  private readonly savedDocumentKey = signal<string | null>(null);

  readonly hasDraft = computed(() => this.draftRevision() !== undefined);

  readonly flowTitle = computed(() => {
    if (this.savedName()) return this.savedName()!;
    const name = this.route.snapshot.queryParamMap.get('name');
    if (name) return name;
    return this.flowId ? 'Modifier un flow' : 'Nouveau flow';
  });

  readonly editorStatus = computed(() =>
    this.loadState() === 'loading'
      ? 'Chargement du flow…'
      : this.loadState() === 'error'
        ? 'Chargement impossible'
        : this.hasUnsavedChanges()
          ? 'Modifications non enregistrées'
          : this.draftRevision() !== undefined
            ? 'Brouillon enregistré'
            : 'Enregistré',
  );

  readonly packCreationForm = computed<RecordFormData>(() => ({
    title: 'Créer un pack depuis la version publiée',
    description: `La version publiée v${this.storedVersion()} sera copiée avec ses composants internes et ses ports.`,
    submitLabel: 'Créer le pack',
    initialValues: { name: `${this.flowTitle()} — Pack`, description: '' },
    fields: [
      { id: 'name', label: 'Nom du pack', type: 'text', required: true, maxLength: 128 },
      { id: 'description', label: 'Description', type: 'text', maxLength: 1000 },
    ],
  }));

  constructor() {
    toObservable(computed(() => ({ version: this.storedVersion(), draft: this.draftRevision() })))
      .pipe(
        switchMap(({ version, draft }) =>
          version > 0 || draft !== undefined
            ? this.live
                .drive(['flows'], { refreshMs: 15_000 })
                .pipe(
                  switchMap(() =>
                    (draft !== undefined
                      ? from(this.serverValidation.get(this.validationFlowId))
                      : this.serverValidation.versionStatus(this.validationFlowId, version)
                    ).pipe(catchError(() => of(null))),
                  ),
                )
            : of(null),
        ),
        takeUntilDestroyed(),
      )
      .subscribe((response) => {
        if (response && (response.draftRevision ?? undefined) === this.draftRevision())
          this.backupStatus.set(response.backupStatus);
      });
  }

  hasUnsavedChanges(): boolean {
    return this.loadState() === 'ready' && this.savedGraph() !== this.graph.graphSnapshot();
  }

  onTitleChange(newTitle: string): void {
    this.savedName.set(newTitle);
  }

  async loadPackCatalog(): Promise<void> {
    try {
      this.packCatalog.set(await firstValueFrom(this.packApi.list()));
    } catch {
      this.packCatalog.set([]);
    }
  }

  async createPackFromPublishedFlow(values: Record<string, string>): Promise<boolean> {
    if (this.storedVersion() < 1 || this.saving()) return false;
    try {
      await this.packApi.createFromFlow({
        sourceFlowId: this.validationFlowId,
        sourceVersion: this.storedVersion(),
        name: values['name'],
        description: values['description'] ?? '',
      });
      this.packCatalog.set(await firstValueFrom(this.packApi.list()));
      this.notifications.show('Pack créé depuis la version publiée du flow.');
      return true;
    } catch (error) {
      this.notifications.show(
        error instanceof HttpErrorResponse
          ? (error.error?.title ?? 'Création du pack impossible.')
          : 'Création du pack impossible.',
      );
      return false;
    }
  }

  async loadSavedFlow(id: string): Promise<void> {
    try {
      const [saved, plugins] = await Promise.all([
        this.serverValidation.get(id),
        this.serverValidation.catalog(),
      ]);
      const definition = saved.definition;
      this.savedName.set(definition.name);
      this.components.flowMediaType.set(definition.mediaType);
      const nodes = this.graph.mapDefinitionNodes(definition, plugins);
      this.graph.updateGraphCounters(definition, nodes);
      this.graph.setGraph(nodes, definition.connections);
      this.graph.componentPacks.set(definition.componentPacks ?? []);
      this.savedGraph.set(this.graph.graphSnapshot());
      this.savedDocumentKey.set(flowDocumentKey(definition));
      this.storedVersion.set(flowPublishedVersion(saved));
      this.draftRevision.set(saved.draftRevision ?? undefined);
      this.backupStatus.set(saved.backupStatus);
      this.loadState.set('ready');
    } catch {
      this.loadState.set('error');
      this.notifications.show('Impossible de charger le flow enregistré.');
    }
  }

  async loadTemplate(templateId: string, plugins: readonly FlowPluginCatalog[]): Promise<void> {
    try {
      const template = await this.templates.get(templateId);
      const name =
        this.route.snapshot.queryParamMap.get('name') ?? `${template.template.name} — Copie`;
      const definition = {
        ...template.definition,
        id: this.validationFlowId,
        version: 1,
        name,
      };
      this.components.flowMediaType.set(definition.mediaType);
      this.savedName.set(name);
      const nodes = this.graph.mapDefinitionNodes(definition, plugins);
      this.graph.updateGraphCounters(definition, nodes);
      this.graph.setGraph(nodes, definition.connections);
      this.graph.componentPacks.set(definition.componentPacks ?? []);
      this.savedGraph.set(null);
      this.savedDocumentKey.set(null);
      this.storedVersion.set(0);
      this.draftRevision.set(undefined);
      this.backupStatus.set(null);
      this.loadState.set('ready');
    } catch {
      this.loadState.set('error');
      this.notifications.show('Impossible de charger ce modèle de flow.');
    }
  }

  async currentFlowDocument(): Promise<FlowDocument | null> {
    try {
      return await this.serverValidation.document(
        this.validationFlowId,
        this.storedVersion() + 1,
        this.flowTitle(),
        this.components.flowMediaType(),
        this.graph.nodes(),
        this.graph.connections(),
        this.graph.componentPacks(),
      );
    } catch {
      return null;
    }
  }

  async saveFlow(): Promise<void> {
    if (
      this.saving() ||
      this.loadState() !== 'ready' ||
      (!this.hasUnsavedChanges() && !this.hasDraft())
    )
      return;
    this.saving.set(true);
    const nodes = this.graph.nodes(),
      connections = this.graph.connections(),
      packs = this.graph.componentPacks(),
      expected = this.storedVersion();
    const graphSnapshot = JSON.stringify([nodes, connections, packs]);
    try {
      const document = await this.serverValidation.document(
        this.validationFlowId,
        expected + 1,
        this.flowTitle(),
        this.components.flowMediaType(),
        nodes,
        connections,
        packs,
      );
      const unchanged = flowDocumentKey(document) === this.savedDocumentKey();
      if (unchanged && !this.hasDraft()) {
        this.savedGraph.set(graphSnapshot);
        this.notifications.show('Aucune modification à enregistrer.');
        return;
      }
      const result = await this.serverValidation.saveAutomatically(
        document,
        expected,
        this.draftRevision(),
        unchanged,
      );
      if ('noChanges' in result && result.noChanges) {
        this.notifications.show('Aucune modification à publier.');
        return;
      }
      const { saved, published, validation } = result;
      if (!saved) {
        this.notifications.show(
          'Le brouillon est déjà enregistré. Publication impossible : ' +
            validation.issues.map((issue) => issue.message).join(' '),
        );
        return;
      }
      this.savedGraph.set(graphSnapshot);
      this.savedDocumentKey.set(flowDocumentKey(saved.definition));
      this.storedVersion.set(flowPublishedVersion(saved));
      this.draftRevision.set(saved.draftRevision ?? undefined);
      this.backupStatus.set(saved.backupStatus);
      const changed =
        this.graph.nodes() !== nodes ||
        this.graph.connections() !== connections ||
        this.graph.componentPacks() !== packs;
      if (!changed && this.flowId !== saved.definition.id)
        void this.editorRouter.navigate(['/flows', saved.definition.id, 'edit'], {
          replaceUrl: true,
        });
      this.notifications.show(
        !published
          ? 'Brouillon enregistré. Publication impossible : ' +
              validation.issues.map((issue) => issue.message).join(' ')
          : 'Version ' +
              saved.definition.version +
              ' publiée. Sauvegarde JSON : ' +
              (saved.backupStatus === 'ready' ? 'disponible.' : 'en attente.') +
              (changed ? ' Des modifications plus récentes restent à enregistrer.' : ''),
      );
    } catch (error) {
      const message =
        error instanceof HttpErrorResponse && error.status === 409
          ? 'Conflit de version : rechargez le flow avant de réessayer.'
          : error instanceof HttpErrorResponse && error.status === 422
            ? error.error.issues.map((issue: { message: string }) => issue.message).join(' ')
            : 'Enregistrement non confirmé. Votre flow reste dans l’éditeur.';
      this.notifications.show(message);
    } finally {
      this.saving.set(false);
    }
  }

  async validateGraph(): Promise<void> {
    if (this.validating()) return;
    this.validating.set(true);
    const checkedNodes = this.graph.nodes();
    const checkedConnections = this.graph.connections();
    const checkedPacks = this.graph.componentPacks();
    try {
      const result = await this.serverValidation.validate(
        this.validationFlowId,
        this.flowTitle(),
        this.components.flowMediaType(),
        checkedNodes,
        checkedConnections,
        checkedPacks,
      );
      if (
        this.graph.nodes() !== checkedNodes ||
        this.graph.connections() !== checkedConnections ||
        this.graph.componentPacks() !== checkedPacks
      ) {
        this.notifications.show(
          'Le flow a été modifié pendant la vérification. Vérifiez sa nouvelle version.',
        );
        return;
      }
      this.validationIssues.set(result.issues ?? []);
      this.validationSuccess.set(result.isValid);
      this.notifications.show(
        result.isValid
          ? 'Le backend confirme la validité du flow.'
          : result.issues.map((issue) => issue.path + ' : ' + issue.message).join(' '),
      );
    } catch {
      this.notifications.show(
        'Validation backend indisponible. La validité du flow n’a pas été confirmée.',
      );
    } finally {
      this.validating.set(false);
    }
  }

  /** Enregistre la page comme éditrice auprès de l’assistant de flow. */
  registerAssistant(
    owner: object,
    prepare: (nodeId?: string) => Promise<FlowAssistantSession | null>,
  ): void {
    this.flowAssistant.register(owner, prepare);
  }

  unregisterAssistant(owner: object): void {
    this.flowAssistant.unregister(owner);
  }

  async prepareAiSession(nodeId?: string): Promise<FlowAssistantSession | null> {
    const currentDoc = await this.currentFlowDocument();
    if (!currentDoc) {
      this.notifications.show(
        'Impossible de préparer le contexte du flow. Réessayez après le chargement du catalogue.',
      );
      return null;
    }
    const node = this.graph.nodes().find((n) => n.id === nodeId);
    return {
      flow: currentDoc,
      components: this.components.installedComponents(),
      label: node
        ? 'Étape sélectionnée : ' + (node.name || node.component.name)
        : 'Flow complet : ' + this.flowTitle(),
      prompt: node
        ? 'Explique ou améliore l’étape ' +
          (node.name || node.component.name) +
          ' (identifiant ' +
          node.id +
          '). Conserve le reste du flow sauf nécessité explicitement expliquée.'
        : '',
      apply: async (proposal: FlowDocument) => {
        const latest = await this.currentFlowDocument();
        if (!latest || flowDocumentKey(latest) !== flowDocumentKey(currentDoc)) {
          this.notifications.show(
            'Le flow a changé depuis la proposition. Relancez l’assistant pour conserver vos modifications.',
          );
          return false;
        }
        return this.loadAiFlow(proposal, currentDoc);
      },
    };
  }

  async restoreAiProposal(): Promise<void> {
    const previous = this.graph.aiProposalRollback();
    if (!previous) return;
    try {
      const plugins = await this.serverValidation.catalog();
      this.savedName.set(previous.name);
      this.components.flowMediaType.set(previous.mediaType);
      const nodes = this.graph.mapDefinitionNodes(previous, plugins);
      this.graph.updateGraphCounters(previous, nodes);
      this.graph.setGraph(nodes, previous.connections);
      this.graph.componentPacks.set(previous.componentPacks ?? []);
      this.graph.aiProposalRollback.set(null);
      this.notifications.show('État précédent du flow restauré.');
    } catch {
      this.notifications.show('Impossible de restaurer l’état précédent du flow.');
    }
  }

  async loadAiFlow(definition: FlowDocument, rollback: FlowDocument | null): Promise<boolean> {
    const snapshot = this.graph.graphSnapshot();
    try {
      const plugins = await this.serverValidation.catalog();
      if (snapshot !== this.graph.graphSnapshot()) {
        this.notifications.show(
          'Le flow a changé pendant la préparation. La proposition n’a pas été appliquée.',
        );
        return false;
      }
      this.savedName.set(definition.name);
      this.components.flowMediaType.set(definition.mediaType);
      const nodes = this.graph.mapDefinitionNodes(definition, plugins);
      this.graph.updateGraphCounters(definition, nodes);
      this.graph.setGraph(nodes, definition.connections);
      this.graph.componentPacks.set(definition.componentPacks ?? []);
      this.graph.aiProposalRollback.set(rollback);
      this.notifications.show(
        'Proposition IA appliquée aux deux vues. Enregistrez pour la publier.',
      );
      return true;
    } catch {
      this.notifications.show("Erreur lors de l'application du flow généré.");
      return false;
    }
  }
}
