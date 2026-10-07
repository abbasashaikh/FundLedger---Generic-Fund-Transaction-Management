#!/usr/bin/env bash
# Deploy (or roll back to) an image tag for one environment on the VPS.
#
#   deploy.sh <staging|production> <image-tag>      deploy that tag
#   deploy.sh <staging|production> --rollback       redeploy the previous tag
#
# Migrations are NOT run here: CI runs them with the owner role before calling
# this script, so the VPS never holds database-owner credentials. Migrations are
# expand-only, so the previous image keeps working after a rollback (TR-091).
set -euo pipefail

ENV_NAME="${1:?usage: deploy.sh <staging|production> <tag|--rollback>}"
TARGET="${2:?usage: deploy.sh <staging|production> <tag|--rollback>}"
case "$ENV_NAME" in staging|production) ;; *) echo "unknown env: $ENV_NAME" >&2; exit 64 ;; esac

DIR="/opt/fundledger/${ENV_NAME}"
cd "$DIR"
[[ -f .env ]] || { echo "$DIR/.env missing" >&2; exit 1; }

current_tag="$(grep -E '^IMAGE_TAG=' .env | cut -d= -f2- || true)"
if [[ "$TARGET" == "--rollback" ]]; then
  [[ -s .previous_tag ]] || { echo "no previous tag recorded" >&2; exit 1; }
  TARGET="$(cat .previous_tag)"
  echo "Rolling back ${ENV_NAME}: ${current_tag:-none} -> ${TARGET}"
else
  echo "Deploying ${ENV_NAME}: ${current_tag:-none} -> ${TARGET}"
fi

[[ "$TARGET" =~ ^[A-Za-z0-9._-]{1,128}$ ]] || { echo "invalid tag" >&2; exit 64; }

set_tag() { sed -i "s/^IMAGE_TAG=.*/IMAGE_TAG=$1/" .env; }

docker pull "ghcr.io/abbasashaikh/fundledger-api:${TARGET}"
set_tag "$TARGET"
docker compose up -d --remove-orphans

# Health gate: the API must report ready through Caddy within ~60 s.
domain_var="API_DOMAIN_$(echo "$ENV_NAME" | tr '[:lower:]' '[:upper:]')"
domain="$(grep -E "^${domain_var}=" /opt/fundledger/edge/.env | cut -d= -f2-)"
for _ in $(seq 1 30); do
  if curl -fsS --max-time 5 "https://${domain}/health/ready" >/dev/null; then
    if [[ -n "${current_tag}" && "${current_tag}" != "${TARGET}" ]]; then
      echo "${current_tag}" > .previous_tag
    fi
    echo "OK: ${ENV_NAME} is healthy on ${TARGET}"
    docker image prune -f >/dev/null
    exit 0
  fi
  sleep 2
done

echo "Health check failed for ${TARGET}; restoring ${current_tag:-<none>}" >&2
if [[ -n "${current_tag}" ]]; then
  set_tag "$current_tag"
  docker compose up -d --remove-orphans
fi
exit 1
