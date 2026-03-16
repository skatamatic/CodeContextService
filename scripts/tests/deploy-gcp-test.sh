#!/usr/bin/env bash
# Tests for deploy-gcp.sh: run with --dry-run and assert expected commands appear in output.
# Run from repo root: ./scripts/tests/deploy-gcp-test.sh

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
cd "$ROOT_DIR"

PROJECT_ID="${PROJECT_ID:-test-project-123}"
export PROJECT_ID
export CODECONTEXT_JWT_KEY="test-jwt-key-must-be-at-least-32-characters-long"
export CODECONTEXT_ADMIN_PASSWORD="test-admin-password"

OUTPUT=$(mktemp)
trap "rm -f $OUTPUT" EXIT

echo "Running deploy-gcp.sh --dry-run..."
./scripts/deploy-gcp.sh --dry-run > "$OUTPUT" 2>&1 || { echo "FAIL: exit code $?"; cat "$OUTPUT"; exit 1; }

echo "Checking output for expected commands..."

check() { if grep -q "$1" "$OUTPUT"; then echo "  OK: $1"; else echo "  FAIL: $1"; cat "$OUTPUT"; exit 1; fi; }

check "Using PROJECT_ID=$PROJECT_ID"
check "\[DRY RUN\] gcloud config set project"
check "\[DRY RUN\] gcloud services enable"
check "run.googleapis.com"
check "\[DRY RUN\] gcloud artifacts repositories create"
check "\[DRY RUN\] gcloud secrets create codecontext-jwt-key"
check "\[DRY RUN\] gcloud secrets add-iam-policy-binding"
check "codecontext-admin-password"
check "\[DRY RUN\] gcloud auth configure-docker"
check "\[DRY RUN\] docker build"
check "\[DRY RUN\] docker push"
check "\[DRY RUN\] gcloud run deploy"
check "codecontextservice"
check "--port 8080"
check "--memory 2Gi"
check "JwtSettings__Key=codecontext-jwt-key:latest"
check "UserCredentials__0__Password=codecontext-admin-password:latest"
check "RepoClone__CloneRootPath=/tmp/repo-clones"
check "Deployment complete"
check "Service URL:"
check "/health"

echo ""
echo "All checks passed."
