# TxGuard backend — single-VM deployment

Runs the Temporal server and the TxGuard API together on one always-on VM, with
application data on Aiven Postgres and the frontend on Vercel.

Written for a **DigitalOcean Droplet**, but nothing here is provider-specific beyond §1–§2 —
any Ubuntu VM with Docker works (Hetzner, Azure, GCP, a lab machine).

## Architecture

```
Browser ──HTTPS──> Vercel (frontend)
                        │
                        ▼  HTTPS (your API domain)
              ┌─────────────────────────────────────────┐
              │  Ubuntu VM                              │
              │                                         │
              │   caddy :80/:443  ── public edge, TLS   │
              │      │                                  │
              │      ▼  private docker network          │
              │   txguard-api :8080                     │
              │      │   (hosts the Temporal worker)    │
              │      ▼                                  │
              │   temporal :7233   ← NOT exposed        │
              │      │                                  │
              │      ▼                                  │
              │   temporal-postgres  (cluster state)    │
              └─────────────────────────────────────────┘
                        │
                        ▼  TLS
                 Aiven Postgres (application data)
```

**Why the API and Temporal share a host:** Temporal's gRPC frontend has *no authentication
by default*. Publishing port 7233 would let anyone start, signal, or terminate workflows.
Co-locating them keeps 7233 on a private Docker network, so no Temporal mTLS is needed.

**Why Temporal has its own Postgres:** Temporal persistence is high-churn cluster state
(history, timers, task queues). The Aiven free plan (1 GB storage, 20 connections) would be
exhausted by Temporal alone, leaving nothing for application data.

## 1. Create the Droplet (DigitalOcean)

**Create → Droplets**:

| Field | Value | Why |
|---|---|---|
| Region | **Amsterdam** | Same city as your Aiven Postgres — lowest possible DB latency |
| Image | **Ubuntu 24.04 (LTS)** | Docker's install script has packages for it |
| Type | **Basic → Regular (SSD)** | Premium AMD/NVMe costs more for no benefit here |
| Size | **$12/mo — 1 vCPU / 2 GB / 50 GB** | Runs the whole stack; see the swap note below |
| Authentication | **SSH Key** → paste `~/.ssh/txguard_vm.pub` | Avoids password auth entirely |

**On sizing.** At rest the stack uses roughly 0.8–1.1 GB (Temporal ~200–400 MB, its Postgres
~150–250 MB, the API ~150–250 MB, Caddy ~20 MB), so 2 GB is comfortable. The memory-hungry
step is `dotnet publish` during the image build — add swap **before** the first build:

```bash
fallocate -l 2G /swapfile && chmod 600 /swapfile
mkswap /swapfile && swapon /swapfile
echo '/swapfile none swap sw 0 0' >> /etc/fstab
```

Alternatively build the image elsewhere (locally or in CI) and pull it, so the Droplet never
compiles anything. The $24/mo 4 GB size avoids the issue entirely if you'd rather not bother.

Billing is hourly, so destroying and recreating the Droplet later doesn't waste the month —
but destroying it also destroys the Docker volumes (see the backup note in Operations).

## 2. Firewall

Use a **DigitalOcean Cloud Firewall** (free, under Networking → Firewalls) with **inbound**
rules for exactly three TCP ports, then attach it to the Droplet:

| Port | Source | Purpose |
|---|---|---|
| 22 | your IP (or all) | SSH |
| 80 | all | ACME HTTP challenge — required for certificate issuance |
| 443 | all | The API |

Do **not** open 7233 or 5432. Temporal and its database stay on the private Docker network.

DigitalOcean's Ubuntu images don't enable a restrictive local `ufw`/`iptables` policy by
default, so the Cloud Firewall is the only layer you need to configure.

## 3. Install Docker

DigitalOcean logs you in as **root** (no `sudo` needed):

```bash
ssh -i ~/.ssh/txguard_vm root@<droplet-ip>

curl -fsSL https://get.docker.com | sh
```

Optional hardening — run the stack as a non-root user instead:

```bash
adduser --disabled-password --gecos "" txguard
usermod -aG docker txguard
mkdir -p /home/txguard/.ssh && cp ~/.ssh/authorized_keys /home/txguard/.ssh/
chown -R txguard:txguard /home/txguard/.ssh && chmod 700 /home/txguard/.ssh
```

## 4. Point a domain at the VM

Caddy needs a real hostname to obtain a certificate — an IP alone won't work, and the Vercel
frontend can't call an HTTPS API with a self-signed cert. A free
[DuckDNS](https://duckdns.org) subdomain is sufficient: create e.g. `txguard.duckdns.org`
and set it to the server's public IPv4 (shown in the DigitalOcean console).

Verify before continuing — this is the most common failure point:

```bash
dig +short txguard.duckdns.org    # must print your VM's public IP
```

## 5. Deploy

```bash
git clone <your-repo> && cd <repo>/backend/deploy/vm
cp .env.example .env
nano .env                          # fill in every value

docker compose up -d --build       # first build takes a few minutes
docker compose logs -f api
```

Look for `Temporal worker started on queue txguard-transactions`. Then:

```bash
curl https://<your-domain>/health
# {"status":"ok","service":"TxGuard"}
```

## 6. Reach the Temporal UI

The UI is deliberately **not** published — it can terminate workflows and has no auth.
Add a loopback-only mapping to the `temporal-ui` service in `docker-compose.yml`:

```yaml
    ports:
      - "127.0.0.1:8080:8080"
```

Then tunnel to it over SSH (nothing is exposed publicly):

```bash
ssh -i ~/.ssh/txguard_vm -L 8233:localhost:8080 root@<droplet-ip> -N
# browse http://localhost:8233
```

## 7. Lock down Aiven (now that you have a fixed IP)

DigitalOcean assigns the Droplet a fixed public IPv4 for its lifetime — that fixed address is
what was missing when we deferred this decision earlier. In the Aiven console → service →
**Service settings → IP filters**, replace `0.0.0.0/0` with:

- `<droplet-ip>/32` — **and nothing else**

Deliberately do *not* allowlist your own machine. Residential IPs are ISP-assigned and
change, so that entry would break intermittently and send you back to the Aiven console.
Instead reach the database *through* the VM, which is already allowlisted:

```bash
./db-tunnel.sh    # opens 127.0.0.1:15432 -> Aiven via the VM
psql "host=127.0.0.1 port=15432 dbname=defaultdb user=avnadmin sslmode=require"
```

Aiven sees that connection as coming from the VM's address, so it works from any network.
Verified: `select host(inet_client_addr())` returns the VM's IP, not your laptop's.

The VM's IP is static for the life of the Droplet, but changes if you destroy and recreate
it — update the filter then, or you'll get timeouts that look like an application bug.

## 8. Point Vercel at the API

Set the frontend's API base URL to `https://<your-domain>`, and make sure `FRONTEND_ORIGIN`
in `.env` exactly matches the Vercel origin (scheme, host, **no trailing slash**) or the
browser will block requests at CORS.

## Operations

```bash
docker compose ps
docker compose logs -f api
docker compose pull && docker compose up -d --build   # update
docker compose down                                   # stop (data survives in volumes)
```

Back up Temporal's cluster state:

```bash
docker compose exec temporal-postgres pg_dumpall -U temporal > temporal-backup.sql
```

Application data is backed up by Aiven automatically.

## Known version skew

Your local stack (root `docker-compose.yml`) runs `temporalio/auto-setup:1.25.2`; this
deployment runs **1.29.1**. Both work with the .NET SDK (`Temporalio` 1.16.0), but aligning
them removes a dev/prod difference — consider bumping the local compose to 1.29.1.
