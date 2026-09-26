/**
 * Single-vendor deployment (Workers Paid, wrangler.containers.jsonc).
 *
 *  - /internal/d1/*  ->  D1 bridge for the app (same handler as the free-plan proxy)
 *  - everything else ->  the ASP.NET Core app running in a Cloudflare Container
 */
import { Container, getContainer } from '@cloudflare/containers';
import { handleD1, type D1Env } from './d1';

export class CadenceApp extends Container {
  defaultPort = 8080;
  sleepAfter = '10m';
}

interface Env extends D1Env {
  CADENCE_APP: DurableObjectNamespace<CadenceApp>;
  PUBLIC_URL: string;
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);
    if (url.pathname.startsWith('/internal/')) return handleD1(request, env, url.pathname);

    const container = getContainer(env.CADENCE_APP, 'main');
    await container.startAndWaitForPorts({
      startOptions: {
        envVars: {
          Database__Backend: 'd1',
          Database__WorkerUrl: env.PUBLIC_URL,
          Database__InternalToken: env.INTERNAL_TOKEN,
        },
      },
    });
    return container.fetch(request);
  },
} satisfies ExportedHandler<Env>;
