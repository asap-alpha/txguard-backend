# TxGuard — VPS Deployment Handoff

A runbook for deploying TxGuard onto a fresh Ubuntu 24.04 VPS. It is written for the
**person who owns the VPS** ("you" below). A few steps can only be done by the **project
owner** (who controls the Aiven database and the Vercel frontend) — those are marked
**[OWNER]**. Coordinate with them on those.

---

## What you're deploying

One Docker Compose stack on a single VPS:

- **Temporal** workflow engine + its own private Postgres (cluster state only)
- **TxGuard API** (.NET 8) — also runs the in-process Temporal worker
- **Caddy** — public edge, automatic HTTPS via Let's Encrypt

The **application database is NOT on this VPS** — it's a managed Aiven Postgres reached
over the internet. That's why the Aiven allowlist step matters (see Troubleshooting).

## Requirements

- Ubuntu **24.04** VPS, **2 GB RAM / 2 vCPU / 20 GB** minimum, with a **static public IPv4**
- Root SSH access to it
- A **domain or DuckDNS subdomain** that you can point at this VPS's IP
- From the **[OWNER]**: the `AIVEN_POSTGRES_URI` (database connection string) and the
  Vercel frontend URL

---

## Responsibility split

| Step | Who |
|---|---|
| Provision VPS, host prep, run the stack | **You** (VPS owner) |
| Point DNS at the VPS IP | Whoever controls the domain |
| **Add the VPS IP to the Aiven allowlist** | **[OWNER]** — you cannot do this |
| **Provide the `AIVEN_POSTGRES_URI` secret** | **[OWNER]** |
| Update Vercel `VITE_API_BASE` + redeploy frontend | **[OWNER]** |

---

## Step 1 — Get the code onto the VPS

The code is **not fully in Git** (it has un-pushed changes), so don't `git clone` — use the
copy the owner gives you. Two ways:

**A. Owner rsyncs their working tree to your VPS** (owner runs, from their Mac):
```bash
rsync -avz --delete \
  --exclude 'bin' --exclude 'obj' --exclude '.git' --exclude '.vs' --exclude '.env' \
  /path/to/txguard-app/backend/  root@<VPS_IP>:/opt/txguard/backend/
```

**B. Owner sends a tarball**, you upload and extract:
```bash
# owner:  tar czf backend.tgz --exclude bin --exclude obj --exclude .git backend/
# you:
mkdir -p /opt/txguard && tar xzf backend.tgz -C /opt/txguard/
```

Either way you end up with `/opt/txguard/backend/deploy/vm/` on the VPS.

## Step 2 — Prepare the host (one script)

```bash
ssh root@<VPS_IP>
cd /opt/txguard/backend/deploy/vm
bash bootstrap.sh
```

This adds swap, sets the firewall (only ports 22/80/443 public), installs Docker, and
scaffolds `.env` with random `TEMPORAL_DB_PASSWORD` and `JWT_SIGNING_KEY`. It's idempotent —
safe to re-run.

## Step 3 — [OWNER] Add this VPS IP to Aiven

**Ask the owner to do this — the deploy WILL time out on the database without it.**

> Aiven console → the Postgres service → **Overview → Allowed IP addresses** →
> add `<VPS_IP>/32` → Save. Remove any old/dead server IP.

Confirm it worked from the VPS (host/port are in the Aiven URI):
```bash
nc -zv <aiven-host> <aiven-port>
# "succeeded" = good.   "timed out" = IP not allowlisted yet.
```

## Step 4 — Point DNS at the VPS

- DuckDNS: `curl "https://www.duckdns.org/update?domains=<sub>&token=<token>&ip=<VPS_IP>"`
- Real domain: set the **A record** to `<VPS_IP>`

Verify: `dig +short <your-domain>` returns `<VPS_IP>`. Caddy can't get a TLS cert until this
resolves.

## Step 5 — Finish `.env` and launch

```bash
nano .env
```
Fill these (the random secrets are already set by bootstrap):
- `API_DOMAIN` — your domain from Step 4
- `ACME_EMAIL` — your email (Let's Encrypt notices)
- `FRONTEND_ORIGIN` — the Vercel URL **[OWNER provides]**
- `AIVEN_POSTGRES_URI` — the database URI **[OWNER provides]**

Leave `ENABLE_DEMO_CONTROLS=true` and `DEMO_MAINT_DB=txguard_maint` as scaffolded.

Then launch:
```bash
docker compose --env-file .env up -d --build
```

## Step 6 — Verify

```bash
docker compose ps                          # every service Up / healthy
docker compose logs api | grep -i worker   # worker connected; NOT "Frontend is not healthy"
curl -s https://<your-domain>/health       # 200 once the cert issues (~30s)
```

---

## Troubleshooting

**API logs show Postgres connection timeouts** → the VPS IP is not on the Aiven allowlist
(Step 3), or the wrong IP was added. This is the #1 cause. Re-check with the `nc` test.

**`curl https://<domain>/health` fails / no certificate** → DNS isn't pointing at this VPS
yet (Step 4), or ports 80/443 are blocked. Caddy needs port 80 reachable for the ACME
challenge. Check `docker compose logs caddy`.

**Worker log says "Frontend is not healthy yet"** → Temporal was still starting. The compose
healthcheck normally prevents this; if seen, `docker compose restart api`.

**Everything is up but the frontend can't reach the API** → the owner must set Vercel's
`VITE_API_BASE` to `https://<your-domain>` and redeploy, and confirm `FRONTEND_ORIGIN` in
`.env` matches the Vercel origin exactly (no trailing slash).

## Admin surfaces (optional)

The Temporal Web UI is bound to the VPS loopback only (never public). To view it, tunnel in:
```bash
ssh -L 8233:localhost:8233 root@<VPS_IP>
# then open http://localhost:8233
```
