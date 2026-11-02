/** Mode de fonctionnement du nœud local : fonctions serveur et/ou worker (architecture 0036). */
export interface NodeMode {
  readonly server: boolean;
  readonly worker: boolean;
}

/** `managed` est faux hors image unique : le mode se règle alors par la configuration du déploiement. */
export interface NodeModeStatus extends NodeMode {
  readonly managed: boolean;
}

/** Données de la boîte de dialogue : `remoteNodeName` désigne un autre nœud du cluster, dont le mode est appliqué à son prochain signal. */
export interface NodeModeDialogData extends NodeModeStatus {
  readonly remoteNodeName?: string;
}
