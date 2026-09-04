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
| `docker-compose.yml` | Sobe o `word-processor-server` + `pdf-service` + `caddy` (reverse proxy). |
| `Dockerfile` | Compila o Caddy com o plugin de rate limit (`caddy-ratelimit`). |
| `pdf-service/` | **Imagem nossa** (ASP.NET Core 8 + Syncfusion DocIORenderer) que atende só `POST /api/documenteditor/ConvertToPdf`: `.docx` entra, `application/pdf` sai. |
| `Caddyfile` | Escuta HTTP interno (modo túnel) + allowlist de CORS + respostas explícitas para origin bloqueada + allowlist de rotas/métodos + health/live e health/ready + rate limit + limite de upload + gate opcional por API key (auto-ativa) + headers de segurança + logs JSON. |
| `.env.server.example` | Modelo de variáveis (licença, API key opcional). Copie para `.env.server`. |
| `smoke-test.sh` | Checagens locais (health, rotas, métodos, CORS e as duas conversões: DOCX→SFDT e DOCX→PDF). Rode após `up -d`. |
| `.gitignore` | Impede comitar o `.env.server` (segredos). |
| `DEPLOY.md` | Passo a passo completo (túnel, verificação, segurança, smoke test, troubleshooting, apontar o CRM). |

## Conversão para PDF

A imagem oficial `syncfusion/word-processor-server` **não converte para PDF**: ela não
embarca o `DocIORenderer`. Pedir `format: "Pdf"` ao `Export` dela cai no caso padrão do
switch e devolve um `.doc` legado com `Content-Type: application/msword` (conferido em
03/09/2026 contra este servidor).

Por isso existe o `pdf-service`: um segundo container, imagem nossa, com uma única rota.

```
POST /api/documenteditor/ConvertToPdf     -> application/pdf
```

Aceita o `.docx` de duas formas:

```bash
# multipart, campo `files` — igual ao Import
curl -o saida.pdf -X POST https://SEU-TUNEL/api/documenteditor/ConvertToPdf \
  -F "files=@documento.docx"

# ou o arquivo cru no corpo (mais simples numa Edge Function)
curl -o saida.pdf -X POST "https://SEU-TUNEL/api/documenteditor/ConvertToPdf?fileName=documento.docx" \
  --data-binary @documento.docx
```

**O `word-processor-server` não foi tocado.** `Import`, `Export`, `ExportSFDT`,
`MailMerge`, `SpellCheck` e as demais rotas continuam saindo do binário oficial,
com o mesmo comportamento de sempre — o Caddy desvia só o caminho `ConvertToPdf`.

## TL;DR

```bash
cp .env.server.example .env.server                     # e preencha a licença
docker compose --env-file .env.server up -d --build    # (no Portainer: use env vars da UI)
cloudflared tunnel --url http://localhost:42811        # (ou ngrok http 42811)
# aponte VITE_SYNC_FUSION do CRM para https://SUA-URL-DO-TUNEL/api/documenteditor/
DOCX_FILE=/caminho/documento.docx ./smoke-test.sh      # valida tudo, PDF inclusive
```

Detalhes e checklist: **[DEPLOY.md](./DEPLOY.md)**.

Docs oficiais: <https://ej2.syncfusion.com/documentation/document-editor/server-deployment/word-processor-server-docker-image-overview>
