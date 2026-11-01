import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ServerInfo } from './models';

export interface ServerIdentity {
  displayName: string;
  publicBaseUrl: string;
}

@Injectable({ providedIn: 'root' })
export class ServerService {
  private readonly http = inject(HttpClient);

  get(): Promise<ServerInfo> {
    return firstValueFrom(this.http.get<ServerInfo>('/api/v1/server', { withCredentials: true }));
  }

  workers(): Promise<unknown[]> {
    return firstValueFrom(this.http.get<unknown[]>('/api/v1/workers', { withCredentials: true }));
  }

  update(identity: ServerIdentity): Promise<ServerInfo> {
    return firstValueFrom(
      this.http.put<ServerInfo>('/api/v1/server', identity, { withCredentials: true }),
    );
  }
}
