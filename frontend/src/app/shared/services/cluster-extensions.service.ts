import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import {
  ClusterTool,
  RejectedPlugin,
  StorePackage,
  StoreRepository,
} from '../models/cluster-extensions';

@Injectable({ providedIn: 'root' })
export class ClusterExtensionsService {
  private readonly http = inject(HttpClient);

  listRejectedPlugins(): Promise<readonly RejectedPlugin[]> {
    return firstValueFrom(this.http.get<readonly RejectedPlugin[]>('/api/v1/plugins/rejected'));
  }

  listTools(): Promise<readonly ClusterTool[]> {
    return firstValueFrom(this.http.get<readonly ClusterTool[]>('/api/v1/tools'));
  }

  setPluginEnabled(id: string, enabled: boolean): Promise<{ pluginId: string; enabled: boolean }> {
    return firstValueFrom(
      this.http.put<{ pluginId: string; enabled: boolean }>(
        `/api/v1/plugins/${encodeURIComponent(id)}/enabled`,
        { enabled },
      ),
    );
  }

  setToolEnabled(id: string, enabled: boolean): Promise<{ toolId: string; enabled: boolean }> {
    return firstValueFrom(
      this.http.put<{ toolId: string; enabled: boolean }>(
        `/api/v1/tools/${encodeURIComponent(id)}/enabled`,
        { enabled },
      ),
    );
  }

  testToolConformance(
    toolId: string,
  ): Promise<{ toolId: string; passed: boolean; message: string }> {
    return firstValueFrom(
      this.http.post<{ toolId: string; passed: boolean; message: string }>(
        `/api/v1/tools/${toolId}/conformance-test`,
        {},
      ),
    );
  }

  listRepositories(): Promise<readonly StoreRepository[]> {
    return firstValueFrom(this.http.get<readonly StoreRepository[]>('/api/v1/store/repositories'));
  }

  listPackages(): Promise<readonly StorePackage[]> {
    return firstValueFrom(this.http.get<readonly StorePackage[]>('/api/v1/store/packages'));
  }

  addRepository(url: string): Promise<StoreRepository> {
    return firstValueFrom(this.http.post<StoreRepository>('/api/v1/store/repositories', { url }));
  }

  syncRepository(id: string): Promise<StoreRepository> {
    return firstValueFrom(
      this.http.post<StoreRepository>(
        `/api/v1/store/repositories/${encodeURIComponent(id)}/sync`,
        {},
      ),
    );
  }

  setRepositoryEnabled(id: string, enabled: boolean): Promise<StoreRepository> {
    return firstValueFrom(
      this.http.put<StoreRepository>(`/api/v1/store/repositories/${encodeURIComponent(id)}`, {
        enabled,
      }),
    );
  }

  removeRepository(id: string): Promise<void> {
    return firstValueFrom(
      this.http.delete<void>(`/api/v1/store/repositories/${encodeURIComponent(id)}`),
    );
  }

  installPackage(repositoryId: string, id: string, version?: string): Promise<unknown> {
    return firstValueFrom(
      this.http.post('/api/v1/store/packages/install', { repositoryId, id, version }),
    );
  }

  installToolPackage(repositoryId: string, id: string, version?: string): Promise<unknown> {
    return firstValueFrom(
      this.http.post('/api/v1/store/tools/install', { repositoryId, id, version }),
    );
  }

  importArchive(archive: File): Promise<unknown> {
    return firstValueFrom(
      this.http.post('/api/v1/store/packages/import', archive, {
        headers: { 'Content-Type': 'application/zip' },
      }),
    );
  }

  removePackage(id: string): Promise<void> {
    return firstValueFrom(
      this.http.delete<void>(`/api/v1/store/packages/${encodeURIComponent(id)}`),
    );
  }

  removeToolPackage(id: string, platform: string): Promise<void> {
    return firstValueFrom(
      this.http.delete<void>(`/api/v1/store/tools/${encodeURIComponent(id)}/${encodeURIComponent(platform)}`),
    );
  }
}
