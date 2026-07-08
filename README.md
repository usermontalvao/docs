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
| `Caddyfile` | HTTPS automático + allowlist de CORS. |
| `.env.server.example` | Modelo de variáveis (domínio, e-mail ACME, licença). Copie para `.env.server`. |
| `.gitignore` | Impede comitar o `.env.server` (segredos). |
| `DEPLOY.md` | Passo a passo completo (DNS, TLS, verificação, apontar o CRM). |

## TL;DR

```bash
cp .env.server.example .env.server   # e preencha
docker compose up -d
# aponte VITE_SYNC_FUSION do CRM para https://SEU_DOMINIO/api/documenteditor/
```

Detalhes e checklist: **[DEPLOY.md](./DEPLOY.md)**.

Docs oficiais: <https://ej2.syncfusion.com/documentation/document-editor/server-deployment/word-processor-server-docker-image-overview>
