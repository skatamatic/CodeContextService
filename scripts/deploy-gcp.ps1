# Full GCP deployment for CodeContextService: APIs, Artifact Registry, secrets, IAM, build, push, Cloud Run deploy.
# Can be run from any directory (script switches to repo root). Requires: gcloud, docker.
#
# Optional env vars (if not set, JWT key and admin password are auto-generated and printed at the end):
#   $env:CODECONTEXT_JWT_KEY         - JWT signing key (min 32 chars)
#   $env:CODECONTEXT_ADMIN_PASSWORD  - Admin user password
#
# Optional env vars:
#   $env:PROJECT_ID    - GCP project (default: gcloud config value)
#   $env:REGION        - e.g. us-central1 (default: us-central1)
#   $env:SERVICE_NAME  - Cloud Run service name (default: codecontextservice)
#   $env:REPO_NAME     - Artifact Registry repo name (default: cloud-run-source-deploy)
#   $env:VPC_CONNECTOR - Optional Serverless VPC Access connector name
#   $env:VPC_EGRESS    - Optional egress mode when VPC_CONNECTOR is set (default: private-ranges-only)
#
# Usage:
#   $env:CODECONTEXT_JWT_KEY = "your-64-char-key"
#   $env:CODECONTEXT_ADMIN_PASSWORD = "YourSecurePassword"
#   .\scripts\deploy-gcp.ps1
#
# Or skip image build (use existing image):
#   .\scripts\deploy-gcp.ps1 -SkipBuild
#
# Dry run (echo commands, do not execute; PROJECT_ID must be set):
#   $env:PROJECT_ID = "my-project"; .\scripts\deploy-gcp.ps1 -DryRun

param(
    [switch]$SkipBuild,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

function Invoke-OrDryRun {
    param([string]$Command, [scriptblock]$ScriptBlock)
    if ($DryRun) { Write-Host "[DRY RUN] $Command"; return }
    & $ScriptBlock
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# Run from repo root (so docker build finds Dockerfile)
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RootDir = Split-Path -Parent $ScriptDir
Set-Location $RootDir

$Region = if ($env:REGION) { $env:REGION } else { "us-central1" }
$ServiceName = if ($env:SERVICE_NAME) { $env:SERVICE_NAME } else { "codecontextservice" }
$RepoName = if ($env:REPO_NAME) { $env:REPO_NAME } else { "cloud-run-source-deploy" }
$VpcConnector = $env:VPC_CONNECTOR
$VpcEgress = if ($env:VPC_EGRESS) { $env:VPC_EGRESS } else { "private-ranges-only" }

if (-not $env:PROJECT_ID) {
    if ($DryRun) {
        Write-Error "PROJECT_ID must be set when using -DryRun (e.g. `$env:PROJECT_ID = 'my-proj'; .\scripts\deploy-gcp.ps1 -DryRun)"
        exit 1
    }
    $env:PROJECT_ID = gcloud config get-value project 2>$null
    if (-not $env:PROJECT_ID) {
        Write-Error "PROJECT_ID not set and gcloud project not configured. Set `$env:PROJECT_ID or run: gcloud config set project YOUR_PROJECT"
        exit 1
    }
}

Write-Host "Using PROJECT_ID=$($env:PROJECT_ID) REGION=$Region SERVICE_NAME=$ServiceName"

# --- Preflight: ensure gcloud is authenticated ---
if (-not $DryRun) {
    $null = gcloud auth print-access-token 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Not authenticated with gcloud. Run: gcloud auth login"
        exit 1
    }
    Write-Host "gcloud authenticated as: $(gcloud config get-value account 2>$null)"
}

# --- Secrets: use env vars or auto-generate ---
if (-not $env:CODECONTEXT_JWT_KEY) {
    if ($DryRun) { $env:CODECONTEXT_JWT_KEY = "dry-run-jwt-key-min-32-chars-long--------" }
    else {
        $bytes = [byte[]]::new(32); [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
        $env:CODECONTEXT_JWT_KEY = [Convert]::ToBase64String($bytes)
        Write-Host "Generated new JWT signing key (min 32 chars)."
    }
}
if (-not $env:CODECONTEXT_ADMIN_PASSWORD) {
    if ($DryRun) { $env:CODECONTEXT_ADMIN_PASSWORD = "dry-run-admin-password" }
    else {
        $bytes = [byte[]]::new(24); [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
        $env:CODECONTEXT_ADMIN_PASSWORD = [Convert]::ToBase64String($bytes).Replace('+', '').Replace('/', '').Replace('=', '')
        Write-Host "Generated new admin password."
    }
}
if ($env:CODECONTEXT_JWT_KEY.Length -lt 32) {
    Write-Error "JWT key must be at least 32 characters"
    exit 1
}

Invoke-OrDryRun "gcloud config set project $($env:PROJECT_ID)" { gcloud config set project $env:PROJECT_ID }

# --- Enable APIs (can take 1-2 minutes; not a hang) ---
Write-Host "Enabling APIs (this may take 1-2 minutes)..."
Invoke-OrDryRun "gcloud services enable run.googleapis.com artifactregistry.googleapis.com secretmanager.googleapis.com cloudbuild.googleapis.com --quiet" {
    gcloud services enable run.googleapis.com artifactregistry.googleapis.com secretmanager.googleapis.com cloudbuild.googleapis.com --quiet
}
if (-not $DryRun) { Write-Host "  APIs enabled." }

# --- Artifact Registry ---
Write-Host "Ensuring Artifact Registry repository..."
$repoExists = $false
if (-not $DryRun) {
    gcloud artifacts repositories describe $RepoName --location=$Region 2>$null
    if ($LASTEXITCODE -eq 0) { $repoExists = $true }
}
if (-not $repoExists) {
    Invoke-OrDryRun "gcloud artifacts repositories create $RepoName --repository-format=docker --location=$Region --description=`"Docker images for Cloud Run`"" {
        gcloud artifacts repositories create $RepoName --repository-format=docker --location=$Region --description="Docker images for Cloud Run"
    }
} else {
    Write-Host "  Repository $RepoName already exists"
}

# --- Secrets (use temp files so secrets are not in command line) ---
$JwtSecret = "codecontext-jwt-key"
$AdminSecret = "codecontext-admin-password"

if ($DryRun) {
    Write-Host "Creating secret: $JwtSecret"
    Write-Host "[DRY RUN] gcloud secrets create $JwtSecret --data-file=-"
    Write-Host "Adding new version to $AdminSecret"
    Write-Host "[DRY RUN] gcloud secrets versions add $AdminSecret --data-file=-"
} else {
    $jwtTmp = [System.IO.Path]::GetTempFileName()
    $adminTmp = [System.IO.Path]::GetTempFileName()
    try {
        [System.IO.File]::WriteAllText($jwtTmp, $env:CODECONTEXT_JWT_KEY)
        [System.IO.File]::WriteAllText($adminTmp, $env:CODECONTEXT_ADMIN_PASSWORD)

        gcloud secrets describe $JwtSecret 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Creating secret: $JwtSecret"
            gcloud secrets create $JwtSecret --data-file=$jwtTmp
        } else {
            Write-Host "Adding new version to $JwtSecret"
            gcloud secrets versions add $JwtSecret --data-file=$jwtTmp
        }
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

        gcloud secrets describe $AdminSecret 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Creating secret: $AdminSecret"
            gcloud secrets create $AdminSecret --data-file=$adminTmp
        } else {
            Write-Host "Adding new version to $AdminSecret"
            gcloud secrets versions add $AdminSecret --data-file=$adminTmp
        }
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    } finally {
        Remove-Item $jwtTmp, $adminTmp -Force -ErrorAction SilentlyContinue
    }
}

# --- IAM ---
Write-Host "Granting Cloud Run service account access to secrets..."
if ($DryRun) { $ProjectNumber = "123456789" } else { $ProjectNumber = gcloud projects describe $env:PROJECT_ID --format="value(projectNumber)" }
$SA = "${ProjectNumber}-compute@developer.gserviceaccount.com"
Invoke-OrDryRun "gcloud secrets add-iam-policy-binding $JwtSecret --member=serviceAccount:$SA --role=roles/secretmanager.secretAccessor --quiet" {
    gcloud secrets add-iam-policy-binding $JwtSecret --member="serviceAccount:$SA" --role="roles/secretmanager.secretAccessor" --quiet
}
Invoke-OrDryRun "gcloud secrets add-iam-policy-binding $AdminSecret --member=serviceAccount:$SA --role=roles/secretmanager.secretAccessor --quiet" {
    gcloud secrets add-iam-policy-binding $AdminSecret --member="serviceAccount:$SA" --role="roles/secretmanager.secretAccessor" --quiet
}

# --- Image ---
$Image = "${Region}-docker.pkg.dev/$($env:PROJECT_ID)/${RepoName}/codecontextservice"

if ($SkipBuild) {
    Write-Host "Skipping build (-SkipBuild). Using image: $Image"
} else {
    Write-Host "Configuring Docker for Artifact Registry..."
    Invoke-OrDryRun "gcloud auth configure-docker ${Region}-docker.pkg.dev --quiet" { gcloud auth configure-docker "${Region}-docker.pkg.dev" --quiet }
    Write-Host "Building and pushing image..."
    Invoke-OrDryRun "docker build -t $Image ." { docker build -t $Image . }
    Invoke-OrDryRun "docker push $Image" { docker push $Image }
}

# --- Deploy Cloud Run ---
$deployCmd = @(
    "run", "deploy", $ServiceName,
    "--image", $Image,
    "--region", $Region,
    "--platform", "managed",
    "--allow-unauthenticated",
    "--port", "8080",
    "--memory", "2Gi",
    "--cpu", "2",
    "--timeout", "600",
    "--min-instances", "0",
    "--max-instances", "10",
    "--set-env-vars", "ASPNETCORE_ENVIRONMENT=Production",
    "--set-env-vars", "RepoClone__CloneRootPath=/tmp/repo-clones",
    "--set-env-vars", "UserCredentials__0__Username=admin",
    "--set-env-vars", "UserCredentials__0__Role=Admin",
    "--set-secrets", "JwtSettings__Key=${JwtSecret}:latest",
    "--set-secrets", "UserCredentials__0__Password=${AdminSecret}:latest",
    "--quiet"
)
if ($VpcConnector) {
    Write-Host "Using VPC connector: $VpcConnector (egress: $VpcEgress)"
    $deployCmd += @("--vpc-connector", $VpcConnector, "--vpc-egress", $VpcEgress)
}
$deployArgs = "gcloud " + ($deployCmd -join " ")
Write-Host "Deploying to Cloud Run..."
Invoke-OrDryRun $deployArgs {
    & gcloud @deployCmd
}

if ($DryRun) { $Url = "https://${ServiceName}-xxxxx-${Region}.a.run.app" } else {
    $Url = gcloud run services describe $ServiceName --region $Region --format="value(status.url)"
    try {
        $r = Invoke-WebRequest -Uri "$Url/health" -UseBasicParsing -TimeoutSec 15 -ErrorAction Stop
        if ($r.StatusCode -eq 200) { Write-Host "  Health check: OK (HTTP 200)" }
        else { Write-Host "  Health check: got HTTP $($r.StatusCode) (service may still be starting)" }
    } catch {
        Write-Host "  Health check: $($_.Exception.Message) (service may still be starting)"
    }
}
Write-Host ""
Write-Host "Deployment complete."
Write-Host "  Service URL: $Url"
Write-Host "  Health:      $Url/health"
Write-Host ""
Write-Host "--- Credentials (save these for the UI and for any service that calls this API) ---"
Write-Host "  Username:   admin"
Write-Host "  Password:   $($env:CODECONTEXT_ADMIN_PASSWORD)"
Write-Host "  JWT Key:    $($env:CODECONTEXT_JWT_KEY)"
Write-Host "---"
Write-Host ""
Write-Host "Open the Service URL in your browser and log in with the username and password above."
