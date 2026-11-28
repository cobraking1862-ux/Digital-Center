$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Solution = Join-Path $RepoRoot "DigitalCenter.slnx"

Write-Host "Building DigitalCenter so the Playwright CLI is available..." -ForegroundColor Cyan
dotnet build $Solution
if ($LASTEXITCODE -ne 0) {
    throw "DigitalCenter build failed. Fix build errors before installing the browser."
}

$Candidates = @(
    (Join-Path $RepoRoot "src\DigitalCenter.App\bin\Debug\net10.0-windows\playwright.ps1"),
    (Join-Path $RepoRoot "src\DigitalCenter.Core\bin\Debug\net10.0\playwright.ps1"),
    (Join-Path $RepoRoot "src\DigitalCenter.App\bin\Release\net10.0-windows\playwright.ps1"),
    (Join-Path $RepoRoot "src\DigitalCenter.Core\bin\Release\net10.0\playwright.ps1")
)

$PlaywrightScript = $Candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $PlaywrightScript) {
    throw "Could not find the generated playwright.ps1 script in the build output."
}

Write-Host "Installing the Playwright Chromium browser..." -ForegroundColor Cyan
& $PlaywrightScript install chromium
if ($LASTEXITCODE -ne 0) {
    throw "Playwright Chromium installation failed."
}

Write-Host "Browser setup completed." -ForegroundColor Green
Write-Host "DigitalCenter will prefer installed Microsoft Edge and use Playwright Chromium as a fallback."
