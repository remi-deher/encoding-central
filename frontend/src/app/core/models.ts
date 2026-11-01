export interface SessionUser {
  username: string;
  role: string;
}

export interface ServerInfo {
  id: string;
  displayName: string;
  publicBaseUrl: string;
  createdAt: string;
}

export interface SetupStatus {
  setupRequired: boolean;
}

export interface ApiErrorBody {
  error?: string;
}
