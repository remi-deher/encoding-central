import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import {
  ApiTokenReplicationView,
  ApiTokenResponse,
  CreateApiTokenRequest,
  CreatedApiTokenResponse,
} from '../models/api-token';

@Injectable({ providedIn: 'root' })
export class ApiTokenService {
  private readonly http = inject(HttpClient);

  list(): Observable<readonly ApiTokenResponse[]> {
    return this.http.get<readonly ApiTokenResponse[]>('/api/v1/tokens');
  }

  replication(): Observable<ApiTokenReplicationView> {
    return this.http.get<ApiTokenReplicationView>('/api/v1/tokens/replication');
  }

  create(request: CreateApiTokenRequest): Observable<CreatedApiTokenResponse> {
    return this.http.post<CreatedApiTokenResponse>('/api/v1/tokens', request);
  }

  revoke(id: string): Observable<void> {
    return this.http.delete<void>(`/api/v1/tokens/${id}`);
  }

  delete(id: string): Observable<void> {
    return this.http.delete<void>(`/api/v1/tokens/${id}?permanent=true`);
  }
}
