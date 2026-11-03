export interface ApiTokenResponse {
  readonly id: string;
  readonly name: string;
  readonly tokenPrefix: string;
  readonly role: 'admin' | 'viewer';
  readonly isActive: boolean;
  readonly createdAt: string;
  readonly expiresAt?: string | null;
  readonly lastUsedAt?: string | null;
  readonly revokedAt?: string | null;
  readonly scopes?: readonly string[] | null;
}

export interface CreateApiTokenRequest {
  readonly name: string;
  readonly role: 'admin' | 'viewer';
  readonly expiresAt?: string | null;
  readonly scopes?: readonly string[];
  readonly currentPassword: string;
}

export interface CreatedApiTokenResponse {
  readonly id: string;
  readonly name: string;
  readonly tokenPrefix: string;
  readonly role: 'admin' | 'viewer';
  readonly createdAt: string;
  readonly expiresAt?: string | null;
  readonly token: string;
}
