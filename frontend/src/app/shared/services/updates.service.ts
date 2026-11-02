import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';

export interface UpdateStatus {
  readonly installedVersion: string;
  readonly latestVersion: string | null;
  readonly updateAvailable: boolean | null;
  readonly status: 'development' | 'unconfigured' | 'unavailable' | 'available' | 'current';
  readonly checkedAt: string | null;
  readonly releases: readonly {
    readonly version: string;
    readonly publishedAt: string;
    readonly notes: string;
  }[];
}

@Injectable({ providedIn: 'root' })
export class UpdatesService {
  private readonly http = inject(HttpClient);

  getUpdates() {
    return this.http.get<UpdateStatus>('/api/v1/system/updates');
  }
}
