# Deploy — Servidor de conversão DOCX (Syncfusion)

Servidor próprio que converte DOCX ↔ SFDT para o editor do CRM.
Substitui o endpoint **demo público** do Syncfusion (`document.syncfusion.com`), que
faz throttle por IP (erros 403) e recebe os documentos dos clientes em servidor de terceiros.

Só isto vai para Docker. O CRM continua como frontend estático no Render, e o Supabase
continua cuidando de auth, banco, storage e edge functions.

---

## Arquitetura

```
Browser do usuário (CRM no Render)
        │  POST .../api/documenteditor/Import   (DOCX)
        ▼
 docx.seudominio.com  ──►  Caddy (HTTPS + CORS)  ──►  word-processor-server:80
        ▲                                                     │
        └──────────────  SFDT (JSON)  ◄───────────────────────┘
```

---

## Pré-requisitos

- [ ] Um host Linux com IP público (VPS: Hetzner, DigitalOcean, Contabo, etc.). 1 vCPU / 1 GB RAM já roda.
- [ ] Docker + Docker Compose plugin instalados (`docker --version`, `docker compose version`).
- [ ] Portas **80** e **443** liberadas no firewall (necessárias para o TLS do Let's Encrypt).
- [ ] Uma licença Syncfusion que cubra o **Word Processor server-side** (Document Processing / DocIO).

---

## Passo a passo

### 1. DNS
- [ ] Crie um registro **A** (e **AAAA** se tiver IPv6) para um subdomínio dedicado,
      ex.: `docx.seudominio.com.br` → IP do host.
- [ ] Confirme a propagação: `dig +short docx.seudominio.com.br` deve retornar o IP do host.

### 2. Subir os arquivos no host
- [ ] Copie a pasta `docx/` para o host (git clone do repo, `scp`, ou rsync).
- [ ] Dentro dela:
      ```bash
      cp .env.server.example .env.server
      nano .env.server        # preencha DOCX_DOMAIN, ACME_EMAIL e SYNCFUSION_LICENSE_KEY
      ```

### 3. Ajustar CORS
- [ ] Edite o `Caddyfile`, bloco `map {header.Origin} ...`, e deixe **apenas** os
      domínios reais do seu CRM na allowlist (ex.: `https://crm-advogado.onrender.com`
      e o domínio customizado, se houver).

### 4. Subir os containers
- [ ] ```bash
      docker compose up -d --build   # --build compila o Caddy com o plugin de rate limit
      docker compose ps          # os dois containers devem estar "running"
      docker compose logs -f caddy   # acompanhe a emissão do certificado TLS
      ```
- [ ] A primeira subida compila a imagem do Caddy (xcaddy) e emite o certificado — leva ~1-2 min.

### 5. Verificar o serviço
- [ ] TLS ok:
      ```bash
      curl -I https://docx.seudominio.com.br/api/documenteditor/
      ```
- [ ] Teste real de conversão (com um DOCX qualquer no host):
      ```bash
      curl -s -o /dev/null -w "%{http_code}\n" \
        -X POST https://docx.seudominio.com.br/api/documenteditor/Import \
        -F "files=@teste.docx;type=application/vnd.openxmlformats-officedocument.wordprocessingml.document"
      ```
      Esperado: **200**. Se vier a licença inválida, o corpo da resposta avisa.
- [ ] Preflight/CORS ok (simulando o browser):
      ```bash
      curl -s -i -X OPTIONS https://docx.seudominio.com.br/api/documenteditor/Import \
        -H "Origin: https://crm-advogado.onrender.com" \
        -H "Access-Control-Request-Method: POST" | grep -i access-control
      ```
      Esperado: `Access-Control-Allow-Origin: https://crm-advogado.onrender.com`.

### 6. Apontar o CRM para o novo servidor
- [ ] No `.env` do CRM (e nas envvars do Render), troque:
      ```
      VITE_SYNC_FUSION=https://docx.seudominio.com.br/api/documenteditor/
      ```
      (repare que o caminho é `/api/documenteditor/`, **não** `/functions/v1/...`).
- [ ] **Redeploy** do frontend no Render (o Vite só lê env no build).
- [ ] Abra o editor no CRM e teste abrir um DOCX (inclusive o `KIT CONSUMIDOR.docx`).

---

## Operação

- **Logs:** `docker compose logs -f word-processor-server`
- **Reiniciar:** `docker compose restart`
- **Atualizar:** `docker compose build --pull && docker compose pull && docker compose up -d`
  (o Caddy é uma imagem custom com plugin de rate limit — precisa de `build`, não só `pull`).
- **Parar:** `docker compose down` (mantém os certificados no volume `caddy_data`)
- **Recursos:** limite inicial de 1 vCPU / 1 GB no compose; suba se sentir lentidão em DOCX grandes.

---

## Segurança

O endpoint é público (o CORS só barra browsers de outros sites, não `curl`). As defesas
que já vêm configuradas no `Caddyfile`:

- **Rate limit por IP:** 120 req/min por IP (plugin `caddy-ratelimit`, compilado via `Dockerfile`).
  Ajuste `events`/`window` no `Caddyfile` se um escritório grande atrás de um IP (NAT) esbarrar.
- **Limite de upload:** 30 MB por requisição (`request_body max_size`) — evita estourar a memória do conversor.
- **Headers de segurança:** `X-Content-Type-Options`, `Referrer-Policy`, remoção do header `Server`.

### (Opcional) Exigir X-Api-Key

Camada extra contra abuso casual. **Limitação honesta:** como o CRM é um SPA, a chave vai no
bundle do navegador e é legível — não protege contra atacante dedicado, só corta script bobo.
As defesas reais são o rate limit e o limite de tamanho acima.

Para ligar:

1. Defina `DOCX_API_KEY=<uma-chave-aleatoria>` no `.env.server`.
2. No `Caddyfile`, descomente o bloco `@unauthorized` / `handle @unauthorized`.
3. No frontend (repo `CRMlaw`), faça o editor enviar o header. Já existe o gancho
   `beforeXmlHttpRequestSend` em `src/components/SyncfusionEditor.tsx`; adicione uma env
   `VITE_DOCX_API_KEY` e injete `{ 'X-Api-Key': import.meta.env.VITE_DOCX_API_KEY }` junto
   dos headers quando o `serviceUrl` apontar para o servidor docx.
4. `docker compose up -d` para recarregar o Caddy.

Se `DOCX_API_KEY` ficar vazio, o gate permanece **inativo** e nada quebra.

---

## Notas

- O **spell-check do editor é local** (Hunspell pt-BR via WASM, em `src/components/local-spell-checker.ts`),
  então este servidor **não** precisa se preocupar com corretor ortográfico. Ele serve só para Import de DOCX.
- O proxy `syncfusion-proxy` do Supabase pode continuar existindo para outros usos, mas
  **deixa de ser o caminho do Import** do editor.
- O erro `400` em `profiles?select=petition_editor_theme_preference` é ambiente **sem a migration
  aplicada** — a migration `supabase/migrations/20260705000001_profiles_petition_editor_theme_preference.sql`
  já existe; basta aplicá-la no banco remoto. Não tem relação com este servidor.
