#!/usr/bin/env bash
# Full GCP deployment for CodeContextService: APIs, Artifact Registry, secrets, IAM, build, push, Cloud Run deploy.
# Can be run from any directory (script switches to repo root). Requires: gcloud, docker.
#
# Optional env vars (if not set, JWT key and admin password are auto-generated and printed at the end):
#   CODECONTEXT_JWT_KEY         - JWT signing key (min 32 chars)
#   CODECONTEXT_ADMIN_PASSWORD  - Admin user password
#
# Optional env vars:
#   PROJECT_ID               - GCP project (default: gcloud config value)
#   REGION                    - e.g. us-central1 (default: us-central1)
#   SERVICE_NAME              - Cloud Run service name (default: codecontextservice)
#   REPO_NAME                 - Artifact Registry repo name (default: cloud-run-source-deploy)
#   VPC_CONNECTOR             - Optional Serverless VPC Access connector name
#   VPC_EGRESS                - Optional egress mode when VPC_CONNECTOR is set (default: private-ranges-only)
#
# Usage:
#   export CODECONTEXT_JWT_KEY="your-64-char-key"
#   export CODECONTEXT_ADMIN_PASSWORD="YourSecurePassword"
#   ./scripts/deploy-gcp.sh
#
# Or skip image build (use existing image):
#   ./scripts/deploy-gcp.sh --skip-build
#
# Dry run (echo commands, do not execute; PROJECT_ID must be set):
#   PROJECT_ID=my-project ./scripts/deploy-gcp.sh --dry-run

set -e

# Run from repo root (so docker build finds Dockerfile)
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$ROOT_DIR"

REGION="${REGION:-us-central1}"
SERVICE_NAME="${SERVICE_NAME:-codecontextservice}"
REPO_NAME="${REPO_NAME:-cloud-run-source-deploy}"
VPC_CONNECTOR="${VPC_CONNECTOR:-}"
VPC_EGRESS="${VPC_EGRESS:-private-ranges-only}"
SKIP_BUILD=false
DRY_RUN=""

for arg in "$@"; do
  case $arg in
    --skip-build) SKIP_BUILD=true ;;
    --dry-run)    DRY_RUN=1 ;;
  esac
done

run_cmd() {
  if [[ -n "$DRY_RUN" ]]; then
    echo "[DRY RUN] $*"
  else
    "$@"
  fi
}

if [[ -z "$PROJECT_ID" ]]; then
  if [[ -n "$DRY_RUN" ]]; then
    echo "Error: PROJECT_ID must be set when using --dry-run (e.g. PROJECT_ID=my-proj ./scripts/deploy-gcp.sh --dry-run)"
    exit 1
  fi
  PROJECT_ID=$(gcloud config get-value project 2>/dev/null || true)
  if [[ -z "$PROJECT_ID" ]]; then
    echo "Error: PROJECT_ID not set and gcloud project not configured. Set PROJECT_ID or run: gcloud config set project YOUR_PROJECT"
    exit 1
  fi
fi

echo "Using PROJECT_ID=$PROJECT_ID REGION=$REGION SERVICE_NAME=$SERVICE_NAME"

# --- Preflight: ensure gcloud is authenticated (needed for APIs, push, deploy) ---
if [[ -z "$DRY_RUN" ]]; then
  if ! gcloud auth print-access-token &>/dev/null; then
    echo "Error: Not authenticated with gcloud. Run: gcloud auth login"
    exit 1
  fi
  echo "gcloud authenticated as: $(gcloud config get-value account 2>/dev/null)"
fi

# --- Secrets: use env vars or auto-generate ---
if [[ -z "$CODECONTEXT_JWT_KEY" ]]; then
  if [[ -n "$DRY_RUN" ]]; then
    CODECONTEXT_JWT_KEY="dry-run-jwt-key-min-32-chars-long--------"
  else
    CODECONTEXT_JWT_KEY=$(openssl rand -base64 32 | tr -d '\n')
    echo "Generated new JWT signing key (min 32 chars)."
  fi
fi
if [[ -z "$CODECONTEXT_ADMIN_PASSWORD" ]]; then
  if [[ -n "$DRY_RUN" ]]; then
    CODECONTEXT_ADMIN_PASSWORD="dry-run-admin-password"
  else
    CODECONTEXT_ADMIN_PASSWORD=$(openssl rand -base64 24 | tr -d '\n')
    echo "Generated new admin password."
  fi
fi
if [[ ${#CODECONTEXT_JWT_KEY} -lt 32 ]]; then
  echo "Error: JWT key must be at least 32 characters"
  exit 1
fi

run_cmd gcloud config set project "$PROJECT_ID"

# --- Enable APIs (can take 1-2 minutes; not a hang) ---
echo "Enabling APIs (this may take 1-2 minutes)..."
run_cmd gcloud services enable run.googleapis.com artifactregistry.googleapis.com secretmanager.googleapis.com cloudbuild.googleapis.com --quiet
[[ -z "$DRY_RUN" ]] && echo "  APIs enabled."

# --- Artifact Registry ---
echo "Ensuring Artifact Registry repository..."
if [[ -n "$DRY_RUN" ]] || ! gcloud artifacts repositories describe "$REPO_NAME" --location="$REGION" &>/dev/null; then
  run_cmd gcloud artifacts repositories create "$REPO_NAME" --repository-format=docker --location="$REGION" --description="Docker images for Cloud Run"
else
  echo "  Repository $REPO_NAME already exists"
fi

# --- Secrets ---
JWT_SECRET="codecontext-jwt-key"
ADMIN_SECRET="codecontext-admin-password"

if [[ -n "$DRY_RUN" ]]; then
  echo "Creating secret: $JWT_SECRET"
  echo "[DRY RUN] gcloud secrets create $JWT_SECRET --data-file=-"
  echo "Adding new version to $ADMIN_SECRET"
  echo "[DRY RUN] gcloud secrets versions add $ADMIN_SECRET --data-file=-"
else
  if ! gcloud secrets describe "$JWT_SECRET" &>/dev/null; then
    echo "Creating secret: $JWT_SECRET"
    echo -n "$CODECONTEXT_JWT_KEY" | gcloud secrets create "$JWT_SECRET" --data-file=-
  else
    echo "Adding new version to $JWT_SECRET"
    echo -n "$CODECONTEXT_JWT_KEY" | gcloud secrets versions add "$JWT_SECRET" --data-file=-
  fi
  if ! gcloud secrets describe "$ADMIN_SECRET" &>/dev/null; then
    echo "Creating secret: $ADMIN_SECRET"
    echo -n "$CODECONTEXT_ADMIN_PASSWORD" | gcloud secrets create "$ADMIN_SECRET" --data-file=-
  else
    echo "Adding new version to $ADMIN_SECRET"
    echo -n "$CODECONTEXT_ADMIN_PASSWORD" | gcloud secrets versions add "$ADMIN_SECRET" --data-file=-
  fi
fi

# --- IAM ---
echo "Granting Cloud Run service account access to secrets..."
if [[ -n "$DRY_RUN" ]]; then
  PROJECT_NUMBER="123456789"
else
  PROJECT_NUMBER=$(gcloud projects describe "$PROJECT_ID" --format='value(projectNumber)')
fi
SA="${PROJECT_NUMBER}-compute@developer.gserviceaccount.com"
for name in "$JWT_SECRET" "$ADMIN_SECRET"; do
  run_cmd gcloud secrets add-iam-policy-binding "$name" --member="serviceAccount:${SA}" --role="roles/secretmanager.secretAccessor" --quiet
done

# --- Image ---
IMAGE="${REGION}-docker.pkg.dev/${PROJECT_ID}/${REPO_NAME}/codecontextservice"

if [[ "$SKIP_BUILD" == "true" ]]; then
  echo "Skipping build (--skip-build). Using image: $IMAGE"
else
  echo "Configuring Docker for Artifact Registry..."
  run_cmd gcloud auth configure-docker "${REGION}-docker.pkg.dev" --quiet
  echo "Building and pushing image..."
  run_cmd docker build -t "$IMAGE" .
  run_cmd docker push "$IMAGE"
fi

# --- Deploy Cloud Run ---
echo "Deploying to Cloud Run..."
VPC_ARGS=()
if [[ -n "$VPC_CONNECTOR" ]]; then
  echo "Using VPC connector: $VPC_CONNECTOR (egress: $VPC_EGRESS)"
  VPC_ARGS+=(--vpc-connector "$VPC_CONNECTOR" --vpc-egress "$VPC_EGRESS")
fi
run_cmd gcloud run deploy "$SERVICE_NAME" \
  --image "$IMAGE" \
  --region "$REGION" \
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
  --set-secrets "JwtSettings__Key=${JWT_SECRET}:latest" \
  --set-secrets "UserCredentials__0__Password=${ADMIN_SECRET}:latest" \
  "${VPC_ARGS[@]}" \
  --quiet

if [[ -n "$DRY_RUN" ]]; then
  URL="https://${SERVICE_NAME}-xxxxx-${REGION}.a.run.app"
else
  URL=$(gcloud run services describe "$SERVICE_NAME" --region "$REGION" --format='value(status.url)')
  # Quick health check so user knows the app is responding
  if command -v curl &>/dev/null; then
    HTTP_CODE=$(curl -s -o /dev/null -w "%{http_code}" --max-time 15 "$URL/health" 2>/dev/null || echo "000")
    if [[ "$HTTP_CODE" == "200" ]]; then
      echo "  Health check: OK (HTTP 200)"
    else
      echo "  Health check: got HTTP $HTTP_CODE (service may still be starting)"
    fi
  fi
fi
echo ""
echo "Deployment complete."
echo "  Service URL: $URL"
echo "  Health:      $URL/health"
echo ""
echo "--- Credentials (save these for the UI and for any service that calls this API) ---"
echo "  Username:   admin"
echo "  Password:   $CODECONTEXT_ADMIN_PASSWORD"
echo "  JWT Key:    $CODECONTEXT_JWT_KEY"
echo "---"
echo ""
echo "Open the Service URL in your browser and log in with the username and password above."
