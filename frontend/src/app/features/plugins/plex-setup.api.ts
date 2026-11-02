import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { PluginConfigurationResponse } from '../../shared/models/flow-validation';

/** Assistant de configuration Plex (ADR 0008). Aucun jeton Plex ne transite par le navigateur. */
export interface PlexPin {
  readonly pinId: number;
  readonly code: string;
  readonly clientIdentifier: string;
  readonly authUrl: string;
  readonly expiresAt: string;
}

export interface PlexServerConnection {
  readonly uri: string;
  readonly local: boolean;
  readonly relay: boolean;
}

export interface PlexServer {
  readonly id: string;
  readonly name: string;
  readonly owned: boolean;
  readonly connections: readonly PlexServerConnection[];
}

export interface PlexPinCheck {
  readonly status: 'pending' | 'authorized' | 'expired';
  readonly session: string | null;
  readonly servers: readonly PlexServer[] | null;
}

export interface PlexSection {
  readonly key: string;
  readonly title: string;
  readonly kind: string;
  readonly locations: readonly string[];
}

export interface PlexMappingSuggestion {
  readonly libraryId: string;
  readonly remoteRoot: string;
  readonly remoteKey: string;
  readonly remoteTitle: string;
  readonly reason: string;
}

export interface PlexEncodingLibrary {
  readonly id: string;
  readonly name: string;
  readonly localPaths: readonly string[];
  readonly currentRemoteRoot: string | null;
  readonly suggestion: PlexMappingSuggestion | null;
}

export interface PlexLibraries {
  readonly sections: readonly PlexSection[];
  readonly libraries: readonly PlexEncodingLibrary[];
}

@Injectable({ providedIn: 'root' })
export class PlexSetupApi {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/v1/plugins/plex/setup';

  createPin() {
    return firstValueFrom(this.http.post<PlexPin>(`${this.base}/pin`, {}));
  }

  checkPin(pinId: number, clientIdentifier: string) {
    return firstValueFrom(
      this.http.post<PlexPinCheck>(`${this.base}/pin/check`, { pinId, clientIdentifier }),
    );
  }

  selectServer(session: string, serverId: string, uri: string) {
    return firstValueFrom(
      this.http.post<PluginConfigurationResponse>(`${this.base}/server`, {
        session,
        serverId,
        uri,
      }),
    );
  }

  libraries() {
    return firstValueFrom(
      this.http.post<PlexLibraries>(`${this.base}/libraries`, { values: null }),
    );
  }

  saveMappings(mappings: readonly { libraryId: string; remoteRoot: string }[]) {
    return firstValueFrom(
      this.http.put<PluginConfigurationResponse>(`${this.base}/mappings`, { mappings }),
    );
  }
}
