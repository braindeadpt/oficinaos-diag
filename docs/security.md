# Segurança — estado dos riscos

Revisões do ecossistema de 2026-10. Este ficheiro regista o que era risco
conhecido e como cada ponto foi fechado.

## 1. Atualizações: assinatura ECDSA P-256 — **resolvido**

**Antes:** o updater verificava o SHA-256 do zip contra o `SHA256SUMS.txt`
**da mesma release** — provava integridade, não autenticidade. Uma conta
GitHub comprometida publicava zip + checksum coerentes e o updater aplicava.

**Agora:**
- `release.yml` assina o `SHA256SUMS.txt` com uma chave EC P-256 privada
  (GitHub secret `RELEASE_SIGNING_KEY`, gerado com
  `tools/generate-release-key.ps1` — a privada nunca está no repo; cópia
  offline do dono em `release-signing-key.pem`, gitignored).
- `UpdateChecker` traz a chave pública embutida
  (`ReleaseSigningPublicKey`, SubjectPublicKeyInfo base64) e verifica a
  assinatura P1363 do SHA256SUMS **antes** de descarregar o zip. Sem
  `SHA256SUMS.sig` válido → a release nem sequer é considerada update
  (fail closed em `CheckAsync` e outra vez em `DownloadAndStageAsync`).
- O pin do publicador mantém-se: assets só de
  `https://github.com/braindeadpt/oficinaos-diag/releases/download/<tag>/`.
- Rotação: gerar par novo, embutir a pública nova, publicar essa versão,
  e só depois trocar o secret — exes antigos validam com a chave que trazem.
- Sem `RELEASE_SIGNING_KEY` o workflow de release falha — não saem
  releases não assinadas por engano.

Limite honesto que fica: quem comprometer a conta GitHub **e** o secret
consegue assinar — a chave de assinatura é a raiz de confiança. Authenticode
(Azure Trusted Signing) continua a ser o upgrade seguinte e resolve também
o aviso SmartScreen.

## 2. Token com scope para o diag — **resolvido** (cloud + diag)

**Antes:** o diag guardava (DPAPI) o *shop token* completo — o mesmo que
publica o portal e muda o whatsapp-config — só para relatórios IA.

**Agora:** `ShopToken.scope` ("full" default | "diag"). O técnico cola um
código de emparelhamento (XXXX-YYYY, o mesmo da app) nas Opções ou no
diálogo de relatório IA; o diag troca-o por um token `scope=diag` que só
serve `POST /reports/diagnostic` — `requireShop` é default-deny e cada rota
autoriza scopes explicitamente. Tokens antigos `full` continuam válidos.

## 3. TestServer (LAN) — **resolvido**

**Antes:** `TcpListener` em `IPAddress.Any` — a página de teste respondia
em todas as interfaces (incl. NIC públicas/VPNs), sem limites de pedidos.

**Agora:** bind só ao IP LAN que é anunciado no QR; read/write timeouts de
20 s; headers ≤ 64 KB e body ≤ 256 KB; 60 pedidos/min por IP; máx. 16
clientes em simultâneo; one-shot — após o primeiro resultado válido o
listener fecha (a sessão de teste morreu, não fica porta aberta).

## 4. `ListDevices` iOS — **resolvido**

**Antes:** o wrapper managed do imobiledevice-net não libertava o `char**`
de UDIDs — alguns bytes por poll, para sempre.

**Agora:** `iDeviceNativeMethods.idevice_get_device_list` devolve o `IntPtr`
cru; o marshal faz-se à mão e `idevice_device_list_free` liberta o array
nativo no `finally`.
