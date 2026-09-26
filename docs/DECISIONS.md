# Technical decisions

Short records of the choices that shaped Cadence, in the order they were made.

## 1. Hand-written SQL behind a tiny executor, not an ORM

D1 is SQLite with a Worker-only API. An ORM would either hide that or need a driver that does not exist. `ISqlExecutor` has four methods (`Query`, `Execute`, `Insert`, `Batch`) and every repository is plain SQL with `?` parameters. The cost is manual row mapping (`Row` helpers keep it to one line per column). The benefit is that the SQL in the repo is exactly the SQL that runs in both environments, and reviewers can read it.

## 2. Migrations as numbered files shared by both backends

`db/migrations/0001_init.sql` uses only what D1 supports: `CREATE TABLE IF NOT EXISTS`, `CHECK` constraints, `strftime` defaults, `ON CONFLICT ... DO UPDATE`. The app applies them on SQLite at startup with its own `schema_migrations` table; Wrangler applies the same files to D1. The seed is a migration too so a fresh environment tells a story; it is marked as safe to delete.

## 3. Suggestions are a separate table with a review status

The alternative, writing AI output straight to the lead and flagging it, makes "what did the model change" impossible to answer later. Keeping proposals in `enrichment_suggestions` with `source`, `confidence`, `rationale`, `reviewed_by` and `reviewed_at` gives an audit trail and a natural acceptance-rate metric on the Reports page.

## 4. Pipeline transitions are validated in a service, recorded in an event table

`LeadPipelineService.MoveAsync` owns the rules (allowed moves, validation gate, reason required to disqualify). `lead_stage_events` is the source of truth for daily counts, so editing or re-opening a lead cannot distort last week's numbers.

## 5. Identity is a cookie-selected member, not a login

Cadence is an internal tool; in production it sits behind Cloudflare Access, which already authenticates the team. Building a password system would add risk (credential storage) for no product value. Every write still records `member_id`, so accountability is kept.

## 6. Container talks to D1 through the Worker with a shared token

Containers cannot bind D1 directly. The Worker exposes `/internal/d1/query` and `/internal/d1/batch`, checks `X-Internal-Token`, and forwards everything else to the container. The token is a Wrangler secret passed to the container as an environment variable at start, so it never sits in an image layer or in the repository.

## 7. No frontend build step

One stylesheet with design tokens, one 40-line script for progressive enhancement. Razor renders everything server-side; forms work without JavaScript. For a tool the SDR team will change often, fewer moving parts beats a component framework.

## 8. Free-plan production: app on Render, data on D1

Cloudflare Containers need the Workers Paid plan; Workers and D1 do not. The app therefore runs as a Docker service on Render's free tier and reaches D1 through the Worker, which is now a pure data bridge (`worker/src/proxy.ts`). Render's free disk is ephemeral, so keeping the database in D1 is a requirement, not a preference. The all-Cloudflare topology still exists as `wrangler.containers.jsonc`; both share `worker/src/d1.ts`, so the bridge the app talks to is the same code either way. The token check uses a constant-time comparison.
