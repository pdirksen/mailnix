# mailnix brand

A letter with wings: the "-nix" in the name nods to the Gaulish comics, whose heroes wear winged helmets.
Only that generic motif is used, no characters or lettering from the comics.

| File | Use |
|---|---|
| `logo-mark.svg` | The mark (app icon, favicon). Source of every other icon. |
| `logo-mark-512.png` | The mark as a bitmap, e.g. for presentations. |
| `logo-lockup-light.svg` / `logo-lockup-dark.svg` | Mark with the wordmark, on light or dark backgrounds. |

Colors (the same in light and dark mode):

| | Hex | Note |
|---|---|---|
| Tile, lines | `#171717` | The app's primary color (neutral 900) |
| Envelope | `#fafafa` | |
| Wings | `#eda100` | Gold from the app's chart palette |

- Smallest size: 16 px (favicon). The feather lines disappear below about 48 px; the silhouette carries the mark.
- On dark backgrounds, give the black tile a faint light ring (the app uses 1 px white at 10 % opacity).
- The wordmark is "mailnix" in lowercase, in the app's system font, semibold.

## Changing the logo

Edit `frontend/src/assets/brand/logo-mark.svg` (the single source), then run `npm run brand:icons` in `frontend/`.
It regenerates the favicon, the home-screen and manifest icons in `frontend/public/`, the Windows icon
`backend/src/Mailnix.Api/mailnix.ico` and the files in this folder.
