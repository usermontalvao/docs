# Caddy com o plugin de rate limit (não vem na imagem oficial).
# Build em dois estágios: xcaddy compila o binário, depois copiamos pra imagem final.
FROM caddy:2-builder AS builder
RUN xcaddy build --with github.com/mholt/caddy-ratelimit

FROM caddy:2
COPY --from=builder /usr/bin/caddy /usr/bin/caddy
