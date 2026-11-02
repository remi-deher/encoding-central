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

export interface ApiTokenReplicationEntry {
  readonly id: string;
  readonly revoked: boolean;
}
/** `tokens` vaut null pour un nœud injoignable : son état est inconnu. */
export interface ApiTokenReplicationNode {
  readonly nodeId: string;
  readonly name: string;
  readonly reachable: boolean;
  readonly tokens?: readonly ApiTokenReplicationEntry[] | null;
}
export interface ApiTokenReplicationView {
  readonly nodes: readonly ApiTokenReplicationNode[];
}

export interface TokenPropagation {
  readonly label: string;
  readonly tone: 'success' | 'warning' | 'neutral';
}

/** Propagation d'un jeton aux autres nœuds ; null sur un nœud seul (rien à propager). */
export function tokenPropagation(
  token: Pick<ApiTokenResponse, 'id' | 'isActive'>,
  view: ApiTokenReplicationView | null,
): TokenPropagation | null {
  if (!view?.nodes || view.nodes.length < 2) return null;
  const answering = view.nodes.filter((node) => node.reachable && node.tokens);
  const unreachable = view.nodes.length - answering.length;
  const upToDate = answering.filter((node) => {
    const entry = node.tokens!.find((candidate) => candidate.id === token.id);
    return !!entry && entry.revoked === !token.isActive;
  }).length;
  const suffix = unreachable ? ` · ${unreachable} injoignable${unreachable > 1 ? 's' : ''}` : '';
  const complete = upToDate === answering.length;
  const label = token.isActive
    ? `Présent sur ${upToDate} nœud${upToDate > 1 ? 's' : ''} sur ${view.nodes.length}${suffix}`
    : `Révocation reçue par ${upToDate} nœud${upToDate > 1 ? 's' : ''} sur ${view.nodes.length}${suffix}`;
  return { label, tone: complete && !unreachable ? 'success' : complete ? 'neutral' : 'warning' };
}
