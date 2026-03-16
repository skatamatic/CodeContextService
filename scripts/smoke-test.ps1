# Smoke test for CodeContextService (run after container is up on port 8080)
$base = "http://localhost:8080"
$ok = $false

# Wait for app to be ready
for ($i = 0; $i -lt 30; $i++) {
    try {
        $r = Invoke-WebRequest -Uri $base -UseBasicParsing -TimeoutSec 3
        if ($r.StatusCode -eq 200) { $ok = $true; break }
    } catch {}
    Start-Sleep -Seconds 2
}
if (-not $ok) { Write-Host "FAIL: App did not become ready"; exit 1 }

Write-Host "1. GET / returns 200 and login-related content..."
$home = Invoke-WebRequest -Uri $base -UseBasicParsing
if ($home.StatusCode -ne 200) { Write-Host "FAIL: GET / returned $($home.StatusCode)"; exit 1 }
if ($home.Content -notmatch "Log in|login|Log In") { Write-Host "WARN: Response may not be login page (no 'Log in' text)" }
Write-Host "   OK (status 200, length $($home.Content.Length))"

Write-Host "2. POST /api/auth/login (admin/changeme)..."
$body = '{"username":"admin","password":"changeme"}'
$login = Invoke-WebRequest -Uri "$base/api/auth/login" -Method POST -Body $body -ContentType "application/json" -UseBasicParsing
if ($login.StatusCode -ne 200) { Write-Host "FAIL: Login returned $($login.StatusCode)"; exit 1 }
$json = $login.Content | ConvertFrom-Json
if (-not $json.token) { Write-Host "FAIL: No token in response"; exit 1 }
Write-Host "   OK (got token)"

Write-Host "3. GET /pr-analyze with auth cookie..."
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$cookie = New-Object System.Net.Cookie("authToken", $json.token, "/", "localhost")
$session.Cookies.Add("http://localhost:8080", $cookie)
$pr = Invoke-WebRequest -Uri "$base/pr-analyze" -WebSession $session -UseBasicParsing
if ($pr.StatusCode -ne 200) { Write-Host "FAIL: GET /pr-analyze returned $($pr.StatusCode)"; exit 1 }
if ($pr.Content -notmatch "Analyze|PR|GitHub") { Write-Host "WARN: Response may not be PR page" }
Write-Host "   OK (status 200, length $($pr.Content.Length))"

Write-Host ""
Write-Host "All smoke tests passed."
