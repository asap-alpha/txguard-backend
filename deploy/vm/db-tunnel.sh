#!/usr/bin/env bash
# Open a local tunnel to the Aiven Postgres via the deployment VM.
#
# Why: Aiven's IP allowlist contains only the VM's address. Your own IP is
# ISP-assigned and changes, so allowlisting it would break intermittently and
# force you back into the Aiven console. Tunnelling means the connection reaches
# Aiven *from the VM*, so it works from any network without touching the filter.
#
#   ./db-tunnel.sh                 # opens localhost:15432 -> Aiven, stays in foreground
#   psql "host=127.0.0.1 port=15432 dbname=defaultdb user=avnadmin sslmode=require"
#
# Ctrl-C closes the tunnel.

set -euo pipefail

VM_USER="${VM_USER:-root}"
VM_HOST="${VM_HOST:-188.166.43.238}"
SSH_KEY="${SSH_KEY:-$HOME/.ssh/txguard_vm}"
LOCAL_PORT="${LOCAL_PORT:-15432}"

# Read the Aiven host/port out of the VM's .env so there is a single source of truth.
URI=$(ssh -i "$SSH_KEY" -o BatchMode=yes "$VM_USER@$VM_HOST" \
  "grep '^AIVEN_POSTGRES_URI=' /opt/txguard/backend/deploy/vm/.env | cut -d= -f2-")

if [[ -z "$URI" ]]; then
  echo "Could not read AIVEN_POSTGRES_URI from the VM's .env" >&2
  exit 1
fi

HOST=$(python3 -c "import urllib.parse,sys;print(urllib.parse.urlparse(sys.argv[1]).hostname)" "$URI")
PORT=$(python3 -c "import urllib.parse,sys;print(urllib.parse.urlparse(sys.argv[1]).port)" "$URI")

echo "Tunnelling 127.0.0.1:${LOCAL_PORT} -> ${HOST}:${PORT} via ${VM_HOST}"
echo
echo "  psql \"host=127.0.0.1 port=${LOCAL_PORT} dbname=defaultdb user=avnadmin sslmode=require\""
echo
echo "Ctrl-C to close."

exec ssh -i "$SSH_KEY" -N -L "${LOCAL_PORT}:${HOST}:${PORT}" "$VM_USER@$VM_HOST"
