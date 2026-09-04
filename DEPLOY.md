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
        │
        ├── /api/documenteditor/ConvertToPdf ──►  pdf-service:8080   (imagem NOSSA)
        │                                          .docx ──► application/pdf
        │
        └── todo o resto ────────────────────►  word-processor-server:80
                                                   (conversor Syncfusion, intocado)
        │
        └──────  SFDT (JSON)  ──────►  volta pelo mesmo caminho
```

O host **não** abre porta pública: o Caddy escuta só em `127.0.0.1:42811` e quem
expõe pra internet (com HTTPS) é o túnel.

---

## Serviço de PDF (`pdf-service`)

### Por que um container separado, e não uma imagem customizada do Word Processor

A imagem oficial `syncfusion/word-processor-server` não embarca o `DocIORenderer`, que é
quem desenha Word → PDF. O caminho "natural" seria trocá-la pela nossa própria build do
projeto oficial [Word-Processor-Server-Docker](https://github.com/SyncfusionExamples/Word-Processor-Server-Docker)
com o `DocIORenderer` acrescentado. **Isso foi descartado por um motivo concreto:**

O exemplo público do GitHub tem 9 rotas. O que está rodando em produção tem 11 — ele
expõe `ExportSFDT` e `MailMerge`, que **não existem no exemplo**. Além disso o `Export`
do exemplo recebe `multipart`, enquanto o de produção recebe JSON
(`{content, fileName, format}`). Ou seja: o código público **não é** o código da imagem
que está no ar, e buildar a partir dele significaria perder duas rotas e trocar o
contrato de uma terceira, em silêncio.

Medido em 03/09/2026, sondando `https://docs.jurius-api.com`:

| Rota | Exemplo do GitHub | Produção |
|---|---|---|
| Import, Export, SystemClipboard, RestrictEditing, SpellCheck, SpellCheckByPage, LoadDocument, CompareDocuments | ✅ | ✅ |
| `LoadDefault` | ✅ | ❌ |
| `ExportSFDT` | ❌ | ✅ |
| `MailMerge` | ❌ | ✅ |

Por isso o desenho é **somar, não substituir**: a imagem oficial continua servindo tudo
o que já servia, e um serviço nosso — pequeno, com uma rota só — passa a servir o PDF.
O risco de regressão no editor cai a zero, porque nada do caminho do editor mudou.

### O que o `pdf-service` é

ASP.NET Core 8 + `Syncfusion.DocIORenderer.Net.Core`, ~200 linhas em
`pdf-service/src/Program.cs`. Duas rotas: `GET /health/live` e
`POST /api/documenteditor/ConvertToPdf`.

- **Licença:** usa a MESMA `SYNCFUSION_LICENSE_KEY` do word-processor-server (o compose
  passa para os dois). Precisa cobrir **Document Processing / DocIO** — se não cobrir, o
  PDF sai com marca d'água de avaliação, e o smoke test acusa.
- **Fontes:** o Dockerfile instala `fonts-liberation` (métricas idênticas a Arial, Times
  New Roman e Courier New) e tenta `fonts-crosextra-carlito`/`caladea` (Calibri e
  Cambria). Isso não é enfeite: o PDF é paginado com a fonte que o container encontrar, e
  fonte com métrica diferente move as quebras de linha e de página. Num documento que vai
  ser assinado, isso é defeito grave.
- **Versões travadas:** `Syncfusion.DocIORenderer.Net.Core` 34.2.6 e
  `SkiaSharp.NativeAssets.Linux` 3.119.1. As duas andam juntas — subir o Skia para a
  linha 4.x sem subir o DocIORenderer quebra em tempo de execução.
- **Gate opcional:** `PDF_API_KEY` (ver `.env.server.example`). Vazio = desligado.

### ⚠️ O serviço está aberto

O `DOCX_API_KEY` que o `.env.server.example` descrevia **nunca foi implementado**: o
Caddyfile só lista `X-Api-Key` entre os headers liberados no CORS, sem conferir valor
nenhum. E o filtro de Origin não protege chamada servidor-a-servidor, que vai sem
`Origin`. Na prática, quem souber a URL do túnel usa o conversor.

Isso já valia antes; a rota de PDF só torna o custo maior, por ser a operação mais cara.
Mitigações já no lugar: rate limit de 30/min só para ela (contra 120/min do resto) e
teto de 30 MB. Para fechar de verdade, preencha `PDF_API_KEY` e faça quem chama mandar
o header `X-Api-Key`.

## Pré-requisitos

- [ ] Um host com Docker + Docker Compose (`docker --version`, `docker compose version`).
      Pode ser uma VPS pequena OU até uma máquina sem IP público (é o caso do túnel).
- [ ] Um túnel configurado: **Cloudflare Tunnel** (`cloudflared`) ou **ngrok**.
- [ ] Uma licença Syncfusion que cubra o **Word Processor server-side** (Document Processing / DocIO).
      A mesma chave serve os dois containers; o `pdf-service` depende dela para não marcar d'água.
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
      nano .env.server        # preencha SYNCFUSION_LICENSE_KEY
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
      # --env-file lê SYNCFUSION_LICENSE_KEY; --build compila o Caddy com rate limit
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
      > Use um DOCX **de verdade**. Um `.docx` mínimo montado à mão não é aceito pelas
      > versões atuais do Word Processor: o Import devolve **204** com corpo vazio.
- [ ] Conversão para PDF (rota nova):
      ```bash
      curl -s -o saida.pdf -w "%{http_code} %{content_type}\n" \
        -X POST http://localhost:42811/api/documenteditor/ConvertToPdf \
        -F "files=@teste.docx;type=application/vnd.openxmlformats-officedocument.wordprocessingml.document"
      head -c 5 saida.pdf    # tem de sair %PDF-
      ```
      Esperado: **200**, `application/pdf`, e o arquivo abrindo num leitor de PDF.
      Depois **abra os dois lado a lado** e confira que a paginação bate com o Word —
      é o teste que nenhum código faz por você.

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

- **Logs:** `docker compose logs -f word-processor-server` | `docker compose logs -f pdf-service`
  (o `pdf-service` registra uma linha por conversão, com bytes e nº de páginas)
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

## Smoke test local

Depois de `docker compose up -d` (e antes de mexer no túnel), rode o script de checagem.
Ele valida health/live, health/ready, a página de status, a allowlist de rotas e métodos,
as respostas de CORS bloqueado e uma conversão real:

```bash
# Git Bash (Windows) ou shell do host Linux. Requer curl + base64.
./smoke-test.sh                                      # usa http://localhost:42811
BASE_URL=http://localhost:42811 ./smoke-test.sh      # outra porta
DOCX_FILE=/caminho/documento.docx ./smoke-test.sh    # DOCX real (RECOMENDADO)
PDF_API_KEY=segredo ./smoke-test.sh                  # se o gate do PDF estiver ligado
```

**Passe `DOCX_FILE`.** Sem ele o script usa um `.docx` mínimo embutido que as versões
atuais do Word Processor não carregam — e as duas checagens de conversão falham num
servidor perfeitamente saudável. O script avisa quando cai nesse caso.

Saída esperada: todas as linhas `[PASS]` e `Resultado: N ok / 0 falhas` (exit 0).
Qualquer `[FAIL]` aponta o que quebrou (ex.: conversão sem SFDT = licença inválida).

---

## Troubleshooting

| Sintoma | Causa provável | O que fazer |
|---|---|---|
| `health/live` responde, `health/ready` dá **502** | Word Processor caiu ou ainda subindo | `docker compose ps`; `docker compose logs word-processor-server`; aguarde a subida inicial do container |
| Conversão volta **200** mas sem `sfdt` no corpo | Licença Syncfusion ausente/inválida ou não cobre server-side | Confira `SYNCFUSION_LICENSE_KEY`; precisa cobrir **Document Processing / DocIO** |
| Editor do CRM: erro de **CORS** no console (mas curl dá 200) | Origin do CRM fora da allowlist | Adicione a Origin exata no bloco `map {header.Origin}` do Caddyfile e **rebuild** |
| Tudo volta **403 CORS origin not allowed** | Origin não bate (http vs https, com/sem `www`, porta) | A Origin é o domínio do **CRM**, não o do túnel; copie exatamente do DevTools |
| Requisições legítimas tomando **429** | Rate limit global porque a `key` não reflete o IP real | Ajuste a `key` do `rate_limit` (`Cf-Connecting-Ip` p/ Cloudflare, `X-Forwarded-For` p/ ngrok) |
| DOCX grande falha/timeout | Passou do teto de upload ou do timeout | Suba `request_body max_size` e/ou `read_timeout`/`write_timeout` no Caddyfile |
| Alterou o Caddyfile e "não mudou nada" | Caddyfile é **embutido na imagem** (COPY) | Rebuild obrigatório: `docker compose up -d --build` |
| **404** em rotas que antes passavam | Allowlist de rotas: só `/api/documenteditor/*`, `/health/*`, `/status` | Use o caminho `/api/documenteditor/...`; outros são bloqueados de propósito |
| `ConvertToPdf` responde **404** | O Caddy ainda é o antigo (o Caddyfile é embutido na imagem) | `docker compose up -d --build`; no Portainer, redeploy **com rebuild** |
| `ConvertToPdf` responde **502** | `pdf-service` não subiu ou não compilou | `docker compose logs pdf-service`; erro de restore = pacote Syncfusion inacessível |
| PDF sai com **marca d'água** de avaliação | Licença ausente ou que não cobre DocIO | Confira `SYNCFUSION_LICENSE_KEY`; precisa cobrir **Document Processing / DocIO** |
| PDF abre, mas o **layout não bate** com o Word | Fonte do documento ausente no container | Veja quais fontes o DOCX usa e acrescente o pacote no `pdf-service/Dockerfile`; sem a fonte certa, as quebras de linha mudam |
| `ConvertToPdf` responde **401** | `PDF_API_KEY` preenchida e header ausente/errado | Mande `X-Api-Key`, ou esvazie a variável e reinicie o `pdf-service` |
| `ConvertToPdf` responde **422** | O pedido chegou; ESTE documento é que não converteu | `docker compose logs pdf-service` traz a exceção; 422 nunca é "serviço fora do ar" |

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
