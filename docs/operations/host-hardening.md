# Host hardening: SSH, firewall, security updates (VLMS-RUN appendix)

Applies to the production Droplet. Run once at go-live, after `deploy/README.md` §1 and before the site is announced. Commands assume Ubuntu (see `runbook.md` §1, OS image Ubuntu 24.04 LTS (Philip, 27/10/2026)). Sources: [Ubuntu Server: OpenSSH](https://documentation.ubuntu.com/server/how-to/security/openssh-server/), [Ubuntu Server: firewalls (ufw)](https://documentation.ubuntu.com/server/how-to/security/firewalls/), [Ubuntu Server: automatic updates](https://documentation.ubuntu.com/server/how-to/software/automatic-updates/), [DigitalOcean Cloud Firewalls](https://docs.digitalocean.com/products/networking/firewalls/).

## 1. SSH

1. Create a named admin user with sudo and an SSH key; confirm key login works **in a second session** before changing anything below.
2. In `/etc/ssh/sshd_config.d/10-vlms.conf`:

   ```
   PermitRootLogin no
   PasswordAuthentication no
   KbdInteractiveAuthentication no
   AllowUsers <admin-user>
   ```

3. `sudo sshd -t && sudo systemctl reload ssh`, then test a fresh login before closing the old session.

Decisions:
- Admin username: `philip` (Philip, 27/10/2026).
- Restrict SSH to known source IPs (DigitalOcean Cloud Firewall) or leave open to all with key-only auth: open to all, key-only (Philip, 27/10/2026). The key is stored in Philip's password manager (not only on his PC), and a second key is added from another device; if both are lost, DigitalOcean's Recovery Console with a root password reset from the control panel restores access ([DigitalOcean: Recovery Console](https://docs.digitalocean.com/products/droplets/how-to/recovery/recovery-console/)). A home broadband IP that changes makes IP restriction awkward; key-only auth is the minimum either way.
- fail2ban: not planned. With password auth off it adds little, and it is one more moving part. Not installed (Philip, 27/10/2026).

## 2. Firewall

Two layers: DigitalOcean Cloud Firewall (outside the Droplet, survives a misconfigured host) and `ufw` on the host.

Inbound allowed, both layers:

| Port | Why |
|---|---|
| 22/tcp | SSH (restrict source per §1 decision) |
| 80/tcp | Caddy: ACME HTTP challenge and the redirect to HTTPS |
| 443/tcp | Caddy: HTTPS |
| 443/udp | Caddy: HTTP/3 (optional; harmless to allow) |

Everything else inbound is denied. PostgreSQL (5432) and Kestrel (5000) listen on localhost only and must **never** be opened.

```sh
sudo ufw default deny incoming
sudo ufw default allow outgoing
sudo ufw allow OpenSSH
sudo ufw allow 80/tcp
sudo ufw allow 443
sudo ufw enable
sudo ufw status verbose
```

Check from outside: `nmap -Pn <droplet-ip>` should show only 22, 80 and 443.

Outbound must reach: Let's Encrypt (ACME), `login.microsoftonline.com` and `graph.microsoft.com` (email), the R2 endpoint (backups), and the Ubuntu/Microsoft/Caddy package repositories.

## 3. Unattended security upgrades

```sh
sudo apt install unattended-upgrades
sudo dpkg-reconfigure -plow unattended-upgrades   # answer Yes
```

The default configuration installs security updates only. Packages from the Caddy and Microsoft repositories are not upgraded automatically: update them by hand in the monthly check (`sudo apt update && apt list --upgradable`), restarting `vlms-web.service` after a .NET runtime update.

Automatic reboot (for kernel updates): enabled at 03:30 (Philip, 27/10/2026). Recommendation: enable it at a quiet time, e.g. in `/etc/apt/apt.conf.d/50unattended-upgrades`:

```
Unattended-Upgrade::Automatic-Reboot "true";
Unattended-Upgrade::Automatic-Reboot-Time "04:00";
```

04:00 is local server time; the Droplet's clock should be UTC (`timedatectl`), which puts this after the 02:00, 02:30 and 03:00 UTC jobs. All VLMS units are enabled and come back by themselves after a reboot. If automatic reboot stays off, check for `/var/run/reboot-required` monthly (`runbook.md` §3).

## 4. Other baseline items

- Server timezone UTC: `sudo timedatectl set-timezone Etc/UTC`, so logs, timers and reboot times all read in one clock.
- DigitalOcean Droplet backups/snapshots (paid add-on): not taken (Philip, 27/10/2026). Not needed for data (the R2 backups cover it) but would speed up a full rebuild.
- Two-factor authentication on the DigitalOcean, Cloudflare, Hostinger and Microsoft 365 admin accounts. Anyone who controls these controls the service.
