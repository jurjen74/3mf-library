---
title: Proxmox
description: Run 3MF Library on Proxmox VE in a small Debian VM (recommended) or an LXC container.
---

On Proxmox VE the simplest and most robust setup is a small **Debian VM** with Docker. An LXC container also works,
but needs a few extra settings.

## A Debian VM (recommended)

1. **Download** the Debian netinst ISO (Datacenter → your storage → ISO Images → Download from URL, with the link from
   [debian.org](https://www.debian.org/distrib/netinst)).
2. **Create the VM:**
   - 2 cores, **4 GB** memory, 16 GB disk (enough for the system, the image and the preview cache);
   - network on your LAN bridge (`vmbr0`);
   - optionally a fixed address via a DHCP reservation on your router.
3. **Install Debian**, choosing only *SSH server* and *standard system utilities* (no desktop).
4. **Install the site** with [Docker Compose on Linux](/install/linux/).
5. **Models on a NAS:** use the [NFS add-on](/guides/nas/). The VM mounts the share itself; nothing is needed on the
   Proxmox host.

Add the VM to a backup job (Datacenter → Backup). The important part is `data/users.db`; see
[Updating and backups](/guides/updating/).

## An LXC container

Docker inside LXC works with these settings:

1. Create a **Debian** container (unprivileged is fine) with 2 cores, 2–4 GB memory and 8 GB disk.
2. Options → **Features**: enable **nesting** (and **keyctl** for an unprivileged container).
3. **Models:** an unprivileged container can't mount NFS itself. Mount the share on the Proxmox host (Datacenter →
   Storage → Add → NFS, or `/etc/fstab`) and pass it into the container, read-only:

   ```bash
   pct set <container id> -mp0 /mnt/pve/3d-prints,mp=/srv/3d-prints,ro=1
   ```

   In an unprivileged container, user 1000 is user 101000 on the host: the files must be readable for that user (or
   for everyone).
4. Inside the container, follow [Docker Compose on Linux](/install/linux/) with `LIBRARY_DIR=/srv/3d-prints`.

If something doesn't start in LXC, the VM route avoids these details.
