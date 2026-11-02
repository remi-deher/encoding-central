export interface FlowPoint {
  readonly x: number;
  readonly y: number;
}
export interface FlowObstacle {
  readonly left: number;
  readonly right: number;
  readonly top: number;
  readonly bottom: number;
}

/** Orthogonal visibility graph: links travel through free space around cards. */
export function routeFlowLink(
  source: FlowPoint,
  target: FlowPoint,
  obstacles: readonly FlowObstacle[],
  width: number,
): string {
  const start = { x: source.x, y: source.y + 12 };
  const end = { x: target.x, y: target.y - 12 };
  const xs = [
    ...new Set([
      8,
      width - 8,
      start.x,
      end.x,
      ...obstacles.flatMap((o) => [Math.max(8, o.left - 12), Math.min(width - 8, o.right + 12)]),
    ]),
  ].sort((a, b) => a - b);
  const ys = [
    ...new Set([start.y, end.y, ...obstacles.flatMap((o) => [o.top - 12, o.bottom + 12])]),
  ].sort((a, b) => a - b);
  const points = ys.flatMap((y) => xs.map((x) => ({ x, y })));
  const blocked = (a: FlowPoint, b: FlowPoint) =>
    obstacles.some((o) =>
      a.x === b.x
        ? a.x > o.left &&
          a.x < o.right &&
          Math.max(a.y, b.y) > o.top &&
          Math.min(a.y, b.y) < o.bottom
        : a.y > o.top &&
          a.y < o.bottom &&
          Math.max(a.x, b.x) > o.left &&
          Math.min(a.x, b.x) < o.right,
    );
  const index = (p: FlowPoint) => ys.indexOf(p.y) * xs.length + xs.indexOf(p.x);
  const first = index(start),
    last = index(end);
  const distance = new Map<number, number>([[first, 0]]),
    previous = new Map<number, number>();
  const pending = new Set([first]);
  while (pending.size) {
    const current = [...pending].reduce((a, b) => (distance.get(a)! <= distance.get(b)! ? a : b));
    pending.delete(current);
    if (current === last) break;
    const column = current % xs.length;
    const neighbors = [
      column > 0 ? current - 1 : -1,
      column < xs.length - 1 ? current + 1 : -1,
      current - xs.length,
      current + xs.length,
    ];
    for (const next of neighbors) {
      if (next < 0 || next >= points.length || blocked(points[current], points[next])) continue;
      const cost =
        distance.get(current)! +
        Math.abs(points[current].x - points[next].x) +
        Math.abs(points[current].y - points[next].y);
      if (cost >= (distance.get(next) ?? Infinity)) continue;
      distance.set(next, cost);
      previous.set(next, current);
      pending.add(next);
    }
  }
  // No unsafe fallback crossing a card when no corridor is available.
  if (!distance.has(last)) return '';
  const chain = [end];
  let cursor = last;
  while (cursor !== first) {
    cursor = previous.get(cursor)!;
    chain.unshift(points[cursor]);
  }
  const vertices = [source, ...chain, target].filter(
    (p, i, a) => !i || p.x !== a[i - 1].x || p.y !== a[i - 1].y,
  );
  const corners = vertices.filter(
    (p, i, a) =>
      !i ||
      i === a.length - 1 ||
      !((a[i - 1].x === p.x && p.x === a[i + 1].x) || (a[i - 1].y === p.y && p.y === a[i + 1].y)),
  );
  let path = 'M ' + source.x + ' ' + source.y;
  for (let i = 1; i < corners.length - 1; i++) {
    const a = corners[i - 1],
      b = corners[i],
      c = corners[i + 1];
    const before = Math.hypot(b.x - a.x, b.y - a.y),
      after = Math.hypot(c.x - b.x, c.y - b.y);
    const radius = Math.min(8, before / 2, after / 2);
    const p = {
      x: b.x + ((a.x - b.x) * radius) / before,
      y: b.y + ((a.y - b.y) * radius) / before,
    };
    const q = { x: b.x + ((c.x - b.x) * radius) / after, y: b.y + ((c.y - b.y) * radius) / after };
    path += ' L ' + p.x + ' ' + p.y + ' Q ' + b.x + ' ' + b.y + ' ' + q.x + ' ' + q.y;
  }
  return path + ' L ' + target.x + ' ' + target.y;
}
