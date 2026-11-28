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

## Assisted GDFlix interaction and download route

1. Open the configured BollyFlix IMDb search URL in the visible browser.
2. Locate the matching movie post and open it.
3. Parse each quality heading and only its own Google Drive-labelled FastDLServer link.
4. Choose the preferred quality, using the existing downward quality fallback.
5. Navigate through the FastDLServer redirect to the GDFlix file-information page.
6. **Manual action:** click **FAST CLOUD / ZIPDISK** in the visible GDFlix file-information page. DigitalCenter waits for the newly opened tab and verifies that it has finished navigating to the expected GDFlix `/cloud/` page.
7. Wait until **Cloud Resume Download** is visible. The page may also show **Direct**; DigitalCenter does not choose Direct.
8. **Manual action:** click **Cloud Resume Download**. DigitalCenter monitors the browser context, including the short-lived tab opened by the button, and waits for the actual browser download event.
9. Validate the actual download event URL against the exact approved HTTPS final hosts, cancel the browser's duplicate transfer, and send the captured URL and suggested filename to the existing IDM launcher.

A static `href` for Cloud Resume Download is optional. Some page versions use JavaScript-driven buttons; the actual browser download event is authoritative. Linksmod, Login To DL, Instant DL, Telegram, GoFile and the Direct button are not selected.

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
