# Deploy — Servidor de conversão DOCX (Syncfusion)

Servidor próprio que converte DOCX ↔ SFDT para o editor do CRM.
Substitui o endpoint **demo público** do Syncfusion (`document.syncfusion.com`), que
faz throttle por IP (erros 403) e recebe os documentos dos clientes em servidor de terceiros.

Só isto vai para Docker. O CRM continua como frontend estático no Render, e o Supabase
continua cuidando de auth, banco, storage e edge functions.

---

## Arquitetura (modo túnel)

```
Browser do usuário (CRM no Render)
        │  POST https://SEU-TUNEL/api/documenteditor/Import   (DOCX)
        ▼
   Túnel (Cloudflare/ngrok)  ← termina o HTTPS público
        │  http://localhost:42811   (porta aleatória, só no host)
        ▼
   Caddy :8080  (CORS + rate limit + limite de upload)
        ▼
   word-processor-server:80   (conversor Syncfusion)
        │
        └──────  SFDT (JSON)  ──────►  volta pelo mesmo caminho
```

O host **não** abre porta pública: o Caddy escuta só em `127.0.0.1:42811` e quem
expõe pra internet (com HTTPS) é o túnel.

---

## Pré-requisitos

- [ ] Um host com Docker + Docker Compose (`docker --version`, `docker compose version`).
      Pode ser uma VPS pequena OU até uma máquina sem IP público (é o caso do túnel).
- [ ] Um túnel configurado: **Cloudflare Tunnel** (`cloudflared`) ou **ngrok**.
- [ ] Uma licença Syncfusion que cubra o **Word Processor server-side** (Document Processing / DocIO).
- [ ] Nenhuma porta 80/443 precisa ser aberta no firewall — o túnel faz a saída.

---

## Passo a passo

### 1. Subir os arquivos no host
- [ ] Copie a pasta para o host (git clone, `scp` ou rsync).
- [ ] Dentro dela:
      ```bash
      cp .env.server.example .env.server
      nano .env.server        # preencha SYNCFUSION_LICENSE_KEY (DOCX_API_KEY é opcional)
      ```

### 2. Ajustar CORS
- [ ] Edite o `Caddyfile`, bloco `map {header.Origin} ...`, e deixe **apenas** os
      domínios reais do seu CRM na allowlist (ex.: `https://crm-advogado.onrender.com`).
      A Origin é sempre o domínio do CRM — **não** o domínio do túnel.

### 3. (Opcional) Escolher outra porta aleatória
- [ ] A porta do host é **42811** (em `docker-compose.yml`, `127.0.0.1:42811:8080`).
      Para trocar, mude só o número da esquerda. A interna `:8080` não precisa mexer.

### 4. Subir os containers
- [ ] ```bash
      docker compose up -d --build   # --build compila o Caddy com o plugin de rate limit
      docker compose ps              # os dois containers "running"
      docker compose logs -f caddy
      ```

### 5. Verificar localmente (antes do túnel)
- [ ] Conversão real (com um DOCX qualquer no host):
      ```bash
      curl -s -o /dev/null -w "%{http_code}\n" \
        -X POST http://localhost:42811/api/documenteditor/Import \
        -F "files=@teste.docx;type=application/vnd.openxmlformats-officedocument.wordprocessingml.document"
      ```
      Esperado: **200**. Se a licença estiver inválida, o corpo avisa.

### 6. Ligar o túnel
- **Cloudflare Tunnel** (recomendado — dá um subdomínio HTTPS estável):
  ```bash
  cloudflared tunnel --url http://localhost:42811
  ```
  ou, com túnel nomeado, no `config.yml`:
  ```yaml
  ingress:
    - hostname: docx.seudominio.com.br
      service: http://localhost:42811
    - service: http_status:404
  ```
  > Para o rate limit por IP funcionar atrás do Cloudflare, troque no `Caddyfile`
  > a `key` de `{http.request.header.X-Forwarded-For}` para `{http.request.header.Cf-Connecting-Ip}`.

- **ngrok:**
  ```bash
  ngrok http 42811
  ```
- [ ] Anote a URL pública HTTPS que o túnel devolve (ex.: `https://docx.seudominio.com.br`
      ou `https://xxxx.ngrok-free.app`).

### 7. Apontar o CRM para o túnel
- [ ] No `.env` do CRM (e nas envvars do Render), troque:
      ```
      VITE_SYNC_FUSION=https://SUA-URL-DO-TUNEL/api/documenteditor/
      ```
      (caminho `/api/documenteditor/`, **não** `/functions/v1/...`).
- [ ] Adicione a URL do túnel na allowlist de CORS? **Não precisa** — o CORS filtra a
      Origin (domínio do CRM), não o destino.
- [ ] **Redeploy** do frontend no Render (o Vite só lê env no build).
- [ ] Abra o editor e teste abrir um DOCX (inclusive o `KIT CONSUMIDOR.docx`).

---

## Operação

- **Logs:** `docker compose logs -f word-processor-server`
- **Reiniciar:** `docker compose restart`
- **Atualizar:** `docker compose build --pull && docker compose pull && docker compose up -d`
  (o Caddy é uma imagem custom com plugin de rate limit — precisa de `build`, não só `pull`).
- **Parar:** `docker compose down`
- **Recursos:** limite inicial de 1 vCPU / 1 GB no compose; suba se sentir lentidão em DOCX grandes.

---

## Segurança

Em modo túnel o Caddy escuta só em `127.0.0.1` — o host **não** expõe porta pública, e o
túnel (Cloudflare/ngrok) fica na frente com as proteções dele (DDoS, etc.). Além disso, o
`Caddyfile` já traz:

- **Rate limit por IP:** 120 req/min (plugin `caddy-ratelimit`, compilado via `Dockerfile`).
  ⚠️ Atrás de túnel, o IP real vem num header encaminhado — ajuste a `key` do `rate_limit`
  (`Cf-Connecting-Ip` p/ Cloudflare, `X-Forwarded-For` p/ ngrok), senão o limite vira global.
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
