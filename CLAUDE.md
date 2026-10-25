# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

mailnix is a web-based, imapfilter-like mail sorter. It has a .NET 10 backend in `backend/` and a Vue 3 SPA in
`frontend/`. It runs self-hosted (one process, SQLite) or as SaaS (separate API and worker processes on PostgreSQL).

## Commands

Backend (run from `backend/`):

```bash
dotnet run --project src/Mailnix.Api                  # http://localhost:5080, SQLite in src/Mailnix.Api/data/
dotnet build                                          # also regenerates openapi/mailnix.json
dotnet test --solution Mailnix.slnx                   # all tests (xUnit v3 on Microsoft.Testing.Platform)
dotnet test --project tests/Mailnix.Integration.Tests -- --filter-class "*LeaseTests"
dotnet test --project tests/Mailnix.Integration.Tests -- --filter-method "*Some_test_name"
dotnet tool restore && dotnet nuget-license -i Mailnix.slnx -t -a licenses-allowed.json
```

Migrations: there are two separate EF contexts. Add every migration to both:

```bash
dotnet ef migrations add <Name> --project src/Mailnix.Infrastructure --context SqliteMailnixDbContext --output-dir Migrations/Sqlite
dotnet ef migrations add <Name> --project src/Mailnix.Infrastructure --context PostgresMailnixDbContext --output-dir Migrations/Postgres
```

Frontend (run from `frontend/`):

```bash
npm run dev                         # http://localhost:5173, proxies /api to :5080
npm run gen:api                     # regenerate src/api/schema.d.ts from backend/openapi/mailnix.json after API changes
npm run type-check && npm run lint && npm test
npx vitest run src/pages/auth/RegisterPage.spec.ts
npm run license:check
npm run test:e2e                    # Playwright against a running instance on :5080
npm run brand:icons                 # after changing src/assets/brand/logo-mark.svg: favicon, PNG icons, mailnix.ico, docs/brand
```

E2E needs the backend to serve the built SPA. Run `npm run build` and copy `frontend/dist` to
`backend/src/Mailnix.Api/wwwroot` (git-ignored), then start the backend. `e2e/team.spec.ts` is skipped unless the
instance runs with `Mailnix__Tenancy__Mode=MultiTenant`.

Windows standalone build: `scripts/build-windows.ps1` (output in `artifacts/`); start it with `scripts/run-windows.ps1`.

## Architecture

**Run modes** (`Mailnix:Mode`, wired in `Mailnix.Api/Program.cs`):
- `AllInOne`: API, SPA and mailbox workers in one process.
- `Api`: API and SPA only.
- `Worker`: mailbox processing only.

All three modes use the same executable and the same Docker image. `mailnix --initialize-only` migrates the database,
seeds data and creates the data protection key ring, then exits. Multi-process deployments use it as a one-shot init
step; see `docker/compose.saas.yml`.

**Projects** (`backend/src`):
- `Mailnix.Core`: the pure rule engine, with no IO.
  - The rule JSON model and the field catalog.
  - `RuleEvaluator`: evaluates conditions and returns an explanation trace.
  - `ActionPlanner`: non-terminal actions merge; only the first terminal action by priority runs, and the others are
    reported as conflicts.
  - Folder templates, and `MessageFacts` (newsletter, auto-reply, sender trust and similar facts derived from the headers).
  - `SearchPlanner`: pushes conditions down to IMAP SEARCH. The pushed query must be a *superset*, and every
    candidate is re-evaluated locally. Property tests against `ImapSearchModel` guard this.
- `Mailnix.Mail`: MailKit adapters behind `IMailboxSessionFactory` / `IMailboxSession`.
  - IMAP login with a password or XOAUTH2.
  - An own small OAuth2 + PKCE client for Google and Microsoft.
- `Mailnix.Infrastructure`:
  - `MailnixDbContext` with the Sqlite and Postgres subclasses, and the migrations.
  - Identity (`AppUser`, roles Admin / TenantAdmin / User).
  - Data protection: keys are stored in the database and encrypted per `KeyProtectionOptions`, e.g. with a master
    key.
  - `DatabaseInitializer`.
- `Mailnix.Worker`:
  - `MailboxSupervisor` runs one `MailboxWatcher` per leased account (IMAP IDLE or polling).
  - `RunProcessor`, `UndoProcessor`, `TrashPurger`, `RuleScheduler` (Cronos), `StatsRecorder`.
  - `LeaseManager`: `MailboxLease` rows with conditional updates and fair-share balancing between worker processes.
- `Mailnix.Api`: minimal-API endpoint groups (`*Endpoints.cs` per area), SignalR `LiveHub` at `/hubs/live`, and it
  serves `wwwroot`.

**Jobs go through the database.**
- Manual runs, confirmations of capped runs and undo are queued as `Run` rows. The watcher that holds the account's
  lease executes them.
- The supervisor polls the queue, so runs queued by a separate API process are picked up.
- Runs over an account's `BlastRadiusLimit` stop as `AwaitingConfirmation`, with the plan stored on the run.

**Live events** go through `ILiveNotifier`:
- `AllInOne`: `HubLiveNotifier` sends straight to the hub.
- `Worker`: `OutboxLiveNotifier` writes `LiveEventRecord` rows.
- `Api`: `LiveEventRelay` polls those rows and sends them to its own SignalR connections.

**Tenancy:**
- `MailnixDbContext.TenantFilter` drives global query filters on accounts, rules, lists and runs.
  `TenantScopeMiddleware` sets it from the `mailnix:tenant` cookie claim.
- Code that must cross tenants, such as the System endpoints and the worker, uses `IgnoreQueryFilters()` or runs
  without a filter.
- `Mailnix:Tenancy:Mode` is `SingleTenant` (default, one tenant) or `MultiTenant` (every signup creates a tenant;
  invitations add users to an existing one).

**Safety model:** rules never expunge.
- Delete means moving the message to Trash.
- Permanent deletion happens only through the opt-in per-account Trash purge.
- Every action is written to `ActionLog` with what undo needs (target UID from UIDPLUS, previous flags).
- Folders are opened read-only until an action actually executes.

**Frontend:**
- Stack: Pinia stores and `src/api/client.ts`, an openapi-fetch client typed by the generated `schema.d.ts`.
  `unwrap()` throws `ApiError`, which carries the server's `errorCode`.
- UI: shadcn-vue components copied into `src/components/ui`, Tailwind 4, vue-i18n with `src/i18n/en.json` and
  `de.json`. Every UI text exists in both languages.
- Routes are in `src/router/index.ts`, with meta `requiresAuth`, `requiresAdmin`, `requiresTenantAdmin` and
  `guestOnly`. The sidebar entries are in `src/layouts/navigation.ts`.
- Unit specs mock the API with `src/test/apiMock.ts`. Specs that build their own router from `routes` do not run the
  navigation guard; the guard is only on the exported `router`.

## Conventions and constraints

- **Analyzers:** `TreatWarningsAsErrors` and `AnalysisLevel latest-recommended` are on for all backend projects.
  Logging uses `[LoggerMessage]` source-generated methods. Avoid analyzer-flagged names, e.g. enum members called
  `Single`/`Multi` (CA1720) or type names ending in `Flag` (CA1711).
- **Non-GET `/api` requests** need the header `X-Requested-With: mailnix` (CSRF). The auth cookie is
  `SameSite=Strict`, so OAuth providers redirect to the SPA route `/oauth/callback`, which then posts to the API.
- **Error responses** are problem details with an `errorCode` extension. The frontend translates known codes through
  i18n and falls back to the server's title.
- **Licenses:** all dependencies must be MIT-compatible: MIT, ISC, BSD, 0BSD, Apache-2.0, PostgreSQL, Unlicense or
  CC0. Build-only tooling may also be MPL-2.0, CC-BY or Python-2.0. GPL, LGPL, AGPL, MPL (for shipped code) and
  commercial or source-available packages are not allowed; `frontend/scripts/check-licenses.mjs` and
  `backend/licenses-allowed.json` enforce this. Package versions are pinned centrally in
  `backend/Directory.Packages.props`.
- **Integration tests:**
  - `MailnixAppFactory` boots the real API on a throwaway SQLite file, with workers off unless
    `Mailnix:Worker:Enabled=true`. `SharingDatabaseWith()` simulates a second process.
  - Mail servers are faked with `FakeMailServer` (in-memory `IMailboxSessionFactory`) or `FakeImapServer` (a real IMAP
    socket).
- **Real-mailbox tests:** these run only when `test-email-credentials.ini` exists in the repo root (git-ignored;
  never print or commit it).
  - Read-only everywhere, except inside `INBOX.mailnix-test` and its subfolders, which are filled via IMAP APPEND.
  - Never send a wrong password to the real server (fail2ban).
  - E2E tests pause accounts that watch real folders and delete all their accounts afterwards
    (`frontend/e2e/support.ts`). After e2e runs, check the dev database for leftover accounts.
- **Configuration:** every setting is listed in README.md under "Configuration" (environment variables use `__`
  between sections). Add new options there.
- **Untested so far:** the Postgres migrations and `docker/compose.saas.yml` have never run against a real
  PostgreSQL or Docker. Google and Microsoft OAuth have not been tested against real accounts.
