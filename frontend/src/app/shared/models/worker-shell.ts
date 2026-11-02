/** Routes servies par l'image worker. Les autres restent sur le central. */
const WORKER_EXACT = new Set(['/', '/libraries', '/login', '/setup']);
const WORKER_PREFIXES = ['/activity', '/nodes', '/plugins', '/settings'];

export function workerShellAllows(url: string): boolean {
  const path = (url.split(/[?#]/, 1)[0] || '/').replace(/\/+$/, '') || '/';
  if (WORKER_EXACT.has(path)) return true;
  return WORKER_PREFIXES.some((prefix) => path === prefix || path.startsWith(`${prefix}/`));
}
