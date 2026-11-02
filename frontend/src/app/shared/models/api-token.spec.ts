import { ApiTokenReplicationView, tokenPropagation } from './api-token';

describe('tokenPropagation', () => {
  const node = (name: string, tokens: { id: string; revoked: boolean }[] | null) => ({
    nodeId: name,
    name,
    reachable: tokens !== null,
    tokens,
  });

  it('n’affiche rien sur un nœud seul', () => {
    const view: ApiTokenReplicationView = { nodes: [node('a', [{ id: 't', revoked: false }])] };
    expect(tokenPropagation({ id: 't', isActive: true }, view)).toBeNull();
    expect(tokenPropagation({ id: 't', isActive: true }, null)).toBeNull();
  });

  it('ignore une réponse sans liste de nœuds', () => {
    const view = [] as unknown as ApiTokenReplicationView;
    expect(tokenPropagation({ id: 't', isActive: true }, view)).toBeNull();
  });

  it('compte les nœuds qui ont le jeton et signale un écart', () => {
    const view: ApiTokenReplicationView = {
      nodes: [node('a', [{ id: 't', revoked: false }]), node('b', [])],
    };
    expect(tokenPropagation({ id: 't', isActive: true }, view)).toEqual({
      label: 'Présent sur 1 nœud sur 2',
      tone: 'warning',
    });
  });

  it('suit la propagation d’une révocation et ne présume pas qu’un nœud injoignable est à jour', () => {
    const view: ApiTokenReplicationView = {
      nodes: [
        node('a', [{ id: 't', revoked: true }]),
        node('b', [{ id: 't', revoked: true }]),
        node('c', null),
      ],
    };
    expect(tokenPropagation({ id: 't', isActive: false }, view)).toEqual({
      label: 'Révocation reçue par 2 nœuds sur 3 · 1 injoignable',
      tone: 'neutral',
    });
    const stale: ApiTokenReplicationView = {
      nodes: [node('a', [{ id: 't', revoked: true }]), node('b', [{ id: 't', revoked: false }])],
    };
    expect(tokenPropagation({ id: 't', isActive: false }, stale)?.tone).toBe('warning');
  });

  it('est au vert quand tous les nœuds ont le jeton', () => {
    const view: ApiTokenReplicationView = {
      nodes: [node('a', [{ id: 't', revoked: false }]), node('b', [{ id: 't', revoked: false }])],
    };
    expect(tokenPropagation({ id: 't', isActive: true }, view)?.tone).toBe('success');
  });
});
