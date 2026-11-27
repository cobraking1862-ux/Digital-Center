# Download Workflows (14.0.0)

## Site ownership

Download workflows are assigned independently to Lookup Sites. A stored workflow assignment is not sufficient authorization to run it: the dispatcher also checks the stable Lookup Site ID and the source URL.

The built-in BollyFlix Lookup Site uses:

- Site ID: `builtin-bollyflix`
- Lookup URL: `https://new1.bollyflix.tours/?s={imdb_id}`
- Workflow: `BigOne` (available only on this site)

BigOne is **not** assigned automatically. Select it explicitly in Maintenance → Lookup Sites. Other Lookup Sites expose only **None (Lookup-only)**.

There is no independent BigO workflow implementation in this repository at v14.0.0. BigO is not shown as an option and no BigO behavior is inferred. Stored unrecognized assignments are preserved but are not executed.

## BigOne route

BigOne recognizes the supplied BollyFlix listing and title-page structures. It parses the 480p, 720p, 1080p, and 2160p sections independently and associates only each section's own Google Drive-labelled FastDLServer anchor with that quality.

Only the HTTPS host `dl.fastdlserver.site` is an eligible quality route. Linksmod is not fetched as a fallback. If the expected FastDLServer route is missing, redirects unexpectedly, or cannot be verified, BigOne returns a browser hand-off.

On the file-host page BigOne recognizes the Login To DL, Instant DL, FAST CLOUD / ZIPDISK, Telegram File, and GoFile [Multiup] labels, but follows only the root-relative `/cloud/...` route. It never automates login, credentials, CAPTCHA, JavaScript challenges, DRM, rate-limit bypass, Telegram, or GoFile.

The cloud landing page is treated as an intermediate selection page. Only its Cloud Resume Download and Quick Download (Ready) actions are considered, and only after their destinations pass the explicit HTTPS host policy. A final destination is handed to IDM only after its bounded response check returns non-HTML downloadable evidence (a supported binary content type or an attachment with a filename). A filename or extension by itself is not sufficient.

## Redirect and host policy

BigOne uses an HTTP client configured with automatic redirects disabled. It handles a bounded number of redirects one at a time and validates each next URI before making another request. It rejects non-HTTP(S) destinations, URL user-info, non-default ports, IP literals/local or private destinations, and hosts outside the stage-specific allowlist.

The configured cloud origin defaults to `https://new10.gdflix.io/`. It can be supplied with the configuration key `Download:BigOneCloudOrigin`, but it must remain the approved HTTPS origin and root path. The page cannot nominate an arbitrary cloud origin. Relative `/cloud/...` links retain their path and query when resolved.

## Browser hand-off and IDM

Any unknown page shape, interactive page, rejected redirect, unsupported action, or unverifiable final response produces `NeedsBrowser` with the original BollyFlix title-page URL. The Lookup row retains its availability-check URL separately and opens the fallback URL when the user selects Open in Browser.

Only a verified final destination reaches the existing IDM launcher. The launch preserves the IDM Download File Info confirmation dialog; silent `/n` mode is not used.

## Settings compatibility

The existing `%APPDATA%\\DigitalCenter\\download-settings.json` location and JSON property names are retained. The retired `direct-link` and `multi-step-link` assignments are removed and are never migrated to BigOne. Other site assignments, including unimplemented workflow identifiers, are left untouched and remain non-executable.

The IDM executable path, preferred quality, Movies Watch Folder routing, and ticket auto-completion values are preserved. A malformed settings file is not overwritten during compatibility loading. No database or EF Core migration is introduced.
