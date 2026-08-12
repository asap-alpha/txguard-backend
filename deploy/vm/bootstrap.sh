#!/usr/bin/env bash
# One-shot host preparation for a fresh Ubuntu 24.04 VPS (Contabo, DO, etc.).
# Idempotent: safe to re-run. Does NOT touch secrets or the Aiven allowlist —
# those are done by you (fill .env, add this VM's IP in the Aiven console).
#
#   ssh root@<VM_IP>
#   cd /opt/txguard/backend/deploy/vm
#   bash bootstrap.sh
#
# After it finishes: create .env (it offers to scaffold one), then:
#   docker compose --env-file .env up -d --build
set -euo pipefail

echo "==> 1/4  Swap file (2G)"
if ! swapon --show | grep -q /swapfile; then
  fallocate -l 2G /swapfile
  chmod 600 /swapfile
  mkswap /swapfile
  swapon /swapfile
  grep -q '^/swapfile' /etc/fstab || echo '/swapfile none swap sw 0 0' >> /etc/fstab
  echo "    swap enabled"
else
  echo "    swap already present, skipping"
fi

echo "==> 2/4  Firewall (ufw: 22, 80, 443)"
ufw allow 22/tcp  >/dev/null
ufw allow 80/tcp  >/dev/null
ufw allow 443/tcp >/dev/null
ufw --force enable >/dev/null
echo "    ufw active; only SSH + web are public"

echo "==> 3/4  Docker Engine + compose plugin"
if ! command -v docker >/dev/null 2>&1; then
  curl -fsSL https://get.docker.com | sh
else
  echo "    docker already installed, skipping"
fi
docker version  --format '    docker {{.Server.Version}}'  || true
docker compose version --short | sed 's/^/    compose /' || true

echo "==> 4/4  .env scaffold"
if [ -f .env ]; then
  echo "    .env already exists, leaving it untouched"
else
  cp .env.example .env
  # Auto-fill the two values that should just be random secrets.
  TEMPORAL_PW="$(openssl rand -base64 24)"
  JWT_KEY="$(openssl rand -base64 48)"
  sed -i "s#^TEMPORAL_DB_PASSWORD=.*#TEMPORAL_DB_PASSWORD=${TEMPORAL_PW}#" .env
  sed -i "s#^JWT_SIGNING_KEY=.*#JWT_SIGNING_KEY=${JWT_KEY}#" .env
  # Enable the demo panel + point the outage demo at the Aiven maintenance DB.
  grep -q '^ENABLE_DEMO_CONTROLS=' .env || echo 'ENABLE_DEMO_CONTROLS=true' >> .env
  grep -q '^DEMO_MAINT_DB=' .env       || echo 'DEMO_MAINT_DB=txguard_maint' >> .env
  echo "    created .env with random TEMPORAL_DB_PASSWORD + JWT_SIGNING_KEY"
  echo "    >>> STILL EDIT .env: API_DOMAIN, ACME_EMAIL, FRONTEND_ORIGIN, AIVEN_POSTGRES_URI"
fi

echo
echo "Host ready. Next:"
echo "  1) nano .env         # set API_DOMAIN, ACME_EMAIL, FRONTEND_ORIGIN, AIVEN_POSTGRES_URI"
echo "  2) confirm Aiven allowlist has this VM's IP, and DNS points here"
echo "  3) docker compose --env-file .env up -d --build"
