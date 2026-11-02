import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import {
  FlowResourceDetails,
  FlowResourceFile,
  FlowResourceFileReference,
  FlowResourceSummary,
  FlowResourceVersion,
} from '../models/flow-resources';

/** Catalogue des ressources du cluster (polices, ADR 0006) : un fichier est d'abord envoyé, puis référencé par son empreinte. */
@Injectable({ providedIn: 'root' })
export class FlowResources {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/v1/resources';

  list() {
    return firstValueFrom(this.http.get<readonly FlowResourceSummary[]>(this.base));
  }

  get(id: string) {
    return firstValueFrom(this.http.get<FlowResourceDetails>(`${this.base}/${id}`));
  }

  /** Envoie un fichier dans le magasin du serveur (corps brut) et renvoie son nom contrôlé et son empreinte. */
  uploadFile(file: File, kind = 'font') {
    return firstValueFrom(
      this.http.post<FlowResourceFile>(
        `${this.base}/blobs?kind=${encodeURIComponent(kind)}&name=${encodeURIComponent(file.name)}`,
        file,
        { headers: { 'Content-Type': 'application/octet-stream' } },
      ),
    );
  }

  create(name: string, description: string, files: readonly FlowResourceFileReference[]) {
    return firstValueFrom(
      this.http.post<FlowResourceDetails>(this.base, { kind: 'font', name, description, files }),
    );
  }

  addVersion(id: string, files: readonly FlowResourceFileReference[]) {
    return firstValueFrom(
      this.http.post<FlowResourceVersion>(`${this.base}/${id}/versions`, { files }),
    );
  }

  delete(id: string) {
    return firstValueFrom(this.http.delete<void>(`${this.base}/${id}`));
  }
}
