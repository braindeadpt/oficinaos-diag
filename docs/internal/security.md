# Segurança — estado dos riscos

Revisões do ecossistema de 2026-10. Este ficheiro regista o que era risco
conhecido e como cada ponto foi fechado.

## 1. Atualizações: assinatura ECDSA P-256 **offline** — **resolvido**

**Antes:** o updater verificava o SHA-256 do zip contra o `SHA256SUMS.txt`
**da mesma release** — provava integridade, não autenticidade. Uma conta
GitHub comprometida publicava zip + checksum coerentes e o updater aplicava.

**Agora** — a chave privada nunca passa pelo CI (fluxo completo e
procedimento de rotação em [`RELEASE.md`](RELEASE.md)):
- `release.yml` faz só build+testes e publica um **draft** com o zip e um
  `SHA256SUMS.txt` que inclui uma linha `version <x.y.z>` — coberta pela
  assinatura, ata o conteúdo à versão exata.
- `tools/sign-release.ps1` corre na máquina do dono: descarrega o SUMS do
  draft, confirma `version == tag`, resolve o commit do tag e **recusa se
  não for ancestor de `main`**, pede confirmação, assina com a PEM local
  e publica. Um GitHub comprometido consegue criar um draft — não um
  `.sig` válido.
- `UpdateChecker` traz a chave pública embutida e exige, antes de
  descarregar: assinatura P1363 válida, `version == tag` e `version >
  local` (anti-downgrade/replay). Sem `SHA256SUMS.sig` válido a release
  nem sequer é candidata a update (fail closed).
- O pin do publicador mantém-se: assets só de
  `https://github.com/braindeadpt/oficinaos-diag/releases/download/<tag>/`.

Limite honesto que fica: a PEM é a raiz de confiança — quem a roubar da
máquina do dono assina releases (daí viver num cofre/pen cifrada, nunca em
disco partilhado nem no CI). Authenticode (Azure Trusted Signing)
continua a ser o upgrade seguinte e resolve também o aviso SmartScreen.

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
