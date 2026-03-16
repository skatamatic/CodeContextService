# Tests for deploy-gcp.ps1: run with -DryRun and assert expected commands appear in output.
# Run from repo root: .\scripts\tests\deploy-gcp-test.ps1

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RootDir = (Resolve-Path (Join-Path $ScriptDir "..\..")).Path
Set-Location $RootDir

$env:PROJECT_ID = if ($env:PROJECT_ID) { $env:PROJECT_ID } else { "test-project-123" }
$env:CODECONTEXT_JWT_KEY = "test-jwt-key-must-be-at-least-32-characters-long"
$env:CODECONTEXT_ADMIN_PASSWORD = "test-admin-password"

$output = & "$RootDir\scripts\deploy-gcp.ps1" -DryRun *>&1 | Out-String
if (-not $?) {
    Write-Error "deploy-gcp.ps1 -DryRun failed"
    Write-Host $output
    exit 1
}

function Check {
    param([string]$Pattern)
    # Escape [ ] for -like wildcard
    $escaped = $Pattern -replace '\[', '`[' -replace '\]', '`]'
    if ($output -like "*$escaped*") {
        Write-Host "  OK: $Pattern"
    } else {
        Write-Error "FAIL: expected to find '$Pattern' in output"
        Write-Host $output
        exit 1
    }
}

Write-Host "Checking output for expected commands..."

Check "Using PROJECT_ID=$($env:PROJECT_ID)"
Check "[DRY RUN] gcloud config set project"
Check "[DRY RUN] gcloud services enable"
Check "run.googleapis.com"
Check "[DRY RUN] gcloud artifacts repositories create"
Check "[DRY RUN] gcloud secrets create codecontext-jwt-key"
Check "[DRY RUN] gcloud secrets add-iam-policy-binding"
Check "codecontext-admin-password"
Check "[DRY RUN] gcloud auth configure-docker"
Check "[DRY RUN] docker build"
Check "[DRY RUN] docker push"
Check "[DRY RUN] gcloud run deploy"
Check "codecontextservice"
Check "--port 8080"
Check "--memory 2Gi"
Check "JwtSettings__Key=codecontext-jwt-key:latest"
Check "UserCredentials__0__Password=codecontext-admin-password:latest"
Check "RepoClone__CloneRootPath=/tmp/repo-clones"
Check "Deployment complete"
Check "Service URL:"
Check "/health"

Write-Host ""
Write-Host "All checks passed."
