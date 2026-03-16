# CodeContextService – GCP Deployment Guide

This document covers everything required to deploy CodeContextService to Google Cloud Platform: services, IAM, secrets, environment variables, storage, networking, and step-by-step commands.

**Automated deploy:** From the repo root you can run a script that does setup and deploy in one go (bash on Linux/macOS/WSL, PowerShell on Windows). See [Section 5a](#5a-automated-deploy-script) below.

---

## 1. Architecture and GCP Services

| GCP service | Purpose |
|-------------|---------|
| **Cloud Run** | Run the containerized app (serverless, scale-to-zero, HTTPS). |
| **Artifact Registry** | Store the Docker image. |
| **Secret Manager** | Store JWT signing key and user passwords (no secrets in env). |
| **Cloud Build** (optional) | Build and push the image on commit; optional CI/CD. |

The app is an **ASP.NET Core 8 Blazor Server** app with:

- **JWT auth** for UI and API; login at `/api/auth/login`.
- **PR analysis** via GitHub (clone repo, run Roslyn); **zip upload** analysis (extract zip, run Roslyn).
- **Ephemeral storage**: repo clones and zip extracts go to a configurable directory and are **deleted after each request** (no persistent disk required).
- **Rate limiting**: login (5/min per IP), analyze (10/min per user).

**Recommended target:** **Cloud Run** (ephemeral filesystem, pay-per-request, managed HTTPS). Alternatives: GKE or GCE if you need long-lived VMs.

---

## 2. Prerequisites

- **Google Cloud SDK** (`gcloud`) installed and authenticated: `gcloud auth login` and `gcloud config set project PROJECT_ID`.
- **Docker** installed (for local build/push, or use Cloud Build).
- **Git** (repo is cloned for PR analysis; zip upload does not require GitHub).

---

## 3. Environment Variables and Configuration

Configuration uses the standard ASP.NET Core hierarchy: **environment variables override `appsettings.json`**. Use env vars or **Secret Manager** for all production secrets.

### 3.1 Required in production

The app **refuses to start** in Production if these are missing or still placeholders:

| Variable | Description | Example / notes |
|----------|-------------|------------------|
| `JwtSettings__Key` | JWT signing key (min 32 chars for HMAC-SHA256). | Store in Secret Manager; never commit. |
| At least one user | Username, password, role. | Via `UserCredentials__0__Username`, `UserCredentials__0__Password`, `UserCredentials__0__Role`, or from a secret. |

**Single user (env or secrets):**

- `UserCredentials__0__Username` (e.g. `admin`)
- `UserCredentials__0__Password` (strong password; use Secret Manager)
- `UserCredentials__0__Role` (e.g. `Admin`)

**Multiple users:** Add `UserCredentials__1__Username`, `UserCredentials__1__Password`, `UserCredentials__1__Role`, etc., or use a single secret containing a JSON array and map it to the config key.

### 3.2 Optional (sensible defaults)

| Variable | Description | Default |
|----------|-------------|--------|
| `JwtSettings__Issuer` | JWT issuer. | `CodeContextService` |
| `JwtSettings__Audience` | JWT audience. | `CodeContextServiceUsers` |
| `JwtSettings__ExpiresMinutes` | Token lifetime (minutes). | `60` |
| `RepoClone__CloneRootPath` | Directory for repo clones and zip extracts. | `Path.GetTempPath()` (e.g. `/tmp`); on Cloud Run use `/tmp/repo-clones`. |
| `ASPNETCORE_ENVIRONMENT` | Environment name. | Set to `Production` in production. |
| `ASPNETCORE_URLS` | Listen URLs. | Image default: `http://0.0.0.0:8080`. |
| `CircuitOptions__DetailedErrors` | Show detailed Blazor circuit errors in browser. | `true` in Dockerfile; set to `false` in production to hide stack traces. |

### 3.3 Not set in production

- **Swagger** is enabled only when `ASPNETCORE_ENVIRONMENT=Development`; it is off in Production.
- **HTTPS redirect** is skipped when `DOTNET_RUNNING_IN_CONTAINER=true` (Dockerfile sets this); Cloud Run terminates TLS and forwards HTTP to the container.

---

## 4. Storage and Filesystem

### 4.1 Clone and upload directory

- **Config key:** `RepoClone__CloneRootPath`.
- **Usage:** All repo clones (PR analysis) and zip extract directories (upload analysis) are created under this path. Each run uses a unique subdirectory; the app **deletes it in a `finally` block** after every request.
- **Cloud Run:** The only writable directory is `/tmp`. Set `RepoClone__CloneRootPath=/tmp/repo-clones`. No persistent disk; no extra cost. When the instance scales to zero, everything is gone.
- **GKE with ephemeral disk:** Set `RepoClone__CloneRootPath` to a path on the container’s writable filesystem (e.g. `/tmp/repo-clones`).
- **GKE/GCE with persistent disk:** Mount a volume and set the clone root to that mount; rely on in-app cleanup and optionally a cron to remove stale dirs.

### 4.2 Data Protection (antiforgery) keys

- The app uses ASP.NET Core Data Protection for antiforgery tokens. Keys are stored under the default path (e.g. `%APPDATA%` or `$HOME/.aspnet`).
- **Cloud Run:** Instance filesystem is ephemeral; keys are not persisted across cold starts. This is acceptable: antiforgery still works per instance; after scale-to-zero a new instance gets new keys.
- **Optional:** To persist keys (e.g. for sticky sessions or consistent key ring), you would need a shared store (e.g. GCS or a file share); the app does not configure this by default.

### 4.3 Summary

- No GCS bucket or Filestore is **required** for normal operation.
- Use **ephemeral storage only** (Cloud Run `/tmp` or emptyDir on GKE) to avoid persistent storage cost; the app’s cleanup keeps usage bounded.

---

## 5. GCP Setup (one-time)

### 5a. Automated deploy script

From the **repo root**, with `gcloud` and Docker installed:

**Bash (Linux / macOS / WSL):**

```bash
export CODECONTEXT_JWT_KEY="your-64-char-jwt-signing-key"
export CODECONTEXT_ADMIN_PASSWORD="YourSecureAdminPassword"
./scripts/deploy-gcp.sh
```

**PowerShell (Windows):**

```powershell
$env:CODECONTEXT_JWT_KEY = "your-64-char-jwt-signing-key"
$env:CODECONTEXT_ADMIN_PASSWORD = "YourSecureAdminPassword"
.\scripts\deploy-gcp.ps1
```

The script will: enable APIs, create the Artifact Registry repo (if missing), create or update secrets, grant IAM, build and push the image, and deploy to Cloud Run. If you omit the env vars, it **auto-generates** a JWT key and admin password and prints them at the end so you can use them in the UI and in any service that calls this API.

Optional env vars: `PROJECT_ID`, `REGION` (default `us-central1`), `SERVICE_NAME` (default `codecontextservice`), `REPO_NAME` (default `cloud-run-source-deploy`), `VPC_CONNECTOR` (Serverless VPC Access connector name), and `VPC_EGRESS` (default `private-ranges-only`). To skip the image build: `./scripts/deploy-gcp.sh --skip-build` or `.\scripts\deploy-gcp.ps1 -SkipBuild`.

**Testing the scripts:** Run with `--dry-run` (bash) or `-DryRun` (PowerShell) to echo commands without executing them. Automated tests: `./scripts/tests/deploy-gcp-test.sh` (bash) or `.\scripts\tests\deploy-gcp-test.ps1` (PowerShell). See **scripts/tests/README.md**.

**After deployment:** The script prints the Service URL and a **Credentials** block (username, password, JWT key). Save these for the UI and for any service that calls this API. Open the Service URL in your browser and log in with **admin** and the printed password. The script also runs a quick health check against `/health`; if you see "Health check: OK (HTTP 200)", the app is ready.

---

### 5.1 Enable APIs (manual)

```bash
export PROJECT_ID=your-gcp-project-id
gcloud config set project $PROJECT_ID

gcloud services enable \
  run.googleapis.com \
  artifactregistry.googleapis.com \
  secretmanager.googleapis.com \
  cloudbuild.googleapis.com
```

### 5.2 Create Artifact Registry repository

```bash
export REGION=us-central1
gcloud artifacts repositories create cloud-run-source-deploy \
  --repository-format=docker \
  --location=$REGION \
  --description="Docker images for Cloud Run"
```

(If the repo already exists, skip or use the existing repo name.)

### 5.3 Create secrets

**JWT signing key (min 32 characters):**

```bash
# Generate a key (example: 64 hex chars) and store it
echo -n "your-64-char-hex-or-base64-jwt-signing-key-here" | \
  gcloud secrets create codecontext-jwt-key --data-file=-
```

**Admin password:**

```bash
echo -n "YourStrongAdminPassword" | \
  gcloud secrets create codecontext-admin-password --data-file=-
```

### 5.4 IAM: allow Cloud Run to read secrets

Cloud Run uses the default compute service account. Get the project number, then grant it access to the secrets:

```bash
PROJECT_NUMBER=$(gcloud projects describe $PROJECT_ID --format='value(projectNumber)')
SA="${PROJECT_NUMBER}-compute@developer.gserviceaccount.com"

gcloud secrets add-iam-policy-binding codecontext-jwt-key \
  --member="serviceAccount:${SA}" \
  --role="roles/secretmanager.secretAccessor"

gcloud secrets add-iam-policy-binding codecontext-admin-password \
  --member="serviceAccount:${SA}" \
  --role="roles/secretmanager.secretAccessor"
```

---

## 6. Build and Push the Image

### 6.1 Configure Docker for Artifact Registry

```bash
gcloud auth configure-docker ${REGION}-docker.pkg.dev --quiet
```

### 6.2 Build and push (from repo root)

```bash
export IMAGE=${REGION}-docker.pkg.dev/${PROJECT_ID}/cloud-run-source-deploy/codecontextservice

docker build -t $IMAGE .
docker push $IMAGE
```

### 6.3 Optional: Cloud Build (CI/CD)

From repo root:

```bash
gcloud builds submit --tag $IMAGE
```

Or use a `cloudbuild.yaml` that runs `docker build`, pushes to Artifact Registry, and optionally deploys to Cloud Run.

---

## 7. Deploy to Cloud Run

### 7.1 Deploy command (secrets + env)

```bash
gcloud run deploy codecontextservice \
  --image $IMAGE \
  --region $REGION \
  --platform managed \
  --allow-unauthenticated \
  --port 8080 \
  --memory 2Gi \
  --cpu 2 \
  --timeout 600 \
  --min-instances 0 \
  --max-instances 10 \
  --set-env-vars "ASPNETCORE_ENVIRONMENT=Production" \
  --set-env-vars "RepoClone__CloneRootPath=/tmp/repo-clones" \
  --set-env-vars "UserCredentials__0__Username=admin" \
  --set-env-vars "UserCredentials__0__Role=Admin" \
  --set-secrets "JwtSettings__Key=codecontext-jwt-key:latest" \
  --set-secrets "UserCredentials__0__Password=codecontext-admin-password:latest"
```

**Notes:**

- **`--allow-unauthenticated`**: Makes the service publicly reachable (login is app-level JWT). Use `--no-allow-unauthenticated` and IAM if you want only authenticated GCP identities to invoke the service URL.
- **`--memory 2Gi`, `--cpu 2`**: Roslyn and repo clones can be heavy; increase if you see OOM or timeouts on large repos.
- **`--timeout 600`**: 10 minutes; adjust if analyses run longer.
- **`--port 8080`**: Must match `ASPNETCORE_URLS` in the image (default `http://0.0.0.0:8080`).

### 7.2 Multiple users

Add more env and secrets:

```bash
--set-env-vars "UserCredentials__1__Username=viewer,UserCredentials__1__Role=Viewer" \
--set-secrets "UserCredentials__1__Password=codecontext-viewer-password:latest"
```

Create the secret first:

```bash
echo -n "ViewerPassword" | gcloud secrets create codecontext-viewer-password --data-file=-
gcloud secrets add-iam-policy-binding codecontext-viewer-password \
  --member="serviceAccount:${SA}" --role="roles/secretmanager.secretAccessor"
```

### 7.3 Optional: hide Blazor circuit errors in production

```bash
--set-env-vars "CircuitOptions__DetailedErrors=false"
```

### 7.4 Get the service URL

```bash
gcloud run services describe codecontextservice --region $REGION --format='value(status.url)'
```

Open that URL in a browser; you should see the login page. Log in with the username and password you set in secrets.

---

## 8. Health Check and Probes

- **Endpoint:** `GET /health` (no authentication).
- **Response:** `200 OK` with body like `{"Status":"Healthy","Timestamp":"..."}`.

**Cloud Run:** Uses the container’s HTTP port (8080) for health; no extra probe config is required unless you customize. You can point a load balancer or uptime check at `https://YOUR_SERVICE_URL/health`.

---

## 9. Networking and HTTPS

- **Cloud Run** terminates HTTPS and forwards HTTP to the container on the port you set (8080). The app does not need to configure TLS.
- **Port:** Container must listen on `0.0.0.0:8080` (default in the Dockerfile).
- **Allowed hosts:** `AllowedHosts` in appsettings is `*`; for stricter rules, override via config if needed.

---

## 10. Rate Limiting (built-in)

| Endpoint / scope | Limit | Purpose |
|------------------|--------|---------|
| `POST /api/auth/login` | 5 per minute per client IP | Slow brute force; uses `X-Forwarded-For` when behind a proxy. |
| `POST /api/analyze` (PR analysis) | 10 per minute per authenticated user | Cap expensive clone + Roslyn runs. |
| Zip upload (`POST /api/analyze-upload`) | 10 per minute per authenticated user | Same policy as analyze. |

Rejected requests return **429 Too Many Requests** and a **Retry-After** header when applicable. Limits are fixed in code; adjust in `Program.cs` if you need different values.

---

## 11. Request body limits

- **Zip upload:** The app allows up to **500 MB** for `POST /api/analyze-upload` (Kestrel `MaxRequestBodySize`). Default Kestrel limit (30 MB) is overridden only for this path.
- Other endpoints use the default limit.

---

## 12. Checklist Before Production

- [ ] **APIs enabled:** Cloud Run, Artifact Registry, Secret Manager (and Cloud Build if used).
- [ ] **Secrets created:** JWT key and at least one user password; IAM grants Cloud Run’s service account `secretAccessor` on those secrets.
- [ ] **Env vars:** `ASPNETCORE_ENVIRONMENT=Production`, `RepoClone__CloneRootPath=/tmp/repo-clones` (or your chosen path).
- [ ] **No placeholder secrets:** App will not start if `JwtSettings__Key` or user passwords are missing or still `REPLACE_*`.
- [ ] **Image:** Built for `linux-x64` (for LibGit2Sharp); runtime image is SDK (for MSBuild/Roslyn).
- [ ] **Port:** Container listens on `8080`; Cloud Run `--port 8080`.
- [ ] **Memory/CPU/Timeout:** Set appropriately for large repos (e.g. 2Gi, 2 CPU, 600s timeout).
- [ ] **Swagger:** Disabled in Production (automatic when `ASPNETCORE_ENVIRONMENT=Production`).
- [ ] **Optional:** `CircuitOptions__DetailedErrors=false` to hide Blazor stack traces.

---

## 13. Other GCP Targets (brief)

- **GKE:** Use the same image. Set `RepoClone__CloneRootPath` to a path on an ephemeral volume (e.g. `emptyDir`). Expose the deployment via a Service/Ingress; use the same env and secrets (e.g. from a Secret + ConfigMap or from Workload Identity + Secret Manager).
- **GCE (VM):** Run the container or publish the app and run `dotnet CodeContextService.dll`. Set clone root to a local/ephemeral path if available, or a small persistent disk and rely on in-app cleanup plus optional cron for old dirs.

---

## 14. Summary

| Item | Action |
|------|--------|
| **Secrets** | JWT key and user password(s) in Secret Manager; grant Cloud Run SA access. |
| **Env** | Production, clone root, user names/roles; passwords from secrets. |
| **Storage** | Ephemeral only (`/tmp/repo-clones` on Cloud Run); no GCS/Filestore required. |
| **Build** | Docker build `-r linux-x64`; runtime image = SDK for MSBuild/Roslyn. |
| **Deploy** | Cloud Run with `--port 8080`, memory/CPU/timeout tuned, secrets and env set. |
| **Health** | `GET /health` for probes. |
| **Auth** | App-level JWT via `/api/auth/login`; rate limits on login and analyze. |

For more detail on configuration and storage strategy, see **docs/GCP_DEPLOYMENT.md**.
