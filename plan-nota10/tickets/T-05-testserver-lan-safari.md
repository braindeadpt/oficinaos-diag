# T-05 — Seleção Inteligente de IP LAN no `TestServer` (Anti-VirtualBox/WSL) e Quirks iOS Safari em `device-test.html`

- **Fase:** 2 (Hardening LAN & Testes On-Device)
- **Bugs Resolvidos:** `B-10`, `B-13`
- **Dependências:** Nenhuma
- **Ficheiros em Escopo:**
  - `src/OficinaDiag/Tests/TestServer.cs`
  - `src/OficinaDiag/Tests/wwwroot/device-test.html`
- **Fora de Escopo:** `adb reverse` e testes de microfone/auricular v3 ficam reservados para `T-10`.

---

## Contexto do Problema

1. **QR Code aponta para IP virtual inacessível (`B-10a`):** Em `TestServer.cs:63-74`, `ResolveLanAddress()` enumera todas as interfaces `Ethernet`/`Wireless80211` `Up` e ordena por `192.168.*`. Num PC com VirtualBox (`192.168.56.1`), VMware (`192.168.88.1`), Hyper-V ou WSL2, essa interface virtual não tem Gateway para a rede da loja mas ganha a eleição! Como o `TcpListener` faz bind exclusivo a `_bindAddress`, o telemóvel ligado ao Wi-Fi nunca consegue abrir o teste.
2. **Parsing HTTP manual frágil aos 64 KB (`B-10b`):**
   - Em `TestServer.cs:170-202`, se um cliente enviar 64 KB sem `\r\n\r\n`, o loop termina com `headerEnd == -1` e `var bodyStart = headerEnd + 4` (`3`) processa headers como JSON body!
   - `Content-Length` é procurado com regex case-sensitive sobre `request` inteiro em vez de `request[..headerEnd]` com `RegexOptions.IgnoreCase`.
   - `path == $"/{_token}/"` rejeita `GET /{_token}` sem barra final ou com query params adicionados por leitores QR mobile (`?from=qr`).
3. **Falsos negativos em iOS Safari em `device-test.html` (`B-13`):**
   - `AudioContext` no iOS Safari pode nascer `"suspended"`; além disso, o botão físico de Silêncio do iPhone corta o WebAudio (levando técnicos a marcar "NÃO ouvi" → Grau C injusto).
   - O timeout de 6000ms em `startSensors()` arranca antes de o técnico clicar em `PERMITIR SENSORES (iPhone)` e aceitar o diálogo do iOS.
   - `finish()` grava `result.colorsViewed = colors.length` (13) mesmo que o utilizador tenha saltado o teste de cores.

---

## Especificação de Implementação

### 1. Eleição de Interface LAN Real (`TestServer.ResolveLanAddress`)
- Tornar a lógica de ranking testável (`internal static IPAddress? PickBestLanIp(IEnumerable<LanCandidate> candidates)`).
- Excluir interfaces cujo `Name` ou `Description` (case-insensitive) contenha `"virtual"`, `"vethernet"`, `"wsl"`, `"vmware"`, `"vbox"`, `"hyper-v"`, `"docker"`, `"loopback"`, `"tap"`, `"tun"`, `"tailscale"`, `"zerotier"`, `"wireguard"`.
- Priorizar estritamente interfaces que tenham pelo menos um `GatewayAddress` IPv4 válido (`!g.Address.Equals(IPAddress.Any)`), preferindo `Wireless80211` e `Ethernet` com rota default, e só usar interfaces sem gateway como fallback final se nenhuma tiver gateway.

### 2. Robustez HTTP em `TestServer.HandleClientAsync`
- Logo após o loop de leitura de headers (`linha 178`, antes do `if (buf.Count == 0)`): se `headerEnd < 0`, responder `431 Request Header Fields Too Large` (ou fechar socket) e fazer `return;` imediato!
- Separar `var headerText = Encoding.ASCII.GetString(raw, 0, headerEnd);` — extrair a request line e fazer `Regex.Match(headerText, @"^Content-Length:\s*(\d+)\r?$", RegexOptions.IgnoreCase | RegexOptions.Multiline)` apenas sobre `headerText`!
- Extrair `cleanPath = path.Split('?')[0].TrimEnd('/');` e aceitar `cleanPath == $"/{_token}"` no `GET` e `cleanPath == $"/{_token}/result"` no `POST`.

### 3. Correções iOS Safari em `device-test.html`
- Rastrear um `Set` real `seenColors = new Set()` em `showColor()` e gravar `result.colorsViewed = seenColors.size` em `finish()`.
- No teste de altifalante (`spkBtn.onclick`): chamar `if (audioCtx.state === 'suspended') await audioCtx.resume();` antes de `o.start()`, e adicionar lembrete visível em PT: *"No iPhone, desativa o botão lateral de Silêncio e sobe o volume antes de tocar."*
- Em `startSensors()`: se `DeviceMotionEvent.requestPermission` existir (iOS), **não** iniciar o `setTimeout(..., 6000)` logo à entrada; iniciá-lo apenas depois de `permBtn.onclick` resolver a permissão (ou marcando `"no-permission"` se recusada).

---

## Critérios de Aceitação
- [x] Num PC com VirtualBox (`192.168.56.1` sem gateway) e Wi-Fi da loja (`10.0.0.45` com gateway `10.0.0.1`), `ResolveLanAddress()` escolhe `10.0.0.45`. *(prova: `TestServerTests.PrefereWiFiComGatewayAVirtualBoxSemGateway`, `IgnoraWslEDocker`, `ApipaNuncaEEscolhida`)*
- [x] `GET /{token}` e `GET /{token}/?v=1` servem a página de teste sem 404. *(prova: `TestServerTests.GetAceitaTokenSemBarraFinalEComQuery`)*
- [x] Headers > 64 KB sem `\r\n\r\n` são rejeitados de imediato sem underflow de índice. *(prova: `TestServerTests.HeadersAcimaDe64KSemTerminador_Dao431`)*
- [x] Em iPhone, o timeout dos sensores só começa a contar após aceitar a permissão. *(prova: `device-test.test.mjs` — `página: no iOS os 6 s só contam a partir do gesto de permissão` injeta `DeviceMotionEvent.requestPermission` como o Safari faz, percorre as fases e conta os `setTimeout` agendados: nenhum ≥ 6 s antes do clique em PERMITIR SENSORES e exatamente um depois; o teste gémeo sem permissão garante que o caminho Android/desktop continua a armar logo. A mutação que repõe a chamada incondicional falha os dois)*
- [x] Com o movimento recusado no iOS, o payload não leva `accel`/`gyro`/`orient` e o relatório não ganha linhas «sem leitura». *(prova: PR #11 — `D.armedSensorKeys` arma só o `light` quando recusado; `device-test.test.mjs` cobre os dois caminhos que o QA em iPhone encontrou («iPhone sem permissão de movimento entrega payload sem accel/gyro/orient» e «…que recusa o diálogo de permissão…») e o `TestResultMapper` trata a chave ausente como «não medido», sem linha)*
