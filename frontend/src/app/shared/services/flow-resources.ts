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

/** Catalogue des ressources du central : un fichier, une ressource, une version par contenu (ADR 0037). */
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

  create(name: string, description: string, file: FlowResourceFileReference, kind = 'font') {
    return firstValueFrom(
      this.http.post<FlowResourceDetails>(this.base, {
        kind,
        name,
        description,
        files: [file],
      }),
    );
  }

  content(id: string, version: number) {
    return firstValueFrom(
      this.http.get(`${this.base}/${id}/versions/${version}/content`, { responseType: 'blob' }),
    );
  }

  addVersion(id: string, file: FlowResourceFileReference) {
    return firstValueFrom(
      this.http.post<FlowResourceVersion>(`${this.base}/${id}/versions`, { files: [file] }),
    );
  }

  delete(id: string) {
    return firstValueFrom(this.http.delete<void>(`${this.base}/${id}`));
  }
}
