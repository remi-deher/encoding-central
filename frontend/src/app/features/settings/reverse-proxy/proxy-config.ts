export type ProxyKind = 'nginx' | 'caddy' | 'traefik' | 'npm';

export interface ProxyBackend {
  readonly scheme: 'http' | 'https';
  /** `hôte:port`, sans schéma ni « / » final. */
  readonly host: string;
}

export interface ProxyConfigInput {
  readonly proxy: ProxyKind;
  readonly domain: string;
  readonly backends: readonly ProxyBackend[];
  /** Plusieurs nœuds serveurs derrière le proxy ; sinon seul le premier est utilisé. */
  readonly redundancy: boolean;
  /** Le proxy termine le HTTPS ; le certificat reste à la charge de l'utilisateur. */
  readonly https: boolean;
}

export interface ProxyConfigOutput {
  readonly fileName: string;
  readonly text: string;
  readonly notes: readonly string[];
}

export const PROXY_LABELS: Readonly<Record<ProxyKind, string>> = {
  nginx: 'Nginx',
  caddy: 'Caddy',
  traefik: 'Traefik',
  npm: 'Nginx Proxy Manager',
};

export const DEFAULT_DOMAIN = 'encoding.example.com';

/** Sonde de santé publique des nœuds serveurs. */
export const HEALTH_PATH = '/health/ready';

const MAX_BODY_MB = 100;
const STREAM_TIMEOUT = '1h';

/** Lit une ligne saisie : `192.168.1.50:8090`, `http://nas:8090/` ou `https://nas`. */
export function parseBackend(line: string): ProxyBackend | null {
  const trimmed = line.trim();
  if (!trimmed) return null;
  const withScheme = /^https?:\/\//i.test(trimmed) ? trimmed : 'http://' + trimmed;
  try {
    const url = new URL(withScheme);
    if (!url.hostname) return null;
    return {
      scheme: url.protocol === 'https:' ? 'https' : 'http',
      host: url.host + (url.port ? '' : ':' + (url.protocol === 'https:' ? 443 : 80)),
    };
  } catch {
    return null;
  }
}

export function parseBackends(text: string): readonly ProxyBackend[] {
  const seen = new Set<string>();
  const result: ProxyBackend[] = [];
  for (const line of text.split(/[\r\n,;]+/)) {
    const backend = parseBackend(line);
    if (backend && !seen.has(backend.scheme + backend.host)) {
      seen.add(backend.scheme + backend.host);
      result.push(backend);
    }
  }
  return result;
}

/** Nom d'hôte d'une URL publique, ou `null` si elle est absente ou illisible. */
export function domainOf(publicUrl: string | null | undefined): string | null {
  if (!publicUrl?.trim()) return null;
  try {
    return new URL(publicUrl).hostname || null;
  } catch {
    return null;
  }
}

export function generateProxyConfig(input: ProxyConfigInput): ProxyConfigOutput {
  const domain = input.domain.trim() || DEFAULT_DOMAIN;
  const backends = input.redundancy ? input.backends : input.backends.slice(0, 1);
  switch (input.proxy) {
    case 'nginx':
      return nginx(domain, backends, input.https);
    case 'caddy':
      return caddy(domain, backends, input.https);
    case 'traefik':
      return traefik(domain, backends, input.https);
    case 'npm':
      return npm(domain, backends);
  }
}

const REMINDER =
  'Ajoutez ensuite l’adresse du proxy dans « Adresses ou réseaux du proxy » ci-dessus, sinon l’adresse et le schéma vus par l’application restent ceux du proxy.';

function nginxProxyBlock(upstreamOrUrl: string): string {
  return [
    `        proxy_pass ${upstreamOrUrl};`,
    '        proxy_http_version 1.1;',
    '        proxy_set_header Connection "";',
    '        proxy_set_header Host $host;',
    '        proxy_set_header X-Real-IP $remote_addr;',
    '        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;',
    '        proxy_set_header X-Forwarded-Proto $scheme;',
    '        # Flux en direct (suivi des exécutions, journaux) : pas de mise en tampon.',
    '        proxy_buffering off;',
    `        proxy_read_timeout ${STREAM_TIMEOUT};`,
    `        proxy_send_timeout ${STREAM_TIMEOUT};`,
    '        proxy_next_upstream error timeout http_502 http_503 http_504;',
  ].join('\n');
}

function nginxUpstream(backends: readonly ProxyBackend[]): string {
  const lines = backends.map(
    (b, i) => `    server ${b.host} max_fails=2 fail_timeout=10s${i > 0 ? ' backup' : ''};`,
  );
  return ['upstream encoding {', ...lines, '}'].join('\n');
}

function schemeOf(backends: readonly ProxyBackend[]): string {
  return backends[0]?.scheme ?? 'http';
}

function nginx(
  domain: string,
  backends: readonly ProxyBackend[],
  https: boolean,
): ProxyConfigOutput {
  const listen = https
    ? [
        '    listen 443 ssl;',
        '    http2 on;',
        '    ssl_certificate     /chemin/vers/fullchain.pem;',
        '    ssl_certificate_key /chemin/vers/privkey.pem;',
      ]
    : ['    listen 80;'];
  const text = [
    nginxUpstream(backends),
    '',
    'server {',
    ...listen,
    `    server_name ${domain};`,
    `    client_max_body_size ${MAX_BODY_MB}m;`,
    '',
    '    location / {',
    nginxProxyBlock(`${schemeOf(backends)}://encoding`),
    '    }',
    '}',
    '',
  ].join('\n');
  const notes = [
    'À placer dans /etc/nginx/conf.d/encoding.conf (ou sites-available), puis nginx -s reload.',
    ...(backends.length > 1
      ? [
          'Le premier nœud reçoit le trafic ; les autres prennent le relais s’il ne répond plus (bascule passive, Nginx libre n’a pas de sonde active).',
        ]
      : []),
    ...(https ? ['Remplacez les deux chemins de certificat par les vôtres.'] : []),
    REMINDER,
  ];
  return { fileName: 'encoding.conf', text, notes };
}

function caddy(
  domain: string,
  backends: readonly ProxyBackend[],
  https: boolean,
): ProxyConfigOutput {
  const site = https ? domain : `http://${domain}`;
  const targets = backends.map((b) => (b.scheme === 'https' ? `https://${b.host}` : b.host));
  const lines = [
    `${site} {`,
    `    request_body {`,
    `        max_size ${MAX_BODY_MB}MB`,
    '    }',
    '',
  ];
  lines.push(`    reverse_proxy ${targets.join(' ')} {`);
  if (backends.length > 1) {
    lines.push(
      '        lb_policy first',
      '        lb_try_duration 5s',
      `        health_uri ${HEALTH_PATH}`,
      '        health_interval 10s',
      '        health_timeout 3s',
    );
  }
  lines.push(
    '        # Flux en direct (suivi des exécutions, journaux) : pas de mise en tampon.',
    '        flush_interval -1',
    '    }',
    '}',
    '',
  );
  const notes = [
    'À ajouter dans votre Caddyfile, puis caddy reload.',
    ...(backends.length > 1
      ? [
          'Le premier nœud reçoit le trafic ; Caddy sonde /health/ready et bascule sur les suivants si besoin.',
        ]
      : []),
    ...(https
      ? [
          'Caddy obtient et renouvelle le certificat tout seul si le domaine est joignable depuis Internet.',
        ]
      : ['Sans « http:// » devant le domaine, Caddy activerait HTTPS de lui-même.']),
    REMINDER,
  ];
  return { fileName: 'Caddyfile', text: lines.join('\n'), notes };
}

function traefik(
  domain: string,
  backends: readonly ProxyBackend[],
  https: boolean,
): ProxyConfigOutput {
  const lines = [
    '# Fichier dynamique (provider file) de Traefik.',
    'http:',
    '  routers:',
    '    encoding:',
    `      rule: "Host(\`${domain}\`)"`,
    `      entryPoints: [${https ? 'websecure' : 'web'}]`,
    '      service: encoding',
    '      middlewares: [encoding-limits]',
  ];
  if (https) lines.push('      tls: {}');
  lines.push(
    '  middlewares:',
    '    encoding-limits:',
    '      buffering:',
    `        maxRequestBodyBytes: ${MAX_BODY_MB * 1024 * 1024}`,
    '  services:',
    '    encoding:',
    '      loadBalancer:',
  );
  if (backends.length > 1) {
    lines.push(
      '        sticky:',
      '          cookie:',
      '            name: encoding_node',
      '        healthCheck:',
      `          path: ${HEALTH_PATH}`,
      '          interval: 10s',
      '          timeout: 3s',
    );
  }
  lines.push(
    '        servers:',
    ...backends.map((b) => `          - url: "${b.scheme}://${b.host}"`),
    '',
  );
  const notes = [
    'À enregistrer dans le dossier surveillé par le provider file de Traefik (par exemple /etc/traefik/dynamic/encoding.yml).',
    `Les points d’entrée « ${https ? 'websecure' : 'web'} » doivent exister dans votre configuration statique.`,
    ...(backends.length > 1
      ? [
          'Traefik répartit entre les nœuds en gardant chaque navigateur sur le même (cookie) et écarte un nœud dont /health/ready ne répond plus.',
        ]
      : []),
    ...(https ? ['Ajoutez votre « certResolver » dans tls si Traefik gère le certificat.'] : []),
    REMINDER,
  ];
  return { fileName: 'encoding.yml', text: lines.join('\n'), notes };
}

function npm(domain: string, backends: readonly ProxyBackend[]): ProxyConfigOutput {
  const first = backends[0];
  const fallbacks = backends.slice(1);
  const lines = [
    `client_max_body_size ${MAX_BODY_MB}m;`,
    '# Flux en direct (suivi des exécutions, journaux) : pas de mise en tampon.',
    'proxy_buffering off;',
    `proxy_read_timeout ${STREAM_TIMEOUT};`,
    `proxy_send_timeout ${STREAM_TIMEOUT};`,
  ];
  if (fallbacks.length) {
    lines.push(
      '',
      '# Nœuds de secours : utilisés si le précédent ne répond plus.',
      'proxy_intercept_errors on;',
      'proxy_connect_timeout 3s;',
      'error_page 502 503 504 = @encoding_1;',
    );
    fallbacks.forEach((backend, index) => {
      const next =
        index < fallbacks.length - 1 ? `error_page 502 503 504 = @encoding_${index + 2};` : null;
      lines.push(
        '',
        `location @encoding_${index + 1} {`,
        `    proxy_pass ${backend.scheme}://${backend.host};`,
        '    proxy_http_version 1.1;',
        '    proxy_set_header Connection "";',
        '    proxy_set_header Host $host;',
        '    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;',
        '    proxy_set_header X-Forwarded-Proto $scheme;',
        ...(next ? ['    proxy_intercept_errors on;', `    ${next}`] : []),
        '}',
      );
    });
  }
  lines.push('');
  const port = first ? first.host.split(':').pop() : '…';
  const notes = [
    `Dans Nginx Proxy Manager, créez un « Proxy Host » pour ${domain}.`,
    `Onglet Details : schéma ${first?.scheme ?? 'http'}, hôte ${first ? first.host.replace(/:\d+$/, '') : '…'}, port ${port}. Activez « Websockets Support ».`,
    'Onglet Advanced : collez le texte ci-contre dans « Custom Nginx Configuration ». Le HTTPS se règle dans l’onglet SSL.',
    ...(fallbacks.length
      ? [
          'Le nœud saisi dans Details reçoit le trafic ; les suivants prennent le relais s’il répond par une erreur 502, 503 ou 504 (bascule passive, sans sonde de santé).',
        ]
      : []),
    REMINDER,
  ];
  return { fileName: 'encoding-npm.conf', text: lines.join('\n'), notes };
}
