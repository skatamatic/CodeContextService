# GCP Deployment Guide – CodeContextService

This document describes how to deploy CodeContextService to Google Cloud Platform with best-practice configuration, environment-based secrets, and cost-conscious storage for repository clones.

---

## 1. Architecture overview

- **App**: ASP.NET Core 8 Blazor Server + minimal API; JWT auth; PR analysis by cloning GitHub repos and running Roslyn analysis.
- **Storage**: Clones are written to a **configurable root directory** and **deleted after each analysis** to avoid persistent disk cost and clutter.
- **Recommended GCP target**: **Cloud Run** (serverless, ephemeral filesystem, pay-per-request). Alternatives: GKE or GCE if you need long-lived VMs.

---

## 2. Environment variables and configuration

Configuration follows the standard ASP.NET Core hierarchy: **environment variables override `appsettings.json`**. Use env vars (or GCP Secret Manager) for all production secrets.

### 2.1 Required in production

| Environment variable | Description | Example / notes |
|----------------------|-------------|------------------|
| `JwtSettings__Key` | JWT signing key (min 32 bytes, hex or base64). | Set via Secret Manager or env. **Never commit.** |
| `UserCredentials` | JSON array of `{ "Username", "Password", "Role" }`. | For multiple users use Secret Manager or a single user via individual env vars (see below). |

**Single user via env (optional):**

- `UserCredentials__0__Username`
- `UserCredentials__0__Password`
- `UserCredentials__0__Role`

If you only need one user in production, set these three; the app will bind one credential.

**Initial deployment:** There are **no built-in default credentials**. The repo only has placeholders in `appsettings.json`. In **Production** the app **refuses to start** until you set both a real JWT key and at least one user with a non-placeholder password. So you **must** provide credentials via env or Secret Manager on first deploy, for example:

```bash
# Minimum for one admin user (set a strong password)
UserCredentials__0__Username=admin
UserCredentials__0__Password=YourSecurePasswordHere
UserCredentials__0__Role=Admin
```

### 2.2 Optional (sensible defaults)

| Environment variable | Description | Default |
|----------------------|-------------|--------|
| `JwtSettings__Issuer` | JWT issuer. | `CodeContextService` |
| `JwtSettings__Audience` | JWT audience. | `CodeContextServiceUsers` |
| `JwtSettings__ExpiresMinutes` | Token lifetime (minutes). | `60` |
| `RepoClone__CloneRootPath` | Directory where repos are cloned. | `Path.GetTempPath()` (e.g. `/tmp` on Linux) |
| `ASPNETCORE_ENVIRONMENT` | Environment name. | Set to `Production` in production. |
| `ASPNETCORE_URLS` | Listen URLs. | In Dockerfile: `http://0.0.0.0:8080` for Cloud Run. |

### 2.3 Startup validation

- In **Production**, if `JwtSettings:Key` is missing or still the placeholder value from `appsettings.json`, the application **throws at startup**. Ensure you set `JwtSettings__Key` (or inject the key via Secret Manager into config) before deploying.

### 2.4 Local development

- Use **User Secrets** or **env** so real secrets are never in `appsettings.json`:

  ```bash
  dotnet user-secrets set "JwtSettings:Key" "your-64-char-hex-key"
  dotnet user-secrets set "UserCredentials:0:Username" "devuser"
  dotnet user-secrets set "UserCredentials:0:Password" "YourDevPassword"
  dotnet user-secrets set "UserCredentials:0:Role" "Admin"
  ```

- In Development, User Secrets are loaded automatically (optional). Placeholders in `appsettings.json` are only for documentation; replace via secrets or env.

### 2.5 Auth and abuse protection (rate limiting)

To make the app suitable for professional deployment and limit damage from bad actors:

- **JWT auth**: All API endpoints except `/api/auth/login`, `/health`, and the login page are protected by a **fallback policy** requiring a valid JWT. Tokens are validated (issuer, audience, signing key, lifetime) with zero clock skew.
- **Login rate limit**: `POST /api/auth/login` is rate limited **per client IP** (5 requests per minute). This slows brute-force and credential stuffing. When behind a proxy, the app respects `X-Forwarded-For` for the client IP. Rejected requests return **429 Too Many Requests** and a **Retry-After** header when applicable.
- **Analyze rate limit**: `POST /api/analyze` is rate limited **per authenticated user** (10 requests per minute). Each analysis clones a repo and runs Roslyn, so this cap limits cost and DoS from a single account (or stolen token) spamming the endpoint.
- **Health**: `GET /health` is not rate limited so probes and load balancers are not blocked.

Limits are fixed-window; you can tune them via code (e.g. different PermitLimit/Window) or add config keys if you want env-driven limits later.

---

## 3. Storage strategy: clone directory and cleanup

### 3.1 How it works

- **Clone root**: Configurable via `RepoClone__CloneRootPath`. If unset, the app uses the system temp directory (e.g. `/tmp` on Linux, `%TEMP%` on Windows).
- **Per-request clone path**: Each analysis creates a unique directory under the clone root: `{CloneRootPath}/{repo}-{Guid}`.
- **Cleanup**: After every analysis (success or failure), the service deletes the clone directory in a `finally` block. This:
  - Avoids leaving large repos on disk.
  - Keeps GCP storage costs predictable when using ephemeral or local SSD.

### 3.2 GCP-specific recommendations

| Target | Storage recommendation | Why |
|--------|------------------------|-----|
| **Cloud Run** | Use default `/tmp` or set `RepoClone__CloneRootPath=/tmp/repo-clones`. | Cloud Run’s writable filesystem is ephemeral and local to the instance. No persistent disk; no extra cost. Clones are removed after each request; when the instance scales to zero, everything is gone. |
| **GKE (with ephemeral disk)** | Set `RepoClone__CloneRootPath` to a path on the container’s writable filesystem (e.g. `/tmp/repo-clones`). | Same idea: use ephemeral storage only; cleanup after each run keeps usage bounded. |
| **GKE or GCE with persistent disk** | Either mount a separate volume for clones and set `RepoClone__CloneRootPath` to that mount, or use a local SSD / ephemeral volume. | If you must use persistent disk, ensure cleanup runs (it does by default) and consider a cron or startup job to remove any stale `*-{Guid}` dirs older than N hours as a safety net. |

**Cost takeaway:** Prefer **Cloud Run** or **ephemeral disk** so clone storage is not billed as persistent storage. The in-app cleanup ensures you don’t accumulate data between requests.

### 3.3 Failure handling

- If `Directory.Delete` fails (e.g. file locks), the exception is logged and the request still completes. In Cloud Run the instance (and its filesystem) is recycled, so orphaned dirs are not long-lived. On GKE/GCE, you can add a separate cleanup job if needed.

---

## 4. Building and running the container

### 4.1 Build image (from repo root)

```bash
docker build -t codecontextservice:latest .
```

The Dockerfile uses a multi-stage build:

- **Build stage**: `mcr.microsoft.com/dotnet/sdk:8.0`; publishes with **`-r linux-x64`** so LibGit2Sharp native binaries (`runtimes/linux-x64/native`) are included in the output.
- **Runtime stage**: `mcr.microsoft.com/dotnet/sdk:8.0` (not the smaller `aspnet` image) because **Roslyn MSBuildWorkspace** and **MSBuildLocator** require MSBuild at runtime to load solutions and projects. Without the SDK in the container, analysis would fail with "No instances of MSBuild could be detected" or "SDK could not be found".

Env defaults in the image:

- `RepoClone__CloneRootPath=/tmp/repo-clones`
- `ASPNETCORE_URLS=http://0.0.0.0:8080`
- `ASPNETCORE_ENVIRONMENT=Production`

### 4.2 Run locally (example)

```bash
docker run -p 8080:8080 \
  -e JwtSettings__Key="your-64-char-hex-key" \
  -e "UserCredentials__0__Username=admin" \
  -e "UserCredentials__0__Password=YourPassword" \
  -e "UserCredentials__0__Role=Admin" \
  codecontextservice:latest
```

---

## 5. Deploying to Google Cloud Run

### 5.1 Store JWT key in Secret Manager

```bash
# Create secret (one-time)
echo -n "your-64-char-hex-jwt-signing-key" | gcloud secrets create codecontext-jwt-key --data-file=-

# Grant Cloud Run access (replace PROJECT_ID and REGION)
gcloud secrets add-iam-policy-binding codecontext-jwt-key \
  --member="serviceAccount:PROJECT_NUMBER-compute@developer.gserviceaccount.com" \
  --role="roles/secretmanager.secretAccessor" \
  --project=PROJECT_ID
```

### 5.2 Build and push image to Artifact Registry

```bash
export PROJECT_ID=your-gcp-project
export REGION=us-central1
export IMAGE=$REGION-docker.pkg.dev/$PROJECT_ID/cloud-run-source-deploy/codecontextservice

gcloud auth configure-docker $REGION-docker.pkg.dev --quiet
docker build -t $IMAGE .
docker push $IMAGE
```

### 5.3 Deploy to Cloud Run

```bash
gcloud run deploy codecontextservice \
  --image $IMAGE \
  --region $REGION \
  --platform managed \
  --allow-unauthenticated \
  --set-env-vars "ASPNETCORE_ENVIRONMENT=Production,RepoClone__CloneRootPath=/tmp/repo-clones" \
  --set-secrets "JwtSettings__Key=codecontext-jwt-key:latest" \
  --set-env-vars "UserCredentials__0__Username=admin,UserCredentials__0__Role=Admin" \
  --set-secrets "UserCredentials__0__Password=codecontext-admin-password:latest"
```

- For multiple users, use a single secret containing a JSON array and map it to a config key, or add more `UserCredentials__N__*` env vars / secrets.
- Use `--no-allow-unauthenticated` and IAM if you want to restrict who can invoke the service.

### 5.4 Memory and CPU

- Roslyn and repo clones can be memory-heavy. Consider `--memory=2Gi` and `--cpu=2` if you see OOM or timeouts on large repos.

### 5.5 Health check

- The app exposes **`GET /health`** (no auth). Use it for Cloud Run liveness/readiness or load balancer health checks.

---

## 6. Other GCP options (brief)

- **GKE**: Use the same Docker image; set `RepoClone__CloneRootPath` to a path on an ephemeral volume (e.g. emptyDir) in the pod spec. No persistent volume needed if you rely on per-request cleanup.
- **GCE (VM)**: Run the container or the app directly; set `RepoClone__CloneRootPath` to a directory on local/ephemeral disk if available, or accept a small persistent disk and rely on cleanup + optional cron cleanup of old dirs.

---

## 7. Checklist before production

- [ ] `JwtSettings__Key` set from Secret Manager (or secure env), never from appsettings.
- [ ] User credentials from secrets or env, not hardcoded.
- [ ] `RepoClone__CloneRootPath` set to an ephemeral path (e.g. `/tmp/repo-clones`) on Cloud Run / ephemeral disk.
- [ ] `ASPNETCORE_ENVIRONMENT=Production`.
- [ ] Container listens on `0.0.0.0:8080` (or the port your platform expects).
- [ ] Swagger disabled in production (current code only enables it in Development).
- [ ] HTTPS and auth in front of the service (Cloud Run or load balancer).

---

## 8. Summary

- **Config**: All secrets and clone root via environment variables (and GCP Secret Manager where appropriate). Production startup fails if JWT key is missing or placeholder.
- **Storage**: Clones go to a configurable root; each analysis cleans up its clone in a `finally` block. Use Cloud Run or ephemeral disk to avoid paying for persistent storage and keep the service “tidy” after each run.
