import { inject, Injectable, signal } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { FlowDocument } from '../models/flow-validation';
import { FlowComponentConfiguration } from '../models/flow-component';
import { createUuid } from '../utils/uuid';
import {
  SimulationAssumption,
  SimulationFile,
  SimulationMetadata,
  SimulationResult,
  simulationFile,
  simulationObject,
  simulationProfile,
} from '../models/flow-simulation';

@Injectable()
export class FlowSimulation {
  private readonly http = inject(HttpClient);
  private definition?: FlowDocument;
  private assumptions: Record<string, SimulationAssumption> = {};
  private file: SimulationFile = {
    name: 'film.mkv',
    metadata: {
      format: { size: 1000000000, duration: 5400 },
      streams: [
        { codec_type: 'video', codec_name: 'h264', width: 1920, height: 1080 },
        { codec_type: 'audio', codec_name: 'aac', channels: 2, tags: { language: 'fra' } },
      ],
    },
    variables: {},
    sourceNodeId: createUuid(),
    workerNodeId: createUuid(),
  };
  readonly profile = signal<FlowComponentConfiguration>(simulationProfile(this.file));
  readonly result = signal<SimulationResult | null>(null);
  readonly busy = signal(false);
  readonly error = signal('');

  private generation = 0;
  invalidate(): void {
    this.generation++;
    this.result.set(null);
    this.error.set('');
  }

  prepare(definition: FlowDocument): void {
    this.invalidate();
    this.definition = definition;
    this.assumptions = {};
    this.result.set(null);
    this.error.set('');
  }
  async run(values: FlowComponentConfiguration): Promise<void> {
    if (this.busy()) return;
    try {
      this.file = simulationFile(values);
      this.profile.set(values);
      this.assumptions = {};
      this.result.set(null);
      await this.simulate();
    } catch (error) {
      this.showError(error);
    }
  }
  async continue(values: FlowComponentConfiguration): Promise<void> {
    const id = this.result()?.waitingNodeId;
    if (!id || this.busy()) return;
    try {
      const metadataText = String(values['metadata'] ?? '').trim();
      const metadata = metadataText
        ? (simulationObject(metadataText) as unknown as SimulationMetadata)
        : undefined;
      this.assumptions[id] = {
        outputId: String(values['outputId']),
        modified: values['modified'] === true,
        ...(metadata ? { metadata } : {}),
        variables: simulationObject(String(values['variables'] ?? '{}')),
        ...(values['extension'] ? { extension: String(values['extension']) } : {}),
        ...(values['workerNodeId'] ? { workerNodeId: String(values['workerNodeId']) } : {}),
      };
      await this.simulate();
    } catch (error) {
      this.showError(error);
    }
  }
  async import(file: File): Promise<void> {
    if (this.busy()) return;
    if (file.size > 512 * 1024 * 1024) {
      this.error.set('Le fichier dépasse 512 Mio.');
      return;
    }
    this.busy.set(true);
    this.error.set('');
    try {
      const response = await firstValueFrom(
        this.http.post<{ metadata: SimulationMetadata }>('/api/v1/flows/inspect-metadata', file, {
          headers: { 'Content-Type': 'application/octet-stream' },
        }),
      );
      this.file = { ...this.file, name: file.name, metadata: response.metadata };
      this.profile.set(simulationProfile(this.file));
      this.assumptions = {};
      this.result.set(null);
    } catch (error) {
      this.showError(error);
    } finally {
      this.busy.set(false);
    }
  }
  private async simulate(): Promise<void> {
    if (!this.definition) return;
    const generation = this.generation;
    this.busy.set(true);
    this.error.set('');
    try {
      const result = await firstValueFrom(
        this.http.post<SimulationResult>('/api/v1/flows/simulate', {
          definition: this.definition,
          file: this.file,
          assumptions: this.assumptions,
        }),
      );
      if (generation === this.generation) this.result.set(result);
    } catch (error) {
      if (generation !== this.generation) return;
      if (error instanceof HttpErrorResponse && error.status === 422)
        this.result.set(error.error as SimulationResult);
      else this.showError(error);
    } finally {
      this.busy.set(false);
    }
  }
  private showError(error: unknown): void {
    this.error.set(
      error instanceof HttpErrorResponse
        ? (error.error?.title ?? 'Test indisponible.')
        : error instanceof Error
          ? error.message
          : 'Scénario invalide.',
    );
  }
}
