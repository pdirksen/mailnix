<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/brand/logo-lockup-dark.svg">
    <img src="docs/brand/logo-lockup-light.svg" alt="mailnix" width="230">
  </picture>
</p>

# mailnix

Rule-based mail sorting with a web UI, in the spirit of imapfilter: connect IMAP, Google or Microsoft 365
mailboxes, build rules visually, preview what they would do, and let mailnix move, flag or trash incoming mail.

> Status: early development. Auth, IMAP accounts, the rule engine, named lists, the visual rule builder with a
> preview against real mail, rule execution (new mail via IDLE, "run now", confirmation of large runs, undo,
> live status), scheduled rules with server-side preselection, the optional Trash purge, OAuth sign-in for Gmail
> and Microsoft 365, the dashboard and stats, the SaaS mode (organizations, separate API and worker processes) and
> personal settings (time zone, own addresses, password, two-factor sign-in) are in place. Not yet verified against real systems: Google/Microsoft OAuth, PostgreSQL and the SaaS Docker setup.

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

The first account you register becomes the administrator (unless a bootstrap admin is configured). Outside development
it needs the setup code that mailnix logs at startup while no account exists, so nobody who reaches a fresh server
first can take it. Further users join by invitation (page *Team*), unless `Mailnix__Auth__AllowRegistration` is on.

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
executable, whatever the working directory is (another folder: `Mailnix__DataDirectory`). Settings that should survive
an update go into `data\appsettings.json`, not the `appsettings.json` next to the executable, which an update replaces.
Rebuilding keeps an existing `data\` folder, and `-Zip` never includes it. The encryption keys in it are protected with
Windows DPAPI for the account that runs mailnix, so a copied `data\` folder is useless elsewhere; to run mailnix as a Windows service under another account, set
`Mailnix__DataProtection__KeyProtection=WindowsMachine` before its first start there (or enter the mailbox passwords again).
On Linux, macOS and in Docker, configure a master key (see below); without one, the keys are stored unencrypted in
the database and mailnix logs a warning at startup.

## Windows desktop app

```powershell
.\scripts\build-windows.ps1 -Desktop    # artifacts\windows-desktop\win-x64; -Runtime win-arm64, -Zip, -Output <dir>
```

For one person on their own PC, and what the Windows installer installs (see below). `mailnix-desktop.exe` runs
mailnix in the background with an icon in the notification area. A click on the icon opens mailnix in the browser. Its menu can
also start mailnix with Windows, open the data folder and the log, and restart or quit mailnix.

- mailnix listens on http://localhost:5480 only. For another port, put
  `{ "Mailnix": { "Desktop": { "Port": 5481 } } }` into `appsettings.json` in the data folder. If the port is taken,
  that run uses a free one and says so. Sign-in with Google or Microsoft needs the port that the OAuth app has
  registered.
- It runs in the environment `Desktop` (`appsettings.Desktop.json`), so only the first user can register.
- Data is in `data\` next to it. An installed copy keeps it in `%LocalAppData%\mailnix-data` instead, which updates
  and uninstalling leave alone.
- Logs are in `logs\` in the data folder: `mailnix.log`, `mailnix.previous.log` for the run before, and
  `desktop.log` for the desktop app's own errors.
- Quitting closes mailnix's standard input (`Mailnix__StopWhenInputCloses`), so mailnix finishes cleanly.
  `taskkill /im mailnix-desktop.exe` without `/f` does the same.

### Installer and updates

Releases include `mailnix-<version>-win-x64-setup.exe` (made with [Velopack](https://velopack.io), MIT). It installs
for the current user, without administrator rights, into `%LocalAppData%\mailnix`, adds shortcuts on the desktop and
in the Start menu and starts mailnix. Data goes to `%LocalAppData%\mailnix-data`, which updates and uninstalling leave
alone (delete it yourself to remove everything). Uninstalling also removes the "start with Windows" entry.

An installed copy looks for updates a minute after it starts and then every 12 hours, and on request (*Check for
updates* in the menu). A newer version is downloaded in the background (usually as a small delta) and announced; *Install
update* in the menu stops mailnix cleanly, installs it and starts the new version. An update that was downloaded but
not installed yet is installed at the next start of the app.

The update address is built into each release (`release.ps1 -UpdateUrl`); `{ "Mailnix": { "Desktop": { "UpdateUrl":
"https://…" } } }` in `appsettings.json` in the data folder overrides it, and may also be a folder or a file share.
Without an address, an installed copy does not look for updates.

Releases built without a code-signing certificate (see "Releases") are unsigned: Windows SmartScreen warns before the
first start of the installer ("More info" → "Run anyway"), and nothing but the HTTPS download proves where an
update comes from.

## Standalone Linux and macOS app

```bash
./scripts/build-unix.sh                # --runtime linux-arm64 | linux-musl-x64 | osx-arm64 | ..., --archive, --output <dir>
./scripts/run-unix.sh                  # builds if needed, starts it, opens the browser; --build, --urls, --no-browser
```

The same build as the Windows app, for the machine it runs on unless `--runtime` says otherwise:
`artifacts/linux/linux-x64/mailnix` or `artifacts/macos/osx-arm64/mailnix`, with `wwwroot/` and `data/` next to it.
`--archive` writes a `.tar.gz` without `data/`. There is no DPAPI, so create a master key before the first start:

```bash
./mailnix --generate-master-key > ~/.mailnix-master.key && chmod 600 ~/.mailnix-master.key
Mailnix__DataProtection__MasterKeyFile=~/.mailnix-master.key ./mailnix
```

macOS builds made on another OS are not code-signed, and Apple Silicon runs only signed executables: sign it on the
Mac with `codesign --force --sign - mailnix`. A downloaded archive also needs `xattr -dr com.apple.quarantine <folder>`.

## Releases

Versions follow [semantic versioning](https://semver.org). A release is built on a Windows machine from a clean
checkout of the commit to release:

```powershell
.\scripts\release.ps1 1.4.0                              # 1.5.0-beta.1: a pre-release
.\scripts\release.ps1 1.4.0 -NoTag -Runtimes win-x64     # trial run: no tag, uncommitted changes allowed
.\scripts\publish-release.ps1 1.4.0                      # upload to the download server (-WhatIf: only list the files)
```

It runs the backend and frontend tests (not the e2e tests: run those before), builds the standalone apps for
`win-x64`, `win-arm64`, `linux-x64` and `linux-arm64` with that version (the Linux ones in Git Bash), writes them with a
`SHA256SUMS` file to `artifacts/releases/<version>/` and tags the commit `v<version>`. Push the tag yourself.

For each Windows runtime it also builds the desktop app and packs it with Velopack (`vpk`, a local dotnet tool in
`backend/`): the installer goes into the release folder, the update packages into the feed folder
`artifacts/releases/updates/` (`-Feed` for another one). Keep that folder between releases: it lists every version,
and the next release builds its delta updates from it. Installed apps look for updates at `-UpdateUrl` (by default
`https://cloudsteps.de/u/mailnix/releases/updates`, where `publish-release.ps1` puts the feed).
Each runtime is its own update channel (`win-x64`, `win-arm64`); pre-releases go to `win-x64-beta` and so on, so
installed stable versions never get a pre-release. `-NoInstaller` skips all this. macOS builds have to be signed on a Mac: build them there with
`build-unix.sh --archive`, add them to the folder and update `SHA256SUMS`.

Code signing: with a certificate, pass the signing command with `-SignTemplate` (or the environment variable
`MAILNIX_SIGN_TEMPLATE`); `{{file}}` stands for the quoted path of each file, e.g.
`signtool sign /a /fd sha256 /tr http://timestamp.digicert.com /td sha256 {{file}}` (AzureSignTool or osslsigncode work
the same way). It signs `mailnix.exe` and `mailnix-desktop.exe` before they are zipped, and Velopack signs the
installer, the updater and the other files of the update packages with it. Without it the release is unsigned (with a
warning): then whoever gets hold of the upload password can ship an update to every installed desktop app.

`publish-release.ps1 <version>` uploads a release over FTPS (TLS required) to
`ftp://deploy@rainbow.cloudsteps.de/mailnix/releases`, served at `https://cloudsteps.de/u/mailnix/releases`
(`-FtpUrl` and `-WebUrl` for other servers; `rainbow.cloudsteps.de` because the FTP server's certificate is issued for
that name):

- `<version>/`: the release folder, checked against its `SHA256SUMS` first. `SHA256SUMS` goes up last; a version
  whose folder on the server has it counts as published and is not uploaded again (`-Force` overrides this).
- `updates/`: the feed. Packages already on the server are skipped; the index files (`releases.<channel>.json` and
  the like) go up last, so installed apps never see a version whose packages are still missing.
- `latest.json`: the newest stable version and its files, for the front page ([docs/frontpage](docs/frontpage/README.md)).
  Uploaded last, only for stable versions, and never replaced by an older one; `-LatestOnly` uploads just this file
  for a version that is published already.

The password comes from the environment variable `MAILNIX_FTP_PASSWORD` or is asked for; it is never stored. A failed
sign-in stops the script at once, without retrying. Afterwards it checks every uploaded file on the web server and
that the feed lists the new version.

Every other build (`dotnet run`, the build scripts, Docker) is a development build `0.0.0-dev` unless `MailnixVersion`
is set: as an environment variable, with `-p:MailnixVersion=1.4.0`, or with `--build-arg MailnixVersion=1.4.0` for
the Docker image. Administrators see the version on the *System* page, and mailnix logs it at startup.

## Gmail and Microsoft 365

Each installation registers its own OAuth app with Google and/or Microsoft; see [docs/oauth-setup.md](docs/oauth-setup.md)
for the steps and the settings. Until then, those providers show as "Not set up" when adding an account.

## AI assistants (MCP)

mailnix is an [MCP](https://modelcontextprotocol.io) server at `/mcp` (Streamable HTTP), so AI assistants such as
Claude can work with a user's mailboxes: explain the rules, try new ones on real mail (preview), draft them, look at
statistics, activity and runs, start runs and undo them. *Settings → AI assistants* shows the address and manages the
access.

- **Claude.ai and Claude Desktop:** add a custom connector with the address (e.g. `https://mail.example.com/mcp`).
  Claude registers itself, the user signs in to mailnix and agrees on the page `/connect` (read only, or also drafting
  rules and runs). mailnix is its own OAuth 2.1 authorization server for this: metadata under
  `/.well-known/oauth-protected-resource` and `/.well-known/oauth-authorization-server`, dynamic client registration,
  authorization codes with PKCE, access tokens for one hour and rotating refresh tokens for 60 days. The connector must
  be able to reach mailnix, so this needs a public address (`Mailnix__PublicUrl`) with https.
- **Claude Code, VS Code and others:** create a personal access token (read only or read and draft, optional expiry);
  the settings page shows the token once, with the configuration for these clients, e.g.
  `claude mcp add --transport http mailnix https://mail.example.com/mcp --header "Authorization: Bearer mnx_pat_…"`.

What assistants cannot do, whatever the access: switch a rule live (rules they create or change stay disabled or in
dry run until the user enables them in the app; live rules cannot be changed through MCP), confirm runs that stopped
at the account's threshold, delete rules, accounts or mail, see passwords, or run the Trash purge. Rules and runs show
which token or app changed or started them. Tokens are stored as hashes; changing the password, resetting it and
*Sign out other devices* revoke all tokens and connections.

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
| `Mailnix__PublicUrl` | – | Public address of mailnix (e.g. `https://mail.example.com`), used for links in emails (password reset, invitations). Links in emails never use the request's host name, so without this setting (or `Mailnix__OAuth__RedirectBaseUrl`) no password-reset emails are sent. Also the address MCP clients are told to use (`<PublicUrl>/mcp`, and the OAuth issuer); without it, the address the request came to |
| `Mailnix__Email__Host` / `Port` | – / by security | SMTP server for mailnix's own emails (password reset, invitations, security notices). Port defaults to 587 (`StartTls`), 465 (`SslOnConnect`) or 25 (`None`). Without a host, every email feature is off and "Forgot password?" is hidden |
| `Mailnix__Email__Security` | `StartTls` | `StartTls`, `SslOnConnect` or `None` (e.g. a relay on the local network) |
| `Mailnix__Email__Username` / `Password` | – | SMTP sign-in; leave empty for relays that need none |
| `Mailnix__Email__FromAddress` / `FromName` | – / `mailnix` | Sender of the emails; the address is required for sending |
| `Mailnix__Email__InvitationsPerDay` | `20` | Invitations an organization may send by email within 24 hours (links to pass on yourself are not limited) |
| `Mailnix__StopWhenInputCloses` | `false` | Stop when standard input closes; the Windows desktop app runs mailnix this way |
| `Mailnix__Database__Provider` | `Sqlite` | `Sqlite` or `Postgres` |
| `Mailnix__DataDirectory` | `data` | Folder for the SQLite database and an optional `appsettings.json` whose settings rank above the `appsettings.json` next to the executable (environment variables and the command line still win), so they survive updates. Relative to the executable's folder in published builds, otherwise to the working directory. Can't be set in that file itself |
| `Mailnix__Database__ConnectionString` | SQLite: `mailnix.db` in the data directory | Required for Postgres |
| `Mailnix__Database__AutoMigrate` | `true` | Apply database migrations on startup |
| `Mailnix__Auth__AllowRegistration` | `false` | Self-service signup for anyone who reaches the server (the first user can always register; invited users always can). Needed for SaaS, where every signup founds an organization |
| `Mailnix__Auth__RequireSetupCode` | `true` | The first account (the administrator) needs the setup code that mailnix logs at startup while no account exists. Off in development and in the Windows desktop app, which only this computer can reach |
| `Mailnix__Auth__SetupCode` | random | A fixed setup code, e.g. when several API processes run (each would log its own random one) |
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
| `Mailnix__Mail__BlockPrivateNetworks` | `MultiTenant`: `true`, else `false` | Refuse mail servers on private, local and similar addresses, so users cannot probe the internal network |
| `Mailnix__Mail__ConnectTimeout` | `00:00:30` | Timeout for connecting and logging in |
| `Mailnix__RateLimits__MailConnectionsPerMinute` | `20` | Per-user limit for requests that open IMAP connections |
| `Mailnix__RateLimits__PreviewsPerMinute` | `60` | Per-user limit for rule previews |
| `Mailnix__RateLimits__AuthPerMinute` | `30` | Sign-in and signup attempts per IP address (behind a proxy, set `Mailnix__TrustedProxies` so the client's address is used) |
| `Mailnix__RateLimits__ApiRequestsPerMinute` | `600` | All API requests per user (or IP address when signed out) |
| `Mailnix__RateLimits__McpRequestsPerMinute` | `120` | Requests to the MCP endpoint per token or connected app |
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
| `Mailnix__TrustedProxies` | – | Behind a TLS-terminating reverse proxy: its addresses or networks, comma-separated (e.g. `172.16.0.0/12` for a proxy container on the same Docker host). Only their `X-Forwarded-For`/`-Proto` headers count, so cookies are marked Secure and rate limits see the client's address |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | `false` | The ASP.NET Core switch: trusts `X-Forwarded-*` from every sender (mailnix warns at startup). Use `Mailnix__TrustedProxies` instead, or make sure only the proxy can reach the port |
| `AllowedHosts` | `*` | Host names mailnix answers to, `;`-separated (e.g. `mail.example.com;localhost`); others get 400. The desktop app only answers to `localhost`, against DNS rebinding |

## Licensing

mailnix is MIT licensed. Dependencies are limited to permissive licenses compatible with that
(MIT, ISC, BSD, Apache-2.0, PostgreSQL, …). Build-only tooling may additionally be MPL-2.0 (e.g. lightningcss
inside Vite), which is never shipped. Both license gates run in CI; the Docker image contains
`wwwroot/THIRD-PARTY-NOTICES.md` for the frontend.
