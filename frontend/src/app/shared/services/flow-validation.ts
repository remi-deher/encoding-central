import { inject, Injectable } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom, map } from 'rxjs';
import {
  FlowGraphNode,
  FlowGraphConnection,
  FlowComponentConfigurationField,
} from '../models/flow-component';
import { MediaType } from '../models/media-type';
import {
  FlowPluginCatalog,
  FlowDocument,
  FlowVersionResponse,
  FlowSummary,
  FlowValidationResult,
  flowComponentReference,
  FlowComponentPackInstance,
  flowDocumentKey,
} from '../models/flow-validation';
@Injectable({ providedIn: 'root' })
export class FlowValidation {
  private readonly http = inject(HttpClient);
  versionStatus(id: string, version: number) {
    return this.http.get<FlowVersionResponse>('/api/v1/flows/' + id + '/versions/' + version);
  }

  draftBackups(id: string) {
    return firstValueFrom(
      this.http.get<
        readonly { revision: number; backupStatus: string; checkedAt: string | null }[]
      >('/api/v1/flows/' + id + '/draft-backups'),
    );
  }
  draftBackup(id: string, revision: number) {
    return firstValueFrom(
      this.http.get<FlowDocument>('/api/v1/flows/' + id + '/draft-backups/' + revision),
    );
  }
  usage(id: string) {
    return firstValueFrom(
      this.http.get<
        readonly { libraryId: string; libraryName: string; version: number; isActive: boolean }[]
      >('/api/v1/flows/' + id + '/usage'),
    );
  }
  preflight(id: string) {
    return firstValueFrom(
      this.http.get<{
        draftRevision: number | null;
        publishedVersion: number;
        graph: { valid: boolean; issues: readonly { message: string }[] };
        execution: {
          workerCompatible: boolean;
          compatibleWorkers: readonly { id: string; name: string }[];
          observedAt: string;
        };
      }>('/api/v1/flows/' + id + '/preflight'),
    );
  }
  clone(id: string, name: string) {
    return firstValueFrom(
      this.http.post<FlowVersionResponse>('/api/v1/flows/' + id + '/clone', { name }),
    );
  }
  export(id: string) {
    return firstValueFrom(
      this.http.get('/api/v1/flows/' + id + '/export', { responseType: 'blob' }),
    );
  }
  exportMany(ids: readonly string[]) {
    return firstValueFrom(this.http.post('/api/v1/flows/export', ids, { responseType: 'blob' }));
  }
  import(file: File) {
    return firstValueFrom(
      this.http.post<{ imported: number }>('/api/v1/flows/import', file, {
        headers: {
          'Content-Type': file.name.toLowerCase().endsWith('.zip')
            ? 'application/zip'
            : 'application/json',
        },
      }),
    );
  }
  versions(id: string) {
    return firstValueFrom(
      this.http.get<readonly FlowDocument[]>('/api/v1/flows/' + id + '/versions'),
    );
  }
  list() {
    return this.http.get<readonly FlowSummary[]>('/api/v1/flows');
  }
  remove(id: string, detachLibraries = false): Promise<void> {
    return firstValueFrom(
      this.http.delete<void>('/api/v1/flows/' + id, {
        params: detachLibraries ? { detachLibraries: true } : {},
      }),
    );
  }
  get(id: string) {
    return firstValueFrom(this.http.get<FlowVersionResponse>('/api/v1/flows/' + id + '/edit'));
  }
  catalog() {
    return firstValueFrom(
      this.http.get<readonly FlowPluginCatalog[]>('/api/v1/flows/components').pipe(
        map((plugins) =>
          plugins.map((plugin) => ({
            ...plugin,
            components: plugin.components.map((component) => ({
              ...component,
              reference: {
                pluginId: plugin.id,
                pluginVersion: plugin.version,
                componentId: component.id,
                componentVersion: component.version,
                configurationSchemaVersion: component.configurationSchema?.version ?? 1,
              },
              configurationSchema: component.configurationSchema
                ? {
                    ...component.configurationSchema,
                    fields: component.configurationSchema.fields.map(
                      (field) =>
                        Object.fromEntries(
                          Object.entries(field).filter(([, value]) => value !== null),
                        ) as unknown as FlowComponentConfigurationField,
                    ),
                  }
                : undefined,
            })),
          })),
        ),
      ),
    );
  }

  async document(
    id: string,
    version: number,
    name: string,
    mediaType: MediaType,
    nodes: readonly FlowGraphNode[],
    connections: readonly FlowGraphConnection[],
    componentPacks: readonly FlowComponentPackInstance[] = [],
  ): Promise<FlowDocument> {
    const plugins = await this.catalog();
    return {
      formatVersion: 1,
      id,
      version,
      name,
      mediaType,
      nodes: nodes.map((node) => ({
        id: node.id,
        name: node.name ?? null,
        component: node.reference ?? flowComponentReference(node.component, plugins),
        configuration: Object.fromEntries(
          Object.entries(node.configuration).filter(
            ([key]) => node.component.kind !== 'input' || key !== 'inputComponentId',
          ),
        ),
      })),
      connections,
      ...(componentPacks.length ? { componentPacks } : {}),
    };
  }

  generateGuided(request: {
    id: string;
    name: string;
    codec: string;
    encoder: string;
    quality: string;
    audio: string;
    retryWithCpu: boolean;
  }) {
    return firstValueFrom(this.http.post<FlowDocument>('/api/v1/flows/guided', request));
  }

  saveDraft(definition: FlowDocument, expectedRevision: number) {
    return firstValueFrom(
      this.http.put<FlowVersionResponse>('/api/v1/flows/' + definition.id + '/draft', {
        definition,
        expectedRevision,
      }),
    );
  }
  save(definition: FlowDocument, expectedVersion: number, expectedDraftRevision?: number) {
    return firstValueFrom(
      this.http.put<FlowVersionResponse>('/api/v1/flows/' + definition.id, {
        definition,
        expectedVersion,
        expectedDraftRevision,
      }),
    );
  }

  async saveAutomatically(
    definition: FlowDocument,
    expectedVersion: number,
    expectedDraftRevision: number | undefined,
    unchanged: boolean,
  ) {
    if (expectedVersion > 0 && expectedDraftRevision !== undefined) {
      const latest = await firstValueFrom(
        this.http.get<FlowVersionResponse>(`/api/v1/flows/${definition.id}/edit`),
      );
      const latestPublishedVersion = latest.publishedVersion ?? expectedVersion;
      const published = await firstValueFrom(
        this.http.get<FlowVersionResponse>(
          `/api/v1/flows/${definition.id}/versions/${latestPublishedVersion}`,
        ),
      );
      if (flowDocumentKey(published.definition) === flowDocumentKey(definition)) {
        return {
          saved: null,
          published: false,
          noChanges: true,
          validation: { isValid: true, issues: [] },
        };
      }
    }
    let validation = await this.validateDocument(definition);
    if (validation.isValid) {
      try {
        return {
          saved: await this.save(definition, expectedVersion, expectedDraftRevision),
          published: true,
          validation,
        };
      } catch (error) {
        if (
          !(error instanceof HttpErrorResponse) ||
          error.status !== 422 ||
          !Array.isArray(error.error?.issues)
        )
          throw error;
        validation = error.error as FlowValidationResult;
      }
    }
    return {
      saved: unchanged ? null : await this.saveDraft(definition, expectedDraftRevision ?? 0),
      published: false,
      validation,
    };
  }

  async validate(
    id: string,
    name: string,
    mediaType: MediaType,
    nodes: readonly FlowGraphNode[],
    connections: readonly FlowGraphConnection[],
    componentPacks: readonly FlowComponentPackInstance[] = [],
  ): Promise<FlowValidationResult> {
    const document = await this.document(
      id,
      1,
      name,
      mediaType,
      nodes,
      connections,
      componentPacks,
    );
    return this.validateDocument(document);
  }

  async validateDocument(document: FlowDocument): Promise<FlowValidationResult> {
    try {
      return await firstValueFrom(
        this.http.post<FlowValidationResult>('/api/v1/flows/validate', document),
      );
    } catch (error) {
      if (
        error instanceof HttpErrorResponse &&
        error.status === 422 &&
        Array.isArray(error.error?.issues)
      )
        return error.error as FlowValidationResult;
      throw error;
    }
  }
}
