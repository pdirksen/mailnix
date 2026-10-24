# mailnix

Rule-based mail sorting with a web UI, in the spirit of imapfilter: connect IMAP, Google or Microsoft 365
mailboxes, build rules visually, preview what they would do, and let mailnix move, flag or trash incoming mail.

> Status: early development. Auth, IMAP accounts, the rule engine, named lists and the visual rule builder with a
> read-only preview against real mail are in place; executing rules on the mailbox, scheduling and stats follow.

## Repository layout

```
backend/    .NET 10 solution (Mailnix.slnx)
  src/Mailnix.Api             ASP.NET Core host: REST API, auth, serves the SPA
  src/Mailnix.Core            rule engine (pure): conditions, evaluator with explanations, action planning
  src/Mailnix.Infrastructure  EF Core (SQLite + PostgreSQL), Identity, data protection
  src/Mailnix.Mail            IMAP access via MailKit (connect, probe folders/capabilities)
  tests/                      xUnit v3: API integration tests, mail tests with an in-process fake IMAP server
  openapi/mailnix.json        OpenAPI document, regenerated on every build
frontend/   Vue 3 + TypeScript + Vite, shadcn-vue (Reka UI) + Tailwind CSS
docker/     Dockerfile and compose files
```

## Development

Prerequisites: .NET SDK 10, Node.js 22+.

```bash
# Backend on http://localhost:5080 (SQLite database in backend/src/Mailnix.Api/data/)
cd backend
dotnet run --project src/Mailnix.Api

# Frontend on http://localhost:5173, proxies /api to the backend
cd frontend
npm install
npm run dev
```

The first account you register becomes the administrator (unless a bootstrap admin is configured).

After changing API endpoints, rebuild the backend and run `npm run gen:api` in `frontend/` to regenerate the typed client.

### Checks

```bash
cd backend
dotnet build && dotnet test
dotnet tool restore && dotnet nuget-license -i Mailnix.slnx -t -a licenses-allowed.json

cd frontend
npm run type-check && npm run lint && npm test
npm run license:check
npm run test:e2e        # needs a running instance with the built SPA, see playwright.config.ts
```

### Optional tests against a real mailbox

Some backend and e2e tests run against a real IMAP account when `test-email-credentials.ini` exists in the
repository root (git-ignored) or `MAILNIX_TEST_IMAP_CREDENTIALS` points to such a file; otherwise they are skipped.
They only read (log in, list folders, count messages) and never send wrong passwords, so they cannot trip
fail2ban-style lockouts.

```ini
[imap]
host=imap.example.com
port=993
security=SSL        ; SSL | STARTTLS | NONE
user=me@example.com
password=...
```

## Running with Docker

```bash
docker compose -f docker/compose.selfhosted.yml up -d --build
```

Open http://localhost:8080. Data (SQLite database, encryption keys) lives in the `mailnix-data` volume.

## Configuration

All settings can be given as environment variables (`__` separates sections).

| Setting | Default | Description |
|---|---|---|
| `Mailnix__Mode` | `AllInOne` | `AllInOne`, `Api` (API + UI only) or `Worker` (mailbox processing only) |
| `Mailnix__Database__Provider` | `Sqlite` | `Sqlite` or `Postgres` |
| `Mailnix__Database__ConnectionString` | `Data Source=data/mailnix.db` | |
| `Mailnix__Database__AutoMigrate` | `true` | Apply database migrations on startup |
| `Mailnix__Auth__AllowRegistration` | `true` | Self-service signup (the first user can always register) |
| `Mailnix__Bootstrap__AdminEmail` / `AdminPassword` | – | Create this admin account on first start |
| `Mailnix__Mail__AllowInsecureConnections` | `false` | Allow unencrypted IMAP (passwords in clear text) |
| `Mailnix__Mail__BlockPrivateNetworks` | `false` | Refuse mail servers on private/local addresses (recommended for SaaS) |
| `Mailnix__Mail__ConnectTimeout` | `00:00:30` | Timeout for connecting and logging in |
| `Mailnix__RateLimits__MailConnectionsPerMinute` | `20` | Per-user limit for requests that open IMAP connections |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | `false` | Set to `true` behind a TLS-terminating reverse proxy |

## Licensing

mailnix is MIT licensed. Dependencies are limited to permissive licenses compatible with that
(MIT, ISC, BSD, Apache-2.0, PostgreSQL, …). Build-only tooling may additionally be MPL-2.0 (e.g. lightningcss
inside Vite), which is never shipped. Both license gates run in CI; the Docker image contains
`wwwroot/THIRD-PARTY-NOTICES.md` for the frontend.
