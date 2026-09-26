# Automations reference

Every lead source ends in the same intake path:

1. **De-duplicate** by email (case-insensitive). A known email returns the existing lead instead of creating a copy.
2. **Assign** to the SDR with the fewest open leads, when "Assign new leads automatically" is on (Settings, Lead sources). Manual entries keep the owner chosen in the form.
3. **Suggest details** with the rules (website from the email domain, industry from the company name, phone format, country from the domain), when "Suggest missing details" is on.
4. **AI research** in the background, when a provider and key are connected (Settings, AI).
5. **Notify** the configured channel for the events you picked.

Your intake key and address are on the Automations page and under Settings, Lead sources. Creating a new key immediately disables the old one.

## Website form (no code)

Set your form's action to `https://<your-cadence>/intake/<intake-key>` with method POST.

| Field | Also accepted | Notes |
| --- | --- | --- |
| `company` | `company_name`, `organization`, `business` | Company or email is required. |
| `name` | `contact_name`, `full_name`, `first_name` + `last_name` | |
| `email` | `email_address` | Used for de-duplication. |
| `phone` | `tel`, `whatsapp` | |
| `title` | `job_title`, `role` | |
| `website` | `url` | |
| `message` | `notes`, `comments` | Stored in the lead's notes. |
| `_redirect` | | Absolute URL to send the visitor to afterwards. |
| `_gotcha` | | Hidden honeypot. If a bot fills it, the submission is logged as rejected and no lead is created. |

```html
<form action="https://<your-cadence>/intake/<intake-key>" method="POST">
  <input name="company" placeholder="Company" required>
  <input name="name" placeholder="Your name">
  <input name="email" type="email" placeholder="Work email" required>
  <input name="phone" placeholder="Phone">
  <textarea name="message" placeholder="How can we help?"></textarea>
  <input name="_gotcha" style="display:none" tabindex="-1" autocomplete="off">
  <input type="hidden" name="_redirect" value="https://yoursite.com/thanks">
  <button type="submit">Send</button>
</form>
```

The endpoint also accepts a JSON object with the same field names and sends CORS headers, so it works from `fetch()` on your site.

## Zapier, Make, n8n

| Tool | Step |
| --- | --- |
| Zapier | Action "Webhooks by Zapier", event POST |
| Make | Module "HTTP, Make a request" |
| n8n | Node "HTTP Request" |

- URL: `https://<your-cadence>/webhooks/leads`, method POST, body JSON.
- Header: `X-Cadence-Key: <intake-key>`.
- Body fields: `company` (required), `contact_name`, `contact_title`, `email`, `phone`, `website`, `notes`, and `source` (for example `zapier`) so the delivery log shows where it came from.

Responses: `201` created (with `leadId`, `suggestions`, `assignedTo`), `200` duplicate, `400` missing company or bad JSON, `401` wrong key.

```bash
curl -X POST https://<your-cadence>/webhooks/leads \
  -H "Content-Type: application/json" \
  -H "X-Cadence-Key: <intake-key>" \
  -d '{"source":"zapier","company":"Acme Plumbing","contact_name":"Jordan Lee","email":"jordan@acmeplumbing.com"}'
```

## Signed webhook (developers)

Instead of the key, sign the exact raw body: `X-Cadence-Signature: sha256=<hex HMAC-SHA256(Webhooks:Secret, body)>`. The comparison is constant-time. Every delivery, accepted or rejected, is written to the delivery log.

## CSV import

Leads, Import CSV. Up to 200 rows and 2 MB per file, comma or semicolon separated, header row required. Headers are matched by name, so exports from HubSpot, Apollo, LinkedIn Sales Navigator or a hand-made sheet work:

`company` (company name, organization, account), `contact name` (or first name + last name), `title` (job title, position), `email`, `phone` (mobile, whatsapp), `website` (domain, url), `industry`, `country`, `employees` (company size, headcount), `notes`.

Rows without a company and emails that already exist are skipped and counted in the result.

## Notifications

Settings, Lead sources, "Notify a channel". Paste one HTTPS URL and pick the events:

| Event | When |
| --- | --- |
| `lead.created` | A lead arrives from any source |
| `lead.qualified` | Someone moves a lead to Qualified |
| `lead.disqualified` | Someone disqualifies a lead (with the reason) |
| `report.daily` | The team lead presses "Send to channel" on the Stand-up page |

Slack, Microsoft Teams and Discord incoming-webhook URLs receive a chat message. Any other URL receives JSON `{ "event", "text", "data", "sentAt" }` with an `X-Cadence-Signature` header computed like inbound webhooks, so the receiver can verify it. Deliveries appear in the log as "Out".
