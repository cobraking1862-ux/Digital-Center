# BigOne Visible Browser Automation

DigitalCenter uses Microsoft Playwright for .NET to run the BigOne workflow in a real, visible browser window. It does not use a headless browser or an HTTP-only page crawler for this provider.

## Components

- **Microsoft.Playwright** controls visible Microsoft Edge. A bundled Playwright Chromium browser is the fallback when Edge is unavailable.
- **AngleSharp** parses the live browser DOM using CSS selectors. It is the C# HTML-DOM equivalent for projects that would otherwise use BeautifulSoup.
- The browser uses a dedicated persistent profile at `%APPDATA%\\DigitalCenter\\BrowserProfiles\\BigOne`. It does not attach to or overwrite the user's normal Brave/Edge profile.
- The existing IDM launcher still opens the final download URL with its confirmation dialog.

## One-time browser setup

Build the application and install the fallback Chromium binary:

```powershell
.scripts\Install-PlaywrightBrowsers.ps1
```

The workflow first attempts installed Microsoft Edge. It launches Playwright Chromium visibly if Edge cannot be controlled and the Chromium binary is installed.

## Automated GDFlix interaction and download route

1. Open the configured BollyFlix IMDb search URL in the visible browser.
2. Locate the matching movie post and open it.
3. Parse each quality heading and only its own Google Drive-labelled FastDLServer link.
4. Choose the preferred quality, using the existing downward quality fallback.
5. Navigate through the FastDLServer redirect to the GDFlix file-information page.
6. Automatically click **FAST CLOUD / ZIPDISK** with Playwright and capture the newly opened tab. Wait for the popup to finish navigating to the expected GDFlix `/cloud/` page before inspecting it.
7. Wait until **Cloud Resume Download** is visible. The page may also show **Direct**; DigitalCenter selects Cloud Resume Download explicitly and never selects Direct.
8. Attach download listeners to the Cloud Resume page and every new browser tab, then automatically click **Cloud Resume Download**. Capture the actual browser download event even when GDFlix opens a short-lived tab before starting the transfer.
9. Validate the actual download event URL, cancel the browser's duplicate transfer, and send the captured URL and suggested filename to the existing IDM launcher.

A static `href` for Cloud Resume Download is optional. Some page versions use JavaScript-driven buttons; the actual browser download event is authoritative. Linksmod, Login To DL, Instant DL, Telegram, GoFile and the Direct button are not selected.

## Final download-host policy

GDFlix rotates the `cloud-dl.<id>.workers.dev` subdomain that serves the Cloud Resume download. A download event URL is accepted when its host is either:

- one of the exact approved HTTPS hosts in `AllowedFinalHosts` (`cloud-dl.px6xkyh05ea.workers.dev`, `cloud-dl.bj1zcnlye7n.workers.dev`, `cloud-dl.8ntmtvmb42x.workers.dev`, `quick.cloudpaglu.site`), or
- the exact host that GDFlix itself presented as the **Cloud Resume Download** link on the approved `/cloud/` page during the same run, provided it has the `cloud-dl.<id>.workers.dev` delivery shape.

The second rule keeps the workflow fully automatic when GDFlix rotates to a fresh subdomain, without opening the door to arbitrary hosts: only the single host the approved page itself presented is trusted, and only for that run. If a run still times out, the timeout message names the rejected destination so the host can be added to `AllowedFinalHosts` for a stable, static approval.

## Human verification

The browser window remains visible. If a site presents an actual human-verification page, DigitalCenter pauses and asks the user to complete it in the browser. It does not solve CAPTCHAs, forge tokens, evade access controls, or automate login. Once the expected page element appears, the workflow resumes.

A normal container such as `id="cf_captcha"` and a Turnstile library script are not by themselves classified as a challenge.

## Diagnostics

The Lookup row displays live workflow stages. Browser navigation requests, response status codes, failed requests, and stage messages are written to:

```text
%APPDATA%\DigitalCenter\Logs\digitalcenter-YYYYMMDD.log
```

Watch the current log from PowerShell (replace the date with today's local date):

```powershell
$log = Join-Path $env:APPDATA "DigitalCenter\Logs\digitalcenter-$(Get-Date -Format yyyyMMdd).log"
Get-Content $log -Tail 100 -Wait
```

Signed query strings and long path tokens are redacted from browser URL log entries. Do not share raw browser HAR files, cookies, or browser profile data.
