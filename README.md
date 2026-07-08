# docx/ — Servidor de conversão do editor (Syncfusion Word Processor)

Backend **isolado** que converte DOCX → SFDT (e volta) para o editor de documentos do CRM.
Roda em Docker, num host próprio, atrás de HTTPS. **Não** dockeriza o CRM — o app segue
estático no Render e o Supabase segue como está.

## Por que existe

O editor Syncfusion precisa de um web service para importar DOCX. Hoje o CRM usa o
**endpoint demo público** do Syncfusion, que:

- faz **throttle por IP** e devolve **403** para IPs de datacenter (por isso o editor falhava);
- é **apenas para avaliação**, sem SLA;
- recebe **documentos de clientes** num servidor de terceiros (problema de sigilo/LGPD).

Este serviço resolve os três pontos: confiável, seu, e com CORS restrito ao CRM.

## Arquivos

| Arquivo | O que é |
|---|---|
| `docker-compose.yml` | Sobe o `word-processor-server` + `caddy` (reverse proxy). |
| `Dockerfile` | Compila o Caddy com o plugin de rate limit (`caddy-ratelimit`). |
| `Caddyfile` | Escuta HTTP interno (modo túnel) + allowlist de CORS + rate limit + limite de upload + gate opcional por API key. |
| `.env.server.example` | Modelo de variáveis (domínio, ACME, licença, API key opcional). Copie para `.env.server`. |
| `.gitignore` | Impede comitar o `.env.server` (segredos). |
| `DEPLOY.md` | Passo a passo completo (DNS, TLS, verificação, segurança, apontar o CRM). |

## TL;DR

```bash
cp .env.server.example .env.server                     # e preencha a licença
docker compose --env-file .env.server up -d --build    # (no Portainer: use env vars da UI)
cloudflared tunnel --url http://localhost:42811        # (ou ngrok http 42811)
# aponte VITE_SYNC_FUSION do CRM para https://SUA-URL-DO-TUNEL/api/documenteditor/
```

Detalhes e checklist: **[DEPLOY.md](./DEPLOY.md)**.

Docs oficiais: <https://ej2.syncfusion.com/documentation/document-editor/server-deployment/word-processor-server-docker-image-overview>
