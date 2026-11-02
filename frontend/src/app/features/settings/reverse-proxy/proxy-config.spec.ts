import {
  DEFAULT_DOMAIN,
  domainOf,
  generateProxyConfig,
  parseBackend,
  parseBackends,
  ProxyConfigInput,
} from './proxy-config';

const two = parseBackends('192.168.1.50:8090\n192.168.1.51:8090');

function input(overrides: Partial<ProxyConfigInput> = {}): ProxyConfigInput {
  return {
    proxy: 'nginx',
    domain: 'encoding.lan',
    backends: two,
    redundancy: true,
    https: false,
    ...overrides,
  };
}

describe('proxy-config', () => {
  it('reads backends with or without scheme and port', () => {
    expect(parseBackend('192.168.1.50:8090')).toEqual({
      scheme: 'http',
      host: '192.168.1.50:8090',
    });
    expect(parseBackend('https://nas.lan/')).toEqual({ scheme: 'https', host: 'nas.lan:443' });
    expect(parseBackend('nas')).toEqual({ scheme: 'http', host: 'nas:80' });
    expect(parseBackend('  ')).toBeNull();
  });

  it('drops duplicates and blanks', () => {
    expect(parseBackends('a:1\n\na:1, b:2').map((b) => b.host)).toEqual(['a:1', 'b:2']);
  });

  it('extracts the host name of the public URL', () => {
    expect(domainOf('https://encoding.example.com/')).toBe('encoding.example.com');
    expect(domainOf('')).toBeNull();
    expect(domainOf('pas une url')).toBeNull();
  });

  it('generates a Nginx upstream with a backup node and no buffering', () => {
    const { text, fileName } = generateProxyConfig(input());
    expect(fileName).toBe('encoding.conf');
    expect(text).toContain('server 192.168.1.50:8090 max_fails=2 fail_timeout=10s;');
    expect(text).toContain('server 192.168.1.51:8090 max_fails=2 fail_timeout=10s backup;');
    expect(text).toContain('server_name encoding.lan;');
    expect(text).toContain('listen 80;');
    expect(text).toContain('proxy_buffering off;');
  });

  it('keeps only the first node without redundancy', () => {
    const { text } = generateProxyConfig(input({ redundancy: false }));
    expect(text).toContain('192.168.1.50:8090');
    expect(text).not.toContain('192.168.1.51');
  });

  it('listens in HTTPS with certificate placeholders when asked', () => {
    const { text } = generateProxyConfig(input({ https: true }));
    expect(text).toContain('listen 443 ssl;');
    expect(text).toContain('ssl_certificate_key');
  });

  it('generates a Caddyfile with health checks and plain HTTP by default', () => {
    const { text } = generateProxyConfig(input({ proxy: 'caddy' }));
    expect(text).toContain('http://encoding.lan {');
    expect(text).toContain('reverse_proxy 192.168.1.50:8090 192.168.1.51:8090 {');
    expect(text).toContain('health_uri /health/ready');
    expect(text).toContain('flush_interval -1');
    expect(generateProxyConfig(input({ proxy: 'caddy', https: true })).text).toMatch(
      /^encoding\.lan \{/,
    );
  });

  it('generates a Traefik file with sticky cookie and health check', () => {
    const { text } = generateProxyConfig(input({ proxy: 'traefik' }));
    expect(text).toContain('Host(`encoding.lan`)');
    expect(text).toContain('entryPoints: [web]');
    expect(text).toContain('path: /health/ready');
    expect(text).toContain('- url: "http://192.168.1.51:8090"');
    expect(generateProxyConfig(input({ proxy: 'traefik', https: true })).text).toContain(
      'websecure',
    );
  });

  it('gives Nginx Proxy Manager the first node in its notes and fallbacks in the custom block', () => {
    const out = generateProxyConfig(input({ proxy: 'npm' }));
    expect(out.text).toContain('proxy_buffering off;');
    expect(out.text).toContain('error_page 502 503 504 = @encoding_1;');
    expect(out.text).toContain('location @encoding_1 {');
    expect(out.text).toContain('proxy_pass http://192.168.1.51:8090;');
    expect(out.text).not.toContain('192.168.1.50');
    expect(out.notes.join(' ')).toContain('192.168.1.50');
  });

  it('chains several fallbacks for Nginx Proxy Manager and omits them with one node', () => {
    const three = parseBackends('a:1\nb:2\nc:3');
    const chained = generateProxyConfig(input({ proxy: 'npm', backends: three })).text;
    expect(chained).toContain('location @encoding_2 {');
    expect(chained.match(/error_page 502 503 504 = @encoding_2;/g)).toHaveLength(1);
    const single = generateProxyConfig(input({ proxy: 'npm', redundancy: false })).text;
    expect(single).not.toContain('@encoding_1');
  });

  it('falls back to a placeholder domain', () => {
    expect(generateProxyConfig(input({ domain: ' ' })).text).toContain(DEFAULT_DOMAIN);
  });
});
