# mailnix

Rule-based mail sorting with a web UI, in the spirit of imapfilter: connect IMAP, Google or Microsoft 365
mailboxes, build rules visually, preview what they would do, and let mailnix move, flag or trash incoming mail.

> Status: early development. Auth, IMAP accounts, the rule engine, named lists, the visual rule builder with a
> preview against real mail, rule execution (new mail via IDLE, "run now", confirmation of large runs, undo,
> live status), scheduled rules with server-side preselection, the optional Trash purge, and
> OAuth sign-in for Gmail and Microsoft 365, and the dashboard and stats are in place; the SaaS mode follows.

## Repository layout

```
backend/    .NET 10 solution (Mailnix.slnx)
  src/Mailnix.Api             ASP.NET Core host: REST API, auth, serves the SPA
  src/Mailnix.Core            rule engine (pure): conditions, evaluator with explanations, action planning
  src/Mailnix.Infrastructure  EF Core (SQLite + PostgreSQL), Identity, data protection
  src/Mailnix.Mail            IMAP access via MailKit (connect, probe folders/capabilities, read, act)
  src/Mailnix.Worker          mailbox watchers (IDLE), run processing, safety gate, undo
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
They never send wrong passwords, so they cannot trip fail2ban-style lockouts. Everywhere except one folder they
only read (log in, list folders, fetch without marking as read). Tests that change mail work exclusively in
`INBOX.mailnix-test` (or `INBOX/mailnix-test`, depending on the server's delimiter) and its subfolders, which they
create, fill via IMAP APPEND and delete again. E2E tests pause any account that watches real folders, so the dev
server's worker never acts on incoming mail, and delete their accounts afterwards.

```ini
[imap]
host=imap.example.com
port=993
security=SSL        ; SSL | STARTTLS | NONE
user=me@example.com
password=...
```

## Standalone Windows app

```powershell
.\scripts\build-windows.ps1            # -Runtime win-arm64, -Zip, -Output <dir>
.\scripts\run-windows.ps1              # builds if needed, starts it, opens the browser; -Build, -Urls, -NoBrowser
```

Builds `artifacts\windows\win-x64\mailnix.exe`: a self-contained single executable (no .NET installation needed)
with the web UI in `wwwroot\` next to it. Start it and open http://localhost:5000 (other address:
`mailnix.exe --urls http://localhost:8080`). The database and encryption keys live in `data\` next to the
executable, whatever the working directory is. Rebuilding keeps an existing `data\` folder, and `-Zip` never
includes it. The encryption keys in it are protected with Windows DPAPI for the account that runs mailnix, so a copied
`data\` folder is useless elsewhere; to run mailnix as a Windows service under another account, set
`Mailnix__DataProtection__KeyProtection=WindowsMachine` before its first start there (or enter the mailbox passwords again).
On Linux, macOS and in Docker, configure a master key (see below); without one, the keys are stored unencrypted in
the database and mailnix logs a warning at startup.

## Gmail and Microsoft 365

Each installation registers its own OAuth app with Google and/or Microsoft; see [docs/oauth-setup.md](docs/oauth-setup.md)
for the steps and the settings. Until then, those providers show as "Not set up" when adding an account.

## Running with Docker

```bash
mkdir -p docker/secrets
openssl rand -base64 32 > docker/secrets/mailnix_master_key   # once; or: mailnix --generate-master-key
docker compose -f docker/compose.selfhosted.yml up -d --build
```

Open http://localhost:8080. Data (SQLite database, encrypted keys) lives in the `mailnix-data` volume. The master key
is mounted as a Docker secret from `docker/secrets/` (git-ignored; the file must be readable by the container) and is
not part of the volume, so a copy of the volume alone cannot decrypt the stored mailbox passwords. Back up the master
key separately; without it, the mailbox passwords have to be entered again.

## Running as a service (SaaS)

`docker/compose.saas.yml` runs mailnix for several organizations: PostgreSQL, two API containers, two mailbox workers
and Caddy in front (automatic HTTPS certificates for your domain).

```bash
mkdir -p docker/secrets
openssl rand -base64 32 > docker/secrets/mailnix_master_key   # once; back it up
export MAILNIX_DOMAIN=mail.example.com MAILNIX_DB_PASSWORD=$(openssl rand -hex 24)   # or in docker/.env
docker compose -f docker/compose.saas.yml up -d --build
docker compose -f docker/compose.saas.yml up -d --scale api=3 --scale worker=4   # more of either, any time
```

- **Organizations**: with `Mailnix__Tenancy__Mode=MultiTenant`, every signup founds its own organization and
  administers it (page *Team*): invite colleagues by link (optionally for one email address, valid for 7 days), make
  them admins, remove them. Nobody sees mailboxes, rules or users of another organization. The first account
  registered (or the bootstrap admin) runs the installation (page *System*: workers, mailbox distribution, queues,
  all organizations and their mailbox limits).
- **Workers** share the mailboxes through leases in the database: each takes about an equal share, a worker that
  shuts down frees its mailboxes for the others right away, and a crashed worker's mailboxes are taken over after the lease expires
  (`Mailnix__Worker__LeaseDuration`, 90 seconds). Runs started in the browser are queued in the database and picked
  up by the worker that holds the mailbox; live updates travel back through the database to every API container.
- **Startup**: the `init` container (`mailnix --initialize-only`) migrates the database, creates the seed data and the
  shared data protection keys, then exits; API and workers start after it and do not migrate themselves.
- **Self-hosted** installations need none of this: `AllInOne` mode with one organization is the default, and invitations
  (page *Team*) work there too, even with signup switched off.

## Configuration

All settings can be given as environment variables (`__` separates sections).

| Setting | Default | Description |
|---|---|---|
| `Mailnix__Mode` | `AllInOne` | `AllInOne`, `Api` (API + UI only) or `Worker` (mailbox processing only) |
| `Mailnix__Database__Provider` | `Sqlite` | `Sqlite` or `Postgres` |
| `Mailnix__Database__ConnectionString` | `Data Source=data/mailnix.db` | |
| `Mailnix__Database__AutoMigrate` | `true` | Apply database migrations on startup |
| `Mailnix__Auth__AllowRegistration` | `true` | Self-service signup (the first user can always register; invited users always can) |
| `Mailnix__Tenancy__Mode` | `SingleTenant` | `SingleTenant` (everybody in one organization, self-hosted) or `MultiTenant` (every signup founds its own organization, SaaS) |
| `Mailnix__Tenancy__DefaultMaxAccounts` | – | Mailbox limit for new organizations (unlimited without); the operator can change it per organization on the System page |
| `Mailnix__OAuth__Google__ClientId` / `ClientSecret` | – | OAuth app for Gmail and Google Workspace mailboxes (see [docs/oauth-setup.md](docs/oauth-setup.md)) |
| `Mailnix__OAuth__Microsoft__ClientId` / `ClientSecret` | – | OAuth app for Microsoft 365 and Outlook.com mailboxes |
| `Mailnix__OAuth__Microsoft__Tenant` | `common` | `common`, `organizations`, `consumers` or a tenant ID |
| `Mailnix__OAuth__RedirectBaseUrl` | the browser's address | Public address of mailnix for the redirect URI `…/oauth/callback`, when a proxy changes the host name |
| `Mailnix__DataProtection__MasterKey` | – | Master key (base64, from `mailnix --generate-master-key`) that encrypts the keys protecting stored mailbox passwords. Keep it outside the database and back it up separately: without it the stored passwords cannot be decrypted |
| `Mailnix__DataProtection__MasterKeyFile` | – | Alternatively, a file containing the master key, e.g. a Docker secret (`/run/secrets/mailnix_master_key`) |
| `Mailnix__DataProtection__KeyProtection` | `Auto` | `Auto` (= `MasterKey` when one is configured, otherwise `WindowsUser` on Windows and `None` elsewhere), `None`, `MasterKey`, `WindowsUser` (DPAPI, only the Windows account mailnix runs as can read the keys) or `WindowsMachine` (DPAPI, any account on this machine, e.g. for a Windows service). Keys stored the old way are replaced on the next start (users then sign in again); a wrong or missing master key stops the start instead |
| `Mailnix__Bootstrap__AdminEmail` / `AdminPassword` | – | Create this admin account on first start |
| `Mailnix__Mail__AllowInsecureConnections` | `false` | Allow unencrypted IMAP (passwords in clear text) |
| `Mailnix__Mail__BlockPrivateNetworks` | `false` | Refuse mail servers on private/local addresses (recommended for SaaS) |
| `Mailnix__Mail__ConnectTimeout` | `00:00:30` | Timeout for connecting and logging in |
| `Mailnix__RateLimits__MailConnectionsPerMinute` | `20` | Per-user limit for requests that open IMAP connections |
| `Mailnix__RateLimits__PreviewsPerMinute` | `60` | Per-user limit for rule previews |
| `Mailnix__RateLimits__AuthPerMinute` | `30` | Sign-in and signup attempts per IP address (behind a proxy, set `ASPNETCORE_FORWARDEDHEADERS_ENABLED` so the client's address is used) |
| `Mailnix__RateLimits__ApiRequestsPerMinute` | `600` | All API requests per user (or IP address when signed out) |
| `Mailnix__Worker__Enabled` | `true` | Watch mailboxes and execute rules (modes `AllInOne` and `Worker`) |
| `Mailnix__Worker__IdleTimeout` | `00:09:00` | Re-issue IMAP IDLE after this time |
| `Mailnix__Worker__PollInterval` | `00:02:00` | Check interval for servers without IDLE |
| `Mailnix__Worker__ConfirmationExpiry` | `1.00:00:00` | Runs waiting for confirmation expire after this time |
| `Mailnix__Worker__SearchPushdown` | `true` | Let the mail server preselect messages for manual and scheduled runs (IMAP SEARCH; results are always re-checked by mailnix) |
| `Mailnix__Worker__ScheduleCheckInterval` | `00:00:30` | How often due scheduled rules and Trash purges are looked for |
| `Mailnix__Worker__MaxScheduledRunMessages` | `5000` | Candidates a scheduled run checks at most (newest first) |
| `Mailnix__Worker__ActionLogRetention` | `90.00:00:00` | Runs and their action log are kept this long (older ones can no longer be undone); the stats keep their daily counters |
| `Mailnix__Worker__PurgeInterval` | `1.00:00:00` | How often Trash is checked when purging is switched on for an account |
| `Mailnix__Worker__ReconcileInterval` | `00:00:30` | How often a worker renews its leases and picks up new, changed or freed mailboxes |
| `Mailnix__Worker__LeaseDuration` | `00:01:30` | A worker that stops renewing loses its mailboxes after this time |
| `Mailnix__Worker__MaxAccountsPerWorker` | `500` | Mailboxes one worker watches at most |
| `Mailnix__Worker__QueuePollInterval` | `00:00:02` | How often a worker looks for runs queued by an API process |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | `false` | Set to `true` behind a TLS-terminating reverse proxy |

## Licensing

mailnix is MIT licensed. Dependencies are limited to permissive licenses compatible with that
(MIT, ISC, BSD, Apache-2.0, PostgreSQL, …). Build-only tooling may additionally be MPL-2.0 (e.g. lightningcss
inside Vite), which is never shipped. Both license gates run in CI; the Docker image contains
`wwwroot/THIRD-PARTY-NOTICES.md` for the frontend.
