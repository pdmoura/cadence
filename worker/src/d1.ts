/**
 * The D1 bridge. D1 is only reachable from a Worker binding, so the ASP.NET Core app sends
 * SQL here over HTTPS and this runs it against the binding. Shared by both deploy modes.
 *
 *   POST /internal/d1/query  { sql, params }        -> { results, meta: { changes, lastRowId } }
 *   POST /internal/d1/batch  { statements: [...] }  -> { results: [{ changes, lastRowId }] }  (atomic)
 *
 * Every request must carry X-Internal-Token equal to the INTERNAL_TOKEN secret.
 */

export interface D1Env {
  DB: D1Database;
  INTERNAL_TOKEN: string;
}

interface QueryBody { sql: string; params?: unknown[] }
interface BatchBody { statements: QueryBody[] }

export const json = (data: unknown, status = 200) =>
  new Response(JSON.stringify(data), { status, headers: { 'content-type': 'application/json' } });

/** Constant-time string comparison so the token check does not leak length or prefix timing. */
function safeEqual(a: string, b: string): boolean {
  const enc = new TextEncoder();
  const x = enc.encode(a);
  const y = enc.encode(b);
  let diff = x.length ^ y.length;
  for (let i = 0; i < Math.max(x.length, y.length); i++) diff |= (x[i] ?? 0) ^ (y[i] ?? 0);
  return diff === 0;
}

/** D1 rejects `undefined` bindings; JSON never produces it, but be explicit. */
const clean = (params: unknown[] = []) => params.map((p) => (p === undefined ? null : p));

export async function handleD1(request: Request, env: D1Env, pathname: string): Promise<Response> {
  if (!env.INTERNAL_TOKEN || !safeEqual(request.headers.get('x-internal-token') ?? '', env.INTERNAL_TOKEN)) {
    return json({ error: 'forbidden' }, 403);
  }
  if (request.method !== 'POST') return json({ error: 'method not allowed' }, 405);

  try {
    if (pathname === '/internal/d1/query') {
      const { sql, params } = (await request.json()) as QueryBody;
      const result = await env.DB.prepare(sql).bind(...clean(params)).all();
      return json({ results: result.results, meta: { changes: result.meta.changes ?? 0, lastRowId: result.meta.last_row_id ?? 0 } });
    }
    if (pathname === '/internal/d1/batch') {
      const { statements } = (await request.json()) as BatchBody;
      const results = await env.DB.batch(statements.map((s) => env.DB.prepare(s.sql).bind(...clean(s.params))));
      return json({ results: results.map((r) => ({ changes: r.meta.changes ?? 0, lastRowId: r.meta.last_row_id ?? 0 })) });
    }
    return json({ error: 'not found' }, 404);
  } catch (err) {
    return json({ error: err instanceof Error ? err.message : String(err) }, 500);
  }
}
