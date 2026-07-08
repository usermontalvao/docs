# Caddy com o plugin de rate limit (não vem na imagem oficial).
# Build em dois estágios: xcaddy compila o binário, depois copiamos pra imagem final.
FROM caddy:2-builder AS builder
RUN xcaddy build --with github.com/mholt/caddy-ratelimit

FROM caddy:2
COPY --from=builder /usr/bin/caddy /usr/bin/caddy

# Embute o Caddyfile na imagem (evita bind mount, que quebra no Portainer quando o
# arquivo não está junto do compose). Editou o Caddyfile? Rebuild da imagem.
COPY Caddyfile /etc/caddy/Caddyfile
