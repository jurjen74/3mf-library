---
title: Reverse proxy and HTTPS
description: Use the site away from home behind Nginx Proxy Manager, Caddy, Traefik or nginx, with HTTPS and logins.
---

To use the library away from home, put it behind a reverse proxy that handles HTTPS. **Don't forward port 8085 to the
internet**: the proxy is the only way in from outside.

## How logins work with a proxy

- Anything that arrives **through the proxy always needs a login**, even when it claims to come from your home network.
- Only visitors that reach the site **directly** from `TRUSTED_NETWORKS` skip the login.
- Tell the site where the proxy is with `KNOWN_PROXIES`. It then uses the visitor's real address (for the login rate
  limit) and knows the connection is HTTPS (so cookies are marked secure).

```bash
# .env
KNOWN_PROXIES=192.168.1.20      # the proxy's address as the container sees it
```

Without `KNOWN_PROXIES`, `X-Forwarded-For` headers are ignored entirely, so nobody can pretend to be someone else.

## Nginx Proxy Manager

Add a **Proxy Host**:

- *Domain names:* `models.example.com`
- *Scheme* `http`, *Forward hostname/IP* the Docker host, *Forward port* `8085`
- *Cache Assets:* **off** (previews are private to logged-in users), *Block common exploits:* on,
  *Websockets support:* not needed
- *SSL:* request a Let's Encrypt certificate, with *Force SSL* and *HTTP/2* on

Set `KNOWN_PROXIES` to the address of the Nginx Proxy Manager host.

## Caddy

```text
models.example.com {
    reverse_proxy 192.168.1.30:8085
}
```

Caddy gets the certificate by itself. `KNOWN_PROXIES` is the Caddy host's address.

## Traefik (labels)

Add the labels to the service in your own `docker-compose.override.yml`:

```yaml
services:
  3mf-library:
    labels:
      - traefik.enable=true
      - traefik.http.routers.models.rule=Host(`models.example.com`)
      - traefik.http.routers.models.entrypoints=websecure
      - traefik.http.routers.models.tls.certresolver=letsencrypt
      - traefik.http.services.models.loadbalancer.server.port=8080
```

When Traefik and the site share a Docker network, `KNOWN_PROXIES` is Traefik's address on that network (fix it with
a static IP on the network).

## nginx

```nginx
server {
    listen 443 ssl http2;
    server_name models.example.com;
    # ssl_certificate / ssl_certificate_key ...

    client_max_body_size 1m;
    location / {
        proxy_pass http://192.168.1.30:8085;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_buffering off;          # folder ZIPs are streamed
    }
}
```

## Check it

Open the site through the proxy: you should get the login page, with a padlock in the address bar. Log in once to
make sure the cookie sticks. If logins keep failing behind the proxy, check `KNOWN_PROXIES` first; see
[Troubleshooting](/reference/troubleshooting/).
