/**
 * Free-plan deployment (default, wrangler.jsonc).
 *
 * The Worker only owns the data: it exposes D1 to the ASP.NET Core app, which runs as a Docker
 * container on Render (or any host). Workers and D1 are both on Cloudflare's free plan.
 */
import { handleD1, json, type D1Env } from './d1';

export default {
  async fetch(request: Request, env: D1Env): Promise<Response> {
    const url = new URL(request.url);

    if (url.pathname.startsWith('/internal/')) return handleD1(request, env, url.pathname);

    if (url.pathname === '/healthz') {
      // Proves the binding and the schema are there without exposing data.
      try {
        const row = await env.DB.prepare('SELECT COUNT(*) AS n FROM leads').first<{ n: number }>();
        return json({ status: 'ok', d1: 'ok', leads: row?.n ?? 0, timestamp: new Date().toISOString() });
      } catch (err) {
        return json({ status: 'degraded', d1: err instanceof Error ? err.message : String(err) }, 503);
      }
    }

    return json({ service: 'cadence-d1', message: 'Data layer for Cadence. The app itself is served from its own host.' });
  },
} satisfies ExportedHandler<D1Env>;
