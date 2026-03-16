# CodeContextService

ASP.NET Core 8 Blazor Server app for PR/code analysis (Roslyn + GitHub clone).

## Run locally with Docker

```powershell
.\docker-run.ps1
```

Then open **http://127.0.0.1:8080** in your browser (use `127.0.0.1`, not `localhost`—on Windows, localhost can hang due to IPv6). Login: `admin` / `changeme`.

## Deployment (GCP)

From repo root run **`./scripts/deploy-gcp.sh`** (bash) or **`.\scripts\deploy-gcp.ps1`** (PowerShell); set `CODECONTEXT_JWT_KEY` and `CODECONTEXT_ADMIN_PASSWORD` first (or you’ll be prompted). For the full guide (services, env vars, secrets, storage, Cloud Run, and checklist), see **[deployment.md](deployment.md)**. Additional detail is in **[docs/GCP_DEPLOYMENT.md](docs/GCP_DEPLOYMENT.md)**.