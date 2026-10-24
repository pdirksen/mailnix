# Setting up Google and Microsoft 365 sign-in

mailnix connects to Gmail and Microsoft 365 / Outlook.com mailboxes with OAuth: the mailbox owner signs in at
Google or Microsoft and allows mailnix to access the mailbox over IMAP. mailnix never sees the password; it stores
a revocable refresh token, encrypted like mailbox passwords.

Every mailnix installation registers its own OAuth app with each provider. Until it does, the "Add account" page shows
Google and Microsoft as "Not set up". This takes about 10 minutes per provider. The consoles change their layout from
time to time; the names below match them as of autumn 2026.

## Redirect URI

Both providers send the browser back to mailnix at

```
<address of mailnix>/oauth/callback
```

Register every address you open mailnix under, exactly (scheme, host, port):

| Setup | Redirect URI |
|---|---|
| Windows app (`mailnix.exe`) | `http://localhost:5000/oauth/callback` |
| Docker (compose file) | `http://localhost:8080/oauth/callback` |
| Development backend | `http://localhost:5080/oauth/callback` |
| Development frontend (`npm run dev`) | `http://localhost:5173/oauth/callback` |
| Public server | `https://mail.example.com/oauth/callback` |

Both providers accept `http://localhost`; every other address needs HTTPS. Behind a reverse proxy that changes the host
name, set `Mailnix__OAuth__RedirectBaseUrl` to the public address (e.g. `https://mail.example.com`).

## Google (Gmail, Google Workspace)

1. Open the [Google Cloud console](https://console.cloud.google.com/) and create a project, e.g. "mailnix".
2. **APIs & Services → Library**: enable the **Gmail API**. (IMAP does not use it, but its scope can then be picked in
   step 4.)
3. **Google Auth Platform → Branding**: enter an app name ("mailnix") and your email address as support and developer
   contact.
4. **Audience**:
   - Google Workspace, only mailboxes of your own organization: choose **Internal**. No review by Google is needed.
   - Personal Gmail addresses: choose **External** and leave the app in **Testing**. Add every Gmail address that will
     sign in under **Test users** (up to 100).
5. **Data access → Add or remove scopes**: add `https://mail.google.com/` (listed as "Read, compose, send, and
   permanently delete all your email from Gmail"). mailnix needs this scope because IMAP only works with full mailbox
   access; mailnix itself never sends or permanently deletes mail through it, except the Trash purge you switch on yourself.
6. **Clients → Create client**: type **Web application**, name "mailnix", and add the redirect URIs from the table above.
7. Copy the **Client ID** and the **Client secret**.

Limits of the "Testing" status (personal Gmail): Google ends the access after **7 days**, and the account then shows
"Reconnect required"; one click on **Reconnect with Google** fixes that. Publishing the app for everyone needs Google's
verification of `https://mail.google.com/`, a restricted scope, which includes a paid security assessment. For a
personal installation, staying in Testing is the practical choice.

## Microsoft (Microsoft 365, Outlook.com)

1. Open the [Microsoft Entra admin center](https://entra.microsoft.com/) (or the Azure portal) → **App registrations →
   New registration**.
   - Name: "mailnix".
   - Supported account types: **Accounts in any organizational directory and personal Microsoft accounts** works for
     everyone. To allow only your organization, choose single tenant and set `Mailnix__OAuth__Microsoft__Tenant` to your
     tenant ID (default `common`; also possible: `organizations`, `consumers`).
   - Redirect URI: platform **Web**, the first URI from the table above. Add the others under **Authentication**.
2. **Certificates & secrets → New client secret**. Copy the **Value** right away (not the "Secret ID"; the value is
   shown only once). Note the expiry date: mailnix cannot sign in to any Microsoft mailbox after it, until you create a
   new secret and configure it.
3. **API permissions → Add a permission**:
   - **APIs my organization uses → Office 365 Exchange Online → Delegated permissions → IMAP.AccessAsUser.All**.
   - **Microsoft Graph → Delegated permissions**: `offline_access`, `openid`, `email`, `profile`.

   If your organization does not let users consent to apps themselves, an administrator has to click **Grant admin
   consent**.
4. Copy the **Application (client) ID** from the overview page.
5. IMAP has to be allowed for the mailbox:
   - Microsoft 365: admin center → **Users → Active users →** the user **→ Mail → Manage email apps → IMAP**.
   - Outlook.com: Outlook settings **→ Mail → Forwarding and IMAP → Let devices and apps use IMAP**.

## Configure mailnix

Give mailnix the IDs and secrets, as environment variables (or in `appsettings.json`), and restart it:

```
Mailnix__OAuth__Google__ClientId=...apps.googleusercontent.com
Mailnix__OAuth__Google__ClientSecret=...
Mailnix__OAuth__Microsoft__ClientId=00000000-0000-0000-0000-000000000000
Mailnix__OAuth__Microsoft__ClientSecret=...
# Mailnix__OAuth__Microsoft__Tenant=common
# Mailnix__OAuth__RedirectBaseUrl=https://mail.example.com
```

- **Windows app**: `set Mailnix__OAuth__Google__ClientId=...` (and the others) in the console before starting
  `mailnix.exe`, or put them into `appsettings.json` next to it under `"Mailnix": { "OAuth": { ... } }`.
- **Docker**: add them to the `environment:` section of `docker/compose.selfhosted.yml`, or better, to an `.env` file
  next to it that is not committed.
- **Development**: keep them out of the repository with user secrets:

  ```bash
  cd backend/src/Mailnix.Api
  dotnet user-secrets set "Mailnix:OAuth:Google:ClientId" "..."
  dotnet user-secrets set "Mailnix:OAuth:Google:ClientSecret" "..."
  ```

The client secrets identify your installation to Google and Microsoft. Treat them like passwords, and create new ones
if they leak.

## Adding a mailbox

**Accounts → Add account → Google** (or **Microsoft 365**) → **Sign in with Google**. After the provider's sign-in,
mailnix opens the new account's settings: it watches the Inbox, and Trash, Spam and Archive were detected. Gmail's
Archive is "All Mail", because archiving in Gmail means removing the Inbox label.

In Gmail, folders are labels. Rules show "Move to label" (the message leaves the Inbox and gets the label) and "Add
label" (the message keeps its labels and gets one more), and undo puts labels back the same way.

When a provider ends the access (password change, revoked app, the 7-day limit above), the account shows "Reconnect
required" and offers **Reconnect with Google / Microsoft**. Signing in again with the same mailbox restores it, with all
its rules.
