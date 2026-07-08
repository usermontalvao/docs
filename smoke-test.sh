#!/usr/bin/env bash
#
# smoke-test.sh — checagens locais do serviço de conversão DOCX (Caddy + Word Processor).
#
# Valida, contra o Caddy local (antes do túnel), tudo que o Caddyfile promete:
#   health/live, health/ready, página de status, allowlist de rotas e métodos,
#   respostas de CORS bloqueado e uma conversão real.
#
# Uso:
#   ./smoke-test.sh                         # usa http://localhost:42811
#   BASE_URL=http://localhost:42811 ./smoke-test.sh
#   ORIGIN=https://crm-advogado.onrender.com ./smoke-test.sh   # origin permitida p/ o teste de CORS
#
# Requisitos: bash, curl, base64. No Windows use o Git Bash.
# Saída: 0 se tudo passar; 1 se qualquer checagem falhar.

set -u

BASE_URL="${BASE_URL:-http://localhost:42811}"
ALLOWED_ORIGIN="${ORIGIN:-https://crm-advogado.onrender.com}"
pass=0
fail=0
green() { printf '\033[32m%s\033[0m' "$1"; }
red()   { printf '\033[31m%s\033[0m' "$1"; }

ok()   { pass=$((pass+1)); printf '  [%s] %s\n' "$(green PASS)" "$1"; }
bad()  { fail=$((fail+1)); printf '  [%s] %s\n' "$(red FAIL)" "$1"; }

# check <descrição> <esperado> <obtido>
check() {
  if [ "$2" = "$3" ]; then ok "$1 (HTTP $3)"; else bad "$1 (esperado $2, obtido $3)"; fi
}

# status_of <curl args...> -> imprime só o código HTTP
status_of() { curl -s -o /dev/null -w '%{http_code}' "$@"; }

echo "== Smoke test do serviço DOCX =="
echo "   BASE_URL = $BASE_URL"
echo "   ORIGIN   = $ALLOWED_ORIGIN"
echo

# 1) Liveness -----------------------------------------------------------------
live_body="$(curl -s "$BASE_URL/health/live")"
live_code="$(status_of "$BASE_URL/health/live")"
check "health/live responde 200" 200 "$live_code"
case "$live_body" in
  *'"status":"ok"'*) ok "health/live traz JSON de status" ;;
  *) bad "health/live sem JSON esperado (corpo: ${live_body:-vazio})" ;;
esac

# 2) Readiness (proxy alcança o Word Processor) -------------------------------
ready_code="$(status_of "$BASE_URL/health/ready")"
check "health/ready responde 200 (upstream acessível)" 200 "$ready_code"

# 3) Página de status ---------------------------------------------------------
status_code="$(status_of "$BASE_URL/status")"
check "GET /status serve o dashboard" 200 "$status_code"

# 4) Rota fora da allowlist -> 404 -------------------------------------------
notfound_code="$(status_of "$BASE_URL/admin")"
check "rota não permitida (/admin) bloqueada" 404 "$notfound_code"
dotenv_code="$(status_of "$BASE_URL/.env")"
check "rota sensível (/.env) bloqueada" 404 "$dotenv_code"

# 5) Método não permitido na API -> 405 --------------------------------------
delete_code="$(status_of -X DELETE "$BASE_URL/api/documenteditor/")"
check "método DELETE na API rejeitado" 405 "$delete_code"

# 6) CORS: origin não permitida -> 403 ---------------------------------------
badorigin_code="$(status_of -H 'Origin: https://evil.example' "$BASE_URL/api/documenteditor/Import")"
check "origin de CORS não permitida bloqueada" 403 "$badorigin_code"

# 7) CORS: preflight de origin permitida -> 204 ------------------------------
preflight_code="$(status_of -X OPTIONS \
  -H "Origin: $ALLOWED_ORIGIN" \
  -H 'Access-Control-Request-Method: POST' \
  "$BASE_URL/api/documenteditor/Import")"
check "preflight de origin permitida ($ALLOWED_ORIGIN)" 204 "$preflight_code"

# 8) Conversão real (POST Import de um DOCX mínimo) --------------------------
TEST_DOCX_B64="UEsDBBQAAAAIAOue51zJTxqw6wAAAK4BAAATAAAAW0NvbnRlbnRfVHlwZXNdLnhtbH1QvU7DMBDeeQrLK4odGBBCSTrwMwJDeYCTfUks7LPlc0v79jht6YAK4933q69b7YIXW8zsIvXyRrVSIJloHU29/Fi/NPdScAGy4CNhL/fIcjVcdet9QhZVTNzLuZT0oDWbGQOwigmpImPMAUo986QTmE+YUN+27Z02kQpSacriIYfuCUfY+CKed/V9LJLRsxSPR+KS1UtIyTsDpeJ6S/ZXSnNKUFV54PDsEl9XgtQXExbk74CT7q0uk51F8Q65vEKoLP0Vs9U2mk2oSvW/zYWecRydwbN+cUs5GmSukwevzkgARz/99WHu4RtQSwMEFAAAAAgA657nXLmBRHGwAAAAKgEAAAsAAABfcmVscy8ucmVsc43POw7CMAwG4J1TRN5pWgaEUJMuCKkrKgeIEjeNaB5KwqO3JwMDIAZG278/y233sDO5YUzGOwZNVQNBJ70yTjM4D8f1DkjKwikxe4cMFkzQ8VV7wlnkspMmExIpiEsMppzDntIkJ7QiVT6gK5PRRytyKaOmQciL0Eg3db2l8d0A/mGSXjGIvWqADEvAf2w/jkbiwcurRZd/nPhKFFlEjZnB3UdF1atdFRYob+nHi/wJUEsDBBQAAAAIAOue51w8eLmYnQAAAM8AAAARAAAAd29yZC9kb2N1bWVudC54bWxFjjEOwjAMRXdOEWWnKQwIVU27cYJygJCYtlJjR3Gg9PYkZWB5/l+2vn/bf/wi3hB5JtTyVNVSAFpyM45a3ofb8SoFJ4POLISg5QYs++7Qro0j+/KASeQE5GbVckopNEqxncAbrigA5t2Tojcp2ziqlaILkSww5wd+Uee6vihvZpRdjnyQ28oMBbEgdQNwglYVWRh3hp2/c/Wv0n0BUEsBAhQAFAAAAAgA657nXMlPGrDrAAAArgEAABMAAAAAAAAAAAAAAIABAAAAAFtDb250ZW50X1R5cGVzXS54bWxQSwECFAAUAAAACADrnudcuYFEcbAAAAAqAQAACwAAAAAAAAAAAAAAgAEcAQAAX3JlbHMvLnJlbHNQSwECFAAUAAAACADrnudcPHi5mJ0AAADPAAAAEQAAAAAAAAAAAAAAgAH1AQAAd29yZC9kb2N1bWVudC54bWxQSwUGAAAAAAMAAwC5AAAAwQIAAAAA"

tmpdocx="$(mktemp -t smoke-XXXXXX.docx 2>/dev/null || echo "${TMPDIR:-/tmp}/smoke-test.docx")"
printf '%s' "$TEST_DOCX_B64" | base64 -d > "$tmpdocx" 2>/dev/null

conv_headers=(-H "Origin: $ALLOWED_ORIGIN")

conv_out="$(curl -s -w '\n%{http_code}' "${conv_headers[@]}" \
  -X POST "$BASE_URL/api/documenteditor/Import" \
  -F "files=@$tmpdocx;type=application/vnd.openxmlformats-officedocument.wordprocessingml.document")"
conv_code="$(printf '%s' "$conv_out" | tail -n1)"
conv_body="$(printf '%s' "$conv_out" | sed '$d')"
rm -f "$tmpdocx"

check "conversão DOCX -> SFDT responde 200" 200 "$conv_code"
case "$conv_body" in
  *sfdt*|*sections*) ok "resposta contém SFDT (licença ativa)" ;;
  *) bad "resposta sem SFDT — verifique a licença Syncfusion (corpo: ${conv_body:0:160})" ;;
esac

# Resumo ----------------------------------------------------------------------
echo
echo "== Resultado: $(green "$pass ok") / $([ "$fail" -gt 0 ] && red "$fail falhas" || echo '0 falhas') =="
[ "$fail" -eq 0 ]
