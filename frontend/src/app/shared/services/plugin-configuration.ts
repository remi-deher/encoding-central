import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { FlowComponentConfiguration } from '../models/flow-component';
import { PluginConfigurationResponse } from '../models/flow-validation';

@Injectable({ providedIn: 'root' })
export class PluginConfigurationApi {
  private readonly http = inject(HttpClient);

  getGlobal(pluginId: string) {
    return firstValueFrom(
      this.http.get<PluginConfigurationResponse>(`/api/v1/plugins/${pluginId}/configuration`),
    );
  }

  saveGlobal(
    pluginId: string,
    revision: number,
    values: FlowComponentConfiguration,
    clearSecrets: readonly string[] = [],
  ) {
    return firstValueFrom(
      this.http.put<PluginConfigurationResponse>(`/api/v1/plugins/${pluginId}/configuration`, {
        expectedRevision: revision,
        values,
        clearSecrets,
      }),
    );
  }

  testConnection(pluginId: string, values: FlowComponentConfiguration) {
    return firstValueFrom(
      this.http.post<{ success: boolean; message: string }>(
        `/api/v1/plugins/${pluginId}/connection-test`,
        { values },
      ),
    );
  }
}
