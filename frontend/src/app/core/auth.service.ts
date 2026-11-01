import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ServerInfo, SessionUser, SetupStatus } from './models';

export interface SetupInput {
  username: string;
  password: string;
  displayName: string;
  publicBaseUrl: string;
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  readonly user = signal<SessionUser | null>(null);
  readonly setupRequired = signal(false);
  private ready = false;
  private flight: Promise<void> | null = null;

  ensureKnown(): Promise<void> {
    if (this.ready) return Promise.resolve();
    return (this.flight ??= this.load().then(() => {
      this.ready = true;
    }));
  }

  async setup(input: SetupInput): Promise<void> {
    const response = await firstValueFrom(
      this.http.post<{ session: SessionUser }>(
        '/api/v1/setup',
        input,
        { withCredentials: true },
      ),
    );
    this.user.set(response.session);
    this.setupRequired.set(false);
    this.ready = true;
  }

  async login(username: string, password: string): Promise<void> {
    const session = await firstValueFrom(
      this.http.post<SessionUser>(
        '/api/v1/session',
        { username, password },
        { withCredentials: true },
      ),
    );
    this.user.set(session);
    this.ready = true;
  }

  async logout(): Promise<void> {
    await firstValueFrom(this.http.delete('/api/v1/session', { withCredentials: true }));
    this.user.set(null);
  }

  clear(): void {
    this.user.set(null);
  }

  private async load(): Promise<void> {
    const setup = await firstValueFrom(
      this.http.get<SetupStatus>('/api/v1/setup', { withCredentials: true }),
    );
    this.setupRequired.set(setup.setupRequired);
    if (setup.setupRequired) {
      this.user.set(null);
      return;
    }
    try {
      const session = await firstValueFrom(
        this.http.get<SessionUser>('/api/v1/session', { withCredentials: true }),
      );
      this.user.set(session);
    } catch (error) {
      if (error instanceof HttpErrorResponse && error.status === 401) {
        this.user.set(null);
        return;
      }
      throw error;
    }
  }
}
