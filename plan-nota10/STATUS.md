# Estado de Execução — `oficinaos-diag` Plano Nota 10

> **Instrução para qualquer agente LLM:** Atualiza este ficheiro no fim de cada ticket concluído (`[ ]` → `[x]`), preenchendo a data/commit ou nota curta em "Registo de Handoff".

**Próximo ticket a executar:** `T-03` (`tickets/T-03-android-locale-polarity.md`). `T-01` e `T-02` concluídos; `B-01`, `B-02`, `B-03`, `B-04`, `B-09` e `B-16` fechados.

---

## Progresso por Ticket

### Fase 1 — Bloqueadores Críticos: Store/Updater, iOS & Android Hardware (`7.4 → 8.7`)
- [x] **T-01** — Isolar MSIX Store vs Portable, Auto-Update atómico consciente de `adb.exe` e hardening de scripts (`tickets/T-01-updater-msix-guard.md`)
- [x] **T-02** — Correção estrutural do `IosCollector` (`ParsePlistDict`, `battery.capacity`, mutex USB por UDID, `SerialNumber` e `ProductType` sem PII) + paridade no `Grader` (`tickets/T-02-ios-collector-grading-parity.md`)
- [ ] **T-03** — Cultura `InvariantCulture` (`pt-PT`), polaridade de corrente OEM Android (`battery/status`), deduplicação de contas e mAh, e falsos positivos de MDM/bateria ausente (`B-19`, `B-20`) (`tickets/T-03-android-locale-polarity.md`)

### Fase 2 — Hardening de Relatórios, LAN/Safari, WPF UI, Temas & L10n (`8.7 → 9.3`)
- [ ] **T-04** — Sanitização XSS/HTML em `ReportBuilder`, timeouts curtos em `CloudClient` e robustez em `ScanHistory`/`AppLog` (`tickets/T-04-reports-cloud-hardening.md`)
- [ ] **T-05** — Seleção inteligente de IP LAN no `TestServer` (anti-VirtualBox/WSL + HTTP bounds) e compatibilidade iOS Safari em `device-test.html` (`tickets/T-05-testserver-lan-safari.md`)
- [ ] **T-06** — Estabilidade `SettingsDialog`, `Detached` multi-dispositivo, temas dinâmicos WPF, cache `ScopeView` e Inglês 100% em `L10n` (`tickets/T-06-wpf-themes-l10n.md`)

### Fase 3 — Arquitetura `OficinaDiag.Core` & Testes de Regressão + CI (`9.3 → 9.7`)
- [ ] **T-07** — Extrair biblioteca `src/OficinaDiag.Core` (`net8.0`), criar `OficinaDiag.sln` e remover ficheiros `<Compile Link=...>` (`tickets/T-07-extract-oficinadiag-core.md`)
- [ ] **T-08** — Suite completa de testes de regressão (iOS PD 9V, Android OEM, `pt-PT`, XSS, paridade `L10n` e XAML) + build WPF no CI (`tickets/T-08-regression-tests-ci.md`)

### Fase 4 — Diferenciadores "Nota 10" de Bancada Profissional (`9.7 → 10.0`)
- [ ] **T-09** — Seletor multi-dispositivo na bancada, saúde avançada OEM (`mSavedBatteryAsoc`, `bms`, troca de bateria iOS, `fastboot`/`recovery`) e `models.json` Apple/Pixel (`tickets/T-09-multi-device-oem-health.md`)
- [ ] **T-10** — On-Device Test v3 (`adb reverse` zero-Wi-Fi no Android + teste Microfone/Auricular), presets de impressora térmica e retenção RGPD no histórico (`tickets/T-10-test-v3-adb-reverse-labels-gdpr.md`)

---

## Rastreabilidade dos Bugs (18 da auditoria + `B-19`/`B-20` do rig Android)

- [x] `B-01` (Crítico) — Auto-Updater em MSIX Store + corrupção com `adb.exe` ativo → **T-01**
- [x] `B-02` (Crítico) — `ParsePlistDict` achatava a telemetria de carga por cima da célula (no iPhone real: `Watts`/`Current` → 0, `AverageBattSkinTemp` → 0 °C) → **T-02**
- [x] `B-03` (Crítico) — `battery.health` vs `battery.capacity` quebrava Grau D, peças e `BatteryTrend` em iOS → **T-02**
- [x] `B-04` (Crítico) — Corrida USB `lockdownd`/`diagnostics_relay` e processos órfãos em timeout → **T-02**
- [ ] `B-05` (Alto) — `double.TryParse` sem `InvariantCulture` gera 314 °C / 385 °C em Windows `pt-PT` → **T-03**
- [ ] `B-06` (Alto) — Sinal `current_now` invertido em Samsung/MediaTek gera falso defeito de porta de carga → **T-03**
- [ ] `B-07` (Alto) — Crash `DialogResult` em `SettingsDialog.Save_Click` + mutação suja de `_config` → **T-06**
- [ ] `B-08` (Alto) — Links `javascript:` em Markdig IA + `docRef` sem `HtmlEncode` em `ToInsuranceHtml` → **T-04**
- [x] `B-09` (Médio) — `DeviceName` pessoal exposto como `identity.model` (PII) + `UDID` em `Serial` → **T-02**
- [ ] `B-10` (Médio) — `TestServer` escolhe IP virtual (VirtualBox/WSL) + bug `headerEnd == -1` aos 64 KB → **T-05**
- [ ] `B-11` (Médio) — `dumpsys account` multiplica contas + capacidade em `µAh` crua → **T-03**
- [ ] `B-12` (Médio) — `Detached` limpa dispositivo errado + `lang="pt"` fixo no IA + `:` em nomes de ficheiro → **T-04**, **T-06**
- [ ] `B-13` (Médio) — Quirks iOS Safari (`AudioContext` suspenso/Silêncio, timer 6s sensores, `colorsViewed`) → **T-05**
- [ ] `B-14` (Médio) — Troca de tema não refresca `ResultsList`, diálogos `StaticResource` e 18 `Pen`s/frame → **T-06**
- [ ] `B-15` (Médio) — Mensagens hardcoded em PT em `CloudClient`, `CompareWindow` e `TokenDialog` → **T-06**
- [x] `B-16` (Médio) — Redirecionamento `>` em `sign-release.ps1` + `build-store.ps1` sem validar `adb.exe` → **T-01**
- [ ] `B-17` (Baixo) — Timeout global de 150s c/ retry (até 5 min) e falta de `using` em `CloudClient` → **T-04**
- [ ] `B-18` (Baixo) — `Mutex.WaitOne()` infinito em `ScanHistory` e corte UTF-8 a meio em `AppLog` → **T-04**
- [ ] `B-19` (Alto) — `security.mdm` dispara com `Device Owner Type: -1` e força Grau D em Android sem gestão → **T-03**
- [ ] `B-20` (Médio) — Ausência de leitura vira valor: `PropAsync` devolve `""` (bootloader `warn`, baseband/SIM em branco) e bateria sem `present` (`health: 1`, `voltage: 0`, `temp: 0`) → **T-03**

---

## Rig de Teste Android (InstaBox) — útil para T-05 e T-10

Máquina `diag-android` no [InstaBox](https://instabox.sh) (plano free, 4 vCPU / 8 GB): Android 14 (`redroid14_x86_64`, SDK 34), ecrã 720×1280, `adb` id `emulator-5554`. Pausa sozinha após 30 min idle (estado do Android sobrevive ao resume).

```bash
npx instabox@latest ssh diag-android     # shell na máquina
adb reverse tcp:8801 tcp:8801            # telefone → localhost da máquina
adb shell am start -n org.chromium.webview_shell/.WebViewBrowserActivity \
  -a android.intent.action.VIEW -d "http://127.0.0.1:8801/<token>/"
```

O `TestServer` real é WPF (só Windows), por isso no rig corre-se um shim Node em `/tmp/ts/server.js` que replica o contrato: `GET /<token>/` serve `/tmp/ts/device-test.html`, `POST /<token>/result` valida `touchHit`/`touchTotal`/`colorsViewed` e grava `/tmp/ts/result.json` + `/tmp/ts/server.log`. Arranque: `setsid nohup node /tmp/ts/server.js <token> 8801 &`.

**Validado em 2026-11-12 (smoke end-to-end):** página servida, aberta via `adb reverse` em `127.0.0.1:8801`, as 4 fases percorridas e `POST /smoketoken42/result` → `200 shape=true` com `colorsViewed=13`, `touchHit=54/60`, `edgeHit=22/28`, `batteryApi=100%`. Ecrã final: «resultado entregue». Confirma o caminho que o T-10 quer usar (`adb reverse` sem Wi-Fi).

Correr o `AndroidCollector` contra este rig revelou dois falsos positivos reais — `B-19` (MDM) e `B-20` (bateria ausente/props vazias), ambos encaminhados para o `T-03`, com o harness em `/tmp/harness`.

**Limites — não tirar daqui conclusões de hardware:** sem sensores (`accel`/`gyro`/`orient`/`light` = `unavailable`), sem bateria nem `sysfs` (`/sys/class/power_supply` vazio; `dumpsys battery` a zeros), sem contas Google, `maxTouchPoints=1`. O `uiautomator` não expõe o DOM do WebView — os toques são por coordenadas (conteúdo começa em y≈192; grelha 6×10 com ~100 px por linha). O WebView pode aparecer branco no primeiro screenshot; só pinta alguns segundos depois do `GET`.

---

## Registo de Handoff entre Sessões / Agentes

- **2026-11-12 (T-02 concluído — Buffy):** `ParsePlistDict` reescrito (dois níveis, prefixo `AdapterDetails.*`/`BatteryData.*`, guarda `<plist` contra o *usage* com exit 0); a saúde iOS passou a `battery.capacity` com limiares 60/80; `ProbeAsync`/`CollectBatteryHealth` leem `AdapterDetails.Watts` e `BatteryData.*`; identidade usa `ModelNameTable` + `SerialNumber` e deixa de ler `DeviceName`; USB serializado por UDID (`ConcurrentDictionary` + `SemaphoreSlim`), `Kill(entireProcessTree)` no timeout/cancelamento e `Dispose` do CTS só quando a task termina. `Grader`: `battery.health` entra nos `CriticalChecks` e o ramo legado mantém severidade (`fail` → D + peça, `warn` → C). **Verificação:** build WPF (`-p:EnableWindowsTargeting=true`) 0 erros/0 avisos; `dotnet test` 117/117 (3 novos); parser validado na fixture real (63 chaves, `Voltage` 3914, `IsCharging` `true`, `Watts` 13, `0 °C` eliminado, `capacity` 100%). **Não exercitado:** app WPF com iPhone ligado — o coletor usa `idevicediagnostics.exe` + libimobiledevice nativa e não corre em macOS. Próximo: `T-03`.
- **2026-11-12 (T-02 validado em hardware — iPhone 12 Pro / iOS 27.2.1):** primeiro ensaio do parser iOS contra um iPhone real (`iPhone13,3`): `ParsePlistDict` devolve 113 chaves mas achata `AppleRawAdapterDetails` e `DeadBatteryBootData` por cima da célula — `Watts`/`Current` → 0 e `battery.temp` = `0 °C`. A colisão de `Voltage` descrita em `B-02` **não** se confirmou (o adaptador real expõe `AdapterVoltage`). As capacidades (2790/2798 mAh) só existem em `BatteryData`, por isso o `T-02` foi corrigido: o parser tem de ler `BatteryData` explicitamente e o `ProbeAsync` passa a ler `AdapterDetails.Watts`. Fixture real em `tests/OficinaDiag.Tests/Fixtures/apple-smart-battery-iphone13_3.plist` (serial da bateria substituído). `B-04` não reproduziu — dois `ioregentry` em paralelo devolveram ambos o payload completo. Novo achado: `idevicediagnostics` com argumentos inválidos dá **exit 0** e imprime o *usage* no stdout, que o `RunTool` aceita como plist (guarda `<plist` acrescentada ao ticket).
- **2026-11-12 (Sessão Inicial — Mentor Gemini 4 Carbon + Argon + Flash):** Auditoria completa concluída (65 ficheiros, 18 bugs validados, 3 falsos positivos descartados). Kit de execução `plan-nota10/` criado sem alterar código de produção, pronto para revisão e arranque em `T-01`.
- **2026-11-12 (`B-19`/`B-20` do rig Android):** o primeiro scan real do `AndroidCollector` contra a `diag-android` (Android 14 sem bateria) não crashou — 23 checks, `scan=pass` — mas produziu **Grau D num aparelho sem MDM**: `security.mdm` casa a substring «Device Owner» em `Device Owner Type: -1`; `battery.health: 1` sai `warn`; `battery.voltage: 0` sai `warn "fora da faixa"`; `battery.temperature` sai `0°C`; e `security.bootloader` sai `warn` em vez de `skipped` porque `PropAsync` devolve `""`. Registados como `B-19`/`B-20` e acrescentados ao `T-03` (§4 e critérios de aceitação). Sem alterações de código nesta sessão.
- **2026-11-12 (rig InstaBox — handoff):** máquina `diag-android`, template **`android`** (Android 14 `redroid14_x86_64`, SDK 34, 720×1280, `adb` id `emulator-5554`), plano free do InstaBox; pausa sozinha aos 30 min idle. **Valida:** `adb` a falar com o dispositivo, `adb reverse` (o caminho zero-Wi-Fi do `T-10`), `wm size`/ecrã 720×1280, `getprop`/`dumpsys`/`am start` e a `device-test.html` de ponta a ponta (smoke com `POST …/result` → `200 shape=true`). **Não reproduz:** bateria OEM (`/sys/class/power_supply` vazio, `dumpsys battery` a zeros, sem `mSavedBatteryAsoc`/`charge_full`), contas Google (0 contas, sem Play Services) e nada de **Windows/MSIX** — updater, empacotamento MSIX e o `TestServer` WPF continuam a exigir Windows ou o CI `windows-latest`. Comandos e limites detalhados na secção «Rig de Teste Android». Não altera código de produção.
- **2026-11-12 (T-01 concluído — Buffy):** `UpdateChecker.IsPackagedApp()` (P/Invoke `GetCurrentPackageFullName`) + sonda de escrita em `appDir` bloqueiam o updater em MSIX/Store; `BuildUpdateScript` passou a esperar pelo PID real (`Wait-Process`), libertar `adb`/`idevice*`, extrair para `$stage` e validar ZIP antes de tocar em `appDir` (com defesa ZipSlip e rollback). `sign-release.ps1` descarrega o SUMS byte a byte via `BaseStream`; `build-store.ps1` exige `adb.exe`; `fetch-tools.ps1` recusa 0 binários verificados. **Verificação:** `dotnet test` 114/114 e build WPF (`-p:EnableWindowsTargeting=true`) 0 erros, ambos com SDK 8.0.428; os 4 scripts PowerShell (incluindo o updater gerado) passam o parser do PowerShell 7.4.6. **Não exercitado:** instalação MSIX real e execução do updater num Windows (sem essa máquina aqui). Próximo: `T-02`.
