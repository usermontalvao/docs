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

> ### Usando Portainer?
> - **Obrigatório:** deploy como **stack de "Git repository"** apontando para o repositório
>   desta pasta (`docs`). NÃO cole só o YAML no web editor — o `build: .` precisa do
>   `Dockerfile` **e** do `Caddyfile` no contexto (o Caddyfile é copiado pra dentro da imagem).
>   Colar só o compose = build sem os arquivos = os erros que você viu.
> - As variáveis vão na aba **"Environment variables"** do stack (não em `.env.server`):
>   - `SYNCFUSION_LICENSE_KEY` = sua licença
>   - `DOCX_API_KEY` = deixe vazio (ou uma chave, se for ligar o gate)
> - O Caddy é **imagem custom** (`build:`). Garanta que o Portainer **buildte** o Dockerfile
>   (não force "pull" da `docx-caddy-ratelimit:local`, que não existe em registry).
> - Editou o `Caddyfile` (ex.: CORS)? Faça **git push** e **redeploy com rebuild** — o
>   Caddyfile é embutido na imagem, então só recompilando ele muda.
> - Pule os passos 1 e 4 abaixo (que são a via CLI) — o resto (CORS, porta, túnel) vale igual.

### 1. Subir os arquivos no host (via CLI)
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
- [ ] Se você também roda o CRM localmente, inclua a origem local exata usada no navegador
      (ex.: `http://localhost:3000` ou `http://localhost:5173`). Sem isso, o conversor pode
      até responder `200`, mas o browser bloqueia a leitura por CORS.
- [ ] O Caddyfile é **embutido na imagem** (COPY no `Dockerfile`), então qualquer edição
      nele exige **rebuild** (`docker compose ... up -d --build`). Não há bind mount.

### 3. (Opcional) Escolher outra porta aleatória
- [ ] A porta do host é **42811** (em `docker-compose.yml`, `127.0.0.1:42811:8080`).
      Para trocar, mude só o número da esquerda. A interna `:8080` não precisa mexer.

### 4. Subir os containers (via CLI)
- [ ] ```bash
      # --env-file lê SYNCFUSION_LICENSE_KEY/DOCX_API_KEY; --build compila o Caddy com rate limit
      docker compose --env-file .env.server up -d --build
      docker compose ps              # os dois containers "running"
      docker compose logs -f caddy
      ```

### 5. Verificar localmente (antes do túnel)
- [ ] Liveness do proxy:
      ```bash
      curl -i http://localhost:42811/health/live
      ```
      Esperado: **200** com JSON simples.
- [ ] Readiness do proxy + upstream:
      ```bash
      curl -i http://localhost:42811/health/ready
      ```
      Esperado: **200** enquanto o Word Processor estiver acessível.
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
- **Health:** `curl http://localhost:42811/health/live` e `curl http://localhost:42811/health/ready`
- **Reiniciar:** `docker compose restart`
- **Atualizar (CLI):** `docker compose --env-file .env.server up -d --build --pull always`
  (o Caddy é imagem custom com plugin de rate limit — precisa de `build`, não só `pull`).
  No Portainer: use "Pull and redeploy" / "Update the stack" com rebuild.
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

1. Defina `DOCX_API_KEY=<uma-chave-aleatoria>` no `.env.server` (ou na aba de env do Portainer).
2. **Rebuild** da imagem do Caddy — o gate **auto-ativa** quando a chave não está vazia
   (não precisa mais editar o Caddyfile; o bloco `@unauthorized` já vive no arquivo e se
   liga sozinho). O valor é embutido na imagem no build, então trocar a chave exige rebuild.
3. No frontend (repo `CRMlaw`), faça o editor enviar o header. Já existe o gancho
   `beforeXmlHttpRequestSend` em `src/components/SyncfusionEditor.tsx`; adicione uma env
   `VITE_DOCX_API_KEY` e injete `{ 'X-Api-Key': import.meta.env.VITE_DOCX_API_KEY }` junto
   dos headers quando o `serviceUrl` apontar para o servidor docx.
4. `docker compose --env-file .env.server up -d --build` para recompilar e recarregar o Caddy.

Verifique com o smoke test: `DOCX_API_KEY=sua-chave ./smoke-test.sh` deve mostrar
`sem X-Api-Key -> 401` como **PASS**.

Se `DOCX_API_KEY` ficar vazio, o gate permanece **inativo** e nada quebra.
OPTIONS (preflight), a página de status e os health endpoints **nunca** exigem a chave.

---

## Smoke test local

Depois de `docker compose up -d` (e antes de mexer no túnel), rode o script de checagem.
Ele valida health/live, health/ready, a página de status, a allowlist de rotas e métodos,
as respostas de CORS bloqueado, o gate de API key (se ligado) e uma conversão real:

```bash
# Git Bash (Windows) ou shell do host Linux. Requer curl + base64.
./smoke-test.sh                                 # usa http://localhost:42811
BASE_URL=http://localhost:42811 ./smoke-test.sh # outra porta
DOCX_API_KEY=sua-chave ./smoke-test.sh          # também testa o gate de API key
```

Saída esperada: todas as linhas `[PASS]` e `Resultado: N ok / 0 falhas` (exit 0).
Qualquer `[FAIL]` aponta o que quebrou (ex.: conversão sem SFDT = licença inválida).

---

## Troubleshooting

| Sintoma | Causa provável | O que fazer |
|---|---|---|
| `health/live` responde, `health/ready` dá **502** | Word Processor caiu ou ainda subindo | `docker compose ps`; `docker compose logs word-processor-server`; aguarde o `start_period` (45s) |
| Conversão volta **200** mas sem `sfdt` no corpo | Licença Syncfusion ausente/inválida ou não cobre server-side | Confira `SYNCFUSION_LICENSE_KEY`; precisa cobrir **Document Processing / DocIO** |
| Editor do CRM: erro de **CORS** no console (mas curl dá 200) | Origin do CRM fora da allowlist | Adicione a Origin exata no bloco `map {header.Origin}` do Caddyfile e **rebuild** |
| Tudo volta **403 CORS origin not allowed** | Origin não bate (http vs https, com/sem `www`, porta) | A Origin é o domínio do **CRM**, não o do túnel; copie exatamente do DevTools |
| Requisições legítimas tomando **429** | Rate limit global porque a `key` não reflete o IP real | Ajuste a `key` do `rate_limit` (`Cf-Connecting-Ip` p/ Cloudflare, `X-Forwarded-For` p/ ngrok) |
| **401** inesperado na API | `DOCX_API_KEY` foi definida e o gate ativou | Envie `X-Api-Key` no frontend, ou esvazie a chave e rebuilde |
| DOCX grande falha/timeout | Passou do teto de upload ou do timeout | Suba `request_body max_size` e/ou `read_timeout`/`write_timeout` no Caddyfile |
| Alterou o Caddyfile e "não mudou nada" | Caddyfile é **embutido na imagem** (COPY) | Rebuild obrigatório: `docker compose up -d --build` |
| **404** em rotas que antes passavam | Allowlist de rotas: só `/api/documenteditor/*`, `/health/*`, `/status` | Use o caminho `/api/documenteditor/...`; outros são bloqueados de propósito |

Ver logs de acesso estruturados (JSON) do Caddy para diagnóstico fino:
```bash
docker compose logs -f caddy      # cada request vira uma linha JSON (status, método, path, duração)
```

---

## Notas

- O **spell-check do editor é local** (Hunspell pt-BR via WASM, em `src/components/local-spell-checker.ts`),
  então este servidor **não** precisa se preocupar com corretor ortográfico. Ele serve só para Import de DOCX.
- O proxy `syncfusion-proxy` do Supabase pode continuar existindo para outros usos, mas
  **deixa de ser o caminho do Import** do editor.
- O erro `400` em `profiles?select=petition_editor_theme_preference` é ambiente **sem a migration
  aplicada** — a migration `supabase/migrations/20260705000001_profiles_petition_editor_theme_preference.sql`
  já existe; basta aplicá-la no banco remoto. Não tem relação com este servidor.
