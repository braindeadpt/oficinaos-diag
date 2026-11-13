# T-09 — Bancada Multi-Dispositivo, Saúde Avançada OEM / Peças Trocadas e Catálogo Completo Apple + Pixel

- **Fase:** 4 (Diferenciadores "Nota 10" para Bancada Profissional)
- **Dependências:** `T-07`, `T-08`
- **Ficheiros em Escopo:**
  - `src/OficinaDiag.Core/Devices/DeviceDetector.cs`
  - `src/OficinaDiag.Core/Devices/AndroidCollector.cs`
  - `src/OficinaDiag.Core/Devices/IosCollector.cs`
  - `src/OficinaDiag.Core/Data/models.json`
  - `src/OficinaDiag.Core/History/ScanHistory.cs`
  - `src/OficinaDiag.Core/L10n.cs`
  - `src/OficinaDiag/MainWindow.xaml` e `src/OficinaDiag/MainWindow.xaml.cs`
  - `tests/OficinaDiag.Tests/` (testes dos novos parsers e catálogo)

---

## Objetivo ("Nota 10" na Bancada Real)

Numa loja de reparações ou recondicionamento nunca há apenas 1 telemóvel em cima da bancada, nem todos os Samsung/Xiaomi expõem `charge_full` no caminho genérico do kernel, nem uma loja pode ver códigos crus como `iPhone16,2` ou `Pixel 8a` sem nome comercial.

---

## Especificação de Implementação

### 1. Seletor Multi-Dispositivo na Bancada + Deteção `recovery` / `sideload` / `fastboot`
- **`DeviceDetector.cs`:**
  - Reconhecer na regex de `adb devices` os estados `device`, `unauthorized`, `recovery` e `sideload` (guardando o estado num novo campo opcional `ConnectionState` em `DetectedDevice`, ex.: `"ready"`, `"unauthorized"`, `"recovery"`, `"sideload"`, `"fastboot"`).
  - Se `fastboot.exe` existir em `tools/platform-tools/fastboot.exe`, correr também `fastboot devices` (timeout curto 2s) para detetar Androids presos no bootloader!
  - Expor `IReadOnlyList<DetectedDevice> ConnectedDevices` em `DeviceDetector`.
- **`MainWindow.xaml` + `MainWindow.xaml.cs`:**
  - Adicionar um `ComboBox` compacto e estilizado no header (visível quando `ConnectedDevices.Count > 1`, discreto quando `<= 1`) permitindo ao técnico escolher qual dos telemóveis ligados está ativo (`_current`).
  - Se o telemóvel ativo for desligado e houver outro ligado, selecionar automaticamente o seguinte disponível e avisar no log; se estiver em `recovery`/`fastboot`/`unauthorized`, mostrar instrução clara no `StatusText` e desativar apenas ações que exijam OS completo.

### 2. Saúde de Bateria OEM Avançada & Deteção de Troca de Peças
- **Android (`AndroidCollector.cs`):**
  - **Samsung OneUI (`dumpsys battery`):** extrair `mSavedBatteryAsoc: (\d+)` (Absolute State of Charge — saúde real em % calculada pelo fuel gauge da Samsung!) e `mSavedBatteryUsage: (\d+)` (centenas de ciclos nos Samsung, ex. `342` = 3.42 ciclos ou `34200` = 342 ciclos consoante escala OneUI: `>= 100` divide por 100) como fallback quando `/sys/class/power_supply/battery/charge_full` ou `cycle_count` não existirem!
  - **Xiaomi / Qualcomm BMS:** se `/sys/class/power_supply/battery/charge_full` não existir, tentar `/sys/class/power_supply/bms/charge_full` + `charge_full_design` (e `max170xx_battery`).
- **iOS (`IosCollector.cs` + `ScanHistory.cs`):**
  - Comparar `battery.serial` do scan atual com scans anteriores do mesmo `Device.Serial` no `ScanHistory`: se o `battery.serial` mudou entre dois scans, adicionar nota automática em `battery.serial` (`"bateria substituída desde o scan de dd-MM-yyyy"`).
  - Nos crash logs recolhidos (`CollectCrashLogs`), inspecionar padrões críticos de hardware conhecidamente fatais em bancada Apple (`panic-full` com `userspace watchdog timeout: no successful checkins from thermalmonitord` → alerta explícito de flex de carga / sensor térmico em `logs.crashes`!).

### 3. Expansão Completa de `Data/models.json`
- Expandir `models.json` (mantendo JSON leve < 40 KB) com:
  - **Toda a família iPhone e principais iPads** por `ProductType` (`iPhone10,1`..`iPhone10,6` [iPhone 8/X] até `iPhone17,1`..`iPhone17,5` [iPhone 16 / 16 Pro / 16e] e `iPhone18,*`).
  - **Toda a linha Google Pixel** (`Pixel 4` até `Pixel 9 Pro XL` / `Pixel 9a`).
  - **Variantes Samsung internacionais/dual-SIM:** no `ModelNameTable.Lookup(string? code)`, se o lookup direto falhar num código Samsung `SM-XXXX...`, tentar também normalizar sufixos regionais (`B/DS`, `F/DS`, `B`, `F`, `U`, `U1`, `W`, `N`, `0`) contra a base `SM-XXXX` (ex.: `SM-S918U1` encontra automaticamente `"Galaxy S23 Ultra"` a partir de `SM-S918B` sem triplicar o JSON!).

---

## Critérios de Aceitação
- [ ] Dois dispositivos ligados em simultâneo podem ser alternados no seletor sem reiniciar a app. *(pendente de Windows: o `ComboBox` está compilado, mas a UI nunca correu nem houve dois telemóveis ligados)*
- [x] `ModelNameTable.Lookup("iPhone15,2")` devolve `"iPhone 14 Pro"` e `Lookup("SM-S918U1")` devolve `"Galaxy S23 Ultra"`. *(prova: `OemHealthTests.CatalogoResolveProductTypeApple` e `SamsungComSufixoRegionalCaiNaBaseEuropeia`)*
- [x] Samsung sem `charge_full` em sysfs mas com `mSavedBatteryAsoc: 86` em `dumpsys battery` preenche `battery.capacity = "86%"`. *(prova: `OemHealthTests.SavedAsocDoFuelGaugeEViraPercentagem` para o parse (`86`) e `AndroidDecisionsTests.CapacityFromSavedAsoc_LeASaudeDoFuelGauge` para o que é escrito — 86 → `pass`/`"86%"`, 79 → `warn`, 59 → `fail`, e `null` sem leitura; `SavedAsocInvalidoDevolveNulo` cobre os lixos. O coletor só aplica o `Reading`)*
- [x] Panics `thermalmonitord` em iPhone são destacados claramente no diagnóstico. *(prova: `OemHealthTests.ThermalmonitordSemCheckinsEEPanicDeFlex` — `IsThermalPanic` e a dica com «flex de carga» — e `OutrosPanicsNaoSaoThermal`; o mapeamento para `logs.thermal` no `IosCollector` é uma linha revista por código)*
