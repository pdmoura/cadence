/**
 * Cadence edge layer.
 *
 *  - Public traffic  ->  forwarded to the ASP.NET Core app running in a Cloudflare Container.
 *  - /internal/d1/*  ->  runs SQL against the D1 binding for the container (D1 is only reachable from a Worker).
 *  - /healthz        ->  answered here so the edge can report even while the container is cold.
 *
 * The container authenticates to the internal endpoints with a shared token (INTERNAL_TOKEN secret).
 */
import { Container, getContainer } from '@cloudflare/containers';

export class CadenceApp extends Container {
  defaultPort = 8080;
  sleepAfter = '10m';
  envVars = {
    Database__Backend: 'd1',
    Database__WorkerUrl: 'http://cadence-worker.internal',
    ASPNETCORE_URLS: 'http://0.0.0.0:8080',
  };
}

interface Env {
  DB: D1Database;
  CADENCE_APP: DurableObjectNamespace<CadenceApp>;
  INTERNAL_TOKEN: string;
  WEBHOOK_SECRET: string;
}

interface QueryBody { sql: string; params?: unknown[] }
interface BatchBody { statements: QueryBody[] }

const json = (data: unknown, status = 200) =>
  new Response(JSON.stringify(data), { status, headers: { 'content-type': 'application/json' } });

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);

    if (url.pathname.startsWith('/internal/')) {
      if (request.headers.get('x-internal-token') !== env.INTERNAL_TOKEN) return json({ error: 'forbidden' }, 403);
      if (request.method !== 'POST') return json({ error: 'method not allowed' }, 405);

      try {
        if (url.pathname === '/internal/d1/query') {
          const { sql, params = [] } = (await request.json()) as QueryBody;
          const result = await env.DB.prepare(sql).bind(...params).all();
          return json({ results: result.results, meta: { changes: result.meta.changes ?? 0, lastRowId: result.meta.last_row_id ?? 0 } });
        }
        if (url.pathname === '/internal/d1/batch') {
          const { statements } = (await request.json()) as BatchBody;
          const prepared = statements.map((s) => env.DB.prepare(s.sql).bind(...(s.params ?? [])));
          const results = await env.DB.batch(prepared);
          return json({ results: results.map((r) => ({ changes: r.meta.changes ?? 0, lastRowId: r.meta.last_row_id ?? 0 })) });
        }
        return json({ error: 'not found' }, 404);
      } catch (err) {
        return json({ error: err instanceof Error ? err.message : String(err) }, 500);
      }
    }

    if (url.pathname === '/healthz' && request.headers.get('x-edge-only') === '1') {
      return json({ status: 'ok', edge: true, timestamp: new Date().toISOString() });
    }

    // Everything else goes to the container. One instance is enough for an internal tool; scale by id if needed.
    const container = getContainer(env.CADENCE_APP, 'main');
    const headers = new Headers(request.headers);
    headers.set('x-internal-token', env.INTERNAL_TOKEN); // lets the app call back into /internal/* through the same origin
    return container.fetch(new Request(request, { headers }));
  },
} satisfies ExportedHandler<Env>;
