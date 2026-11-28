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

## Automated route

1. Open the configured BollyFlix IMDb search URL in the visible browser.
2. Locate the matching movie post and open it.
3. Parse each quality heading and only its own Google Drive-labelled FastDLServer link.
4. Choose the preferred quality, using the existing downward quality fallback.
5. Navigate through the FastDLServer redirect in the visible Edge browser.
6. Click the validated FAST CLOUD / ZIPDISK link and capture the new GDFlix tab. The workflow no longer navigates directly to the extracted cloud URL.
7. Wait for the Cloud Resume Download anchor (the legacy Quick Download (Ready) link is only recognized as an alternative).
8. Click Cloud Resume Download and capture the browser's actual download event. The event supplies the final URL and browser-suggested filename.
9. Validate the captured HTTPS host, cancel Edge's temporary copy to avoid downloading the same file twice, and return the captured URL to the existing IDM confirmation-dialog launcher.

The workflow observes download events from both the cloud page and any short-lived tab opened by the download click. Unapproved download URLs are cancelled and ignored. Linksmod, Login To DL, Instant DL, Telegram, GoFile and the JavaScript-only Generate Quick Link action are not selected by this route.

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
