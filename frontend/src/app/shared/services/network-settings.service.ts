import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

export interface NetworkSettings {
  readonly trustProxy: boolean;
  readonly proxies: readonly string[];
  readonly publicUrl?: string | null;
}

export interface NetworkDetection {
  readonly proxyAddress: string | null;
  readonly forwardedHeadersSeen: boolean;
  readonly privateAddress: boolean;
  readonly alreadyTrusted: boolean;
  readonly forwardedFor: string;
  readonly forwardedProto: string;
}

@Injectable({ providedIn: 'root' })
export class NetworkSettingsService {
  private readonly http = inject(HttpClient);

  get(): Observable<NetworkSettings> {
    return this.http.get<NetworkSettings>('/api/v1/network/settings');
  }

  save(settings: NetworkSettings): Observable<NetworkSettings> {
    return this.http.put<NetworkSettings>('/api/v1/network/settings', settings);
  }

  detect(): Observable<NetworkDetection> {
    return this.http.post<NetworkDetection>('/api/v1/network/detect-proxy', {});
  }
}
