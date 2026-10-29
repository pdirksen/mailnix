# Front page

The product page at <https://cloudsteps.de/mailnix/>. Upload the content of this folder there as is; it needs no build
step and loads nothing from other sites.

| File | Content |
|---|---|
| `index.html` | The page: HTML, CSS and a small script in one file. English in the HTML, German in the script (browser language, switch EN/DE). |
| `assets/*.jpg` | App screenshots with made-up data, light and dark (shown to match the visitor's color scheme). |
| `assets/favicon.svg`, `assets/apple-touch-icon.png`, `assets/logo-mark.svg` | Copies of the brand files (`docs/brand/`, `frontend/public/`). |

## Downloads

The download links name version 1.4.0 in the HTML. When the page loads, the script reads
`https://cloudsteps.de/u/mailnix/releases/latest.json` and shows the files of the newest stable version from it;
`scripts/publish-release.ps1` uploads that file with every stable release (`-LatestOnly` for a release that is on the
server already). If it cannot be read, the links in the HTML stay. The page has to run on `https://cloudsteps.de` for
that (same origin; other origins are blocked by the browser).

## Refreshing the screenshots

After UI changes, from the repository root:

```powershell
cd frontend; npm run build; cd ..
Remove-Item backend\src\Mailnix.Api\wwwroot -Recurse -ErrorAction SilentlyContinue
Copy-Item frontend\dist backend\src\Mailnix.Api\wwwroot -Recurse

# Terminal 1: the demo instance on http://localhost:5090 (made-up mailbox, never a real mail server)
cd backend
dotnet test --project tests/Mailnix.Integration.Tests -- --filter-method "*Run_the_demo_instance" --explicit only

# Terminal 2: waits until the demo is ready, then writes assets/*.jpg
cd frontend
npm run frontpage:screenshots
```

The demo (`backend/tests/Mailnix.Integration.Tests/DemoHost.cs`) signs up `alex@example.com` / `demo password 123`,
adds an account with fake mail, a few rules and 30 days of statistics, and stops after 30 minutes
(`MAILNIX_DEMO_MINUTES`) or with Ctrl+C. You can also look around in it with a browser.
