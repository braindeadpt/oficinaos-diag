# T-02 — Correção Estrutural do `IosCollector`, Plist `AppleSmartBattery`, Mutex USB e Paridade no `Grader`/`ScanHistory`

- **Fase:** 1 (Bloqueadores Críticos)
- **Bugs Resolvidos:** `B-02`, `B-03`, `B-04`, `B-09`
- **Dependências:** Nenhuma
- **Ficheiros em Escopo:**
  - `src/OficinaDiag/Devices/IosCollector.cs`
  - `src/OficinaDiag/Grading/Grader.cs`
  - `src/OficinaDiag/History/ScanHistory.cs` (verificar compatibilidade com `battery.capacity`)
  - `tests/OficinaDiag.Tests/GraderTests.cs`
  - `tests/OficinaDiag.Tests/ScanHistoryTests.cs`
- **Fora de Escopo:** Não expandir ainda a tabela `Data/models.json` completa (isso é `T-09`), apenas usar `ModelNameTable.Lookup` já existente no `IosCollector`.

---

## Contexto do Problema

1. **`ParsePlistDict` achata sub-dicionários (`B-02`):** `IosCollector.cs:311-330` faz `Descendants("dict")` e escreve `dict[key] = value`. Em `ioregentry AppleSmartBattery`, sub-dicionários como `AdapterDetails` contêm `Voltage` (`9000` mV num carregador USB-C PD 9V) e `Serial`/`Description`, e `ChargerData` contém `IsCharging` (`<integer>0</integer>`), sobrescrevendo os valores reais da célula (`Voltage ~4150 mV`, `IsCharging <true/>`)!
2. **Discrepância `battery.health` vs `battery.capacity` (`B-03`):** `IosCollector.cs:356` grava a % de capacidade em `"battery.health"`, enquanto `AndroidCollector` grava em `"battery.capacity"`. Por isso:
   - `Grader.cs:93-106` nunca atribui Grau D a um iPhone com bateria <60% (dá apenas Grau C) e nunca adiciona `"bateria"` a `SuggestedParts`.
   - `Grader.cs:52-55` define `CriticalChecks = ["battery.capacity", "identity.imei", "security.bootloader"]` e `Grader.cs:192` usa `Any(...)` como gate **apenas para scans falhados**. Em iOS nenhum dos três existe: `IosCollector` nunca grava `security.bootloader` (só `AndroidCollector.cs:122` o faz) e a saúde vai para `battery.health`. Consequência real: um scan iOS falhado cai em `"—"` mesmo tendo lido bateria e identidade; um scan iOS bem-sucedido recebe `A` sem nunca ter lido um check "crítico" (o gate não se aplica).
   - `ScanHistory.BatteryTrend` (`ScanHistory.cs:109`) procura exclusivamente `"battery.capacity"`, ficando 100% morto em iOS.
3. **Corrida USB nativa e processos órfãos (`B-04`):** Quando o timeout de 30s em `CollectAsync` (`IosCollector.cs:213-241`) dispara, a task `collect` continua viva e os processos `idevicediagnostics.exe` / `idevicecrashreport.exe` não são terminados (`cts` também não recebe `Dispose()`). Se `ProbeAsync` (ticker 4s) ou um novo scan correrem para o mesmo UDID, colidem no serviço single-client `com.apple.mobile.diagnostics_relay`.
4. **PII em `identity.model` e UDID em `Device.Serial` (`B-09`):** `IosCollector.cs:81,96-109` deixa o UDID em `r.Device.Serial` (em vez do `SerialNumber` real) e grava `DeviceName` (ex.: *"iPhone de Maria"*) em `identity.model`, expondo o nome pessoal do cliente nos relatórios e histórico em vez do modelo comercial.

**Evidência em hardware (iPhone 12 Pro, `iPhone13,3`, iOS 27.2.1 — 2026-11-12):** o `ParsePlistDict` atual devolve 113 chaves sobre o plist real e corrompe `Watts` (13 → **0**) e `Current` (2550 → **0**, via `AppleRawAdapterDetails`), `AverageBattSkinTemp` (→ **0**, vindo de `DeadBatteryBootData.GeneralPayload`, o que produz `battery.temp = "0 °C"`) e `IsCharging` (`<true/>` → `"1"`). **Não** se confirmou a colisão de `Voltage`: o adaptador real expõe `AdapterVoltage` (5000) e a célula mantém 3914 mV. As capacidades vivem só em `BatteryData` (2790/2798 mAh → 100%), pelo que o achatamento é hoje a única razão pela qual `battery.health` existe em iOS. Fixture: `tests/OficinaDiag.Tests/Fixtures/apple-smart-battery-iphone13_3.plist`.

---

## Especificação de Implementação

### 1. Reescrever `ParsePlistDict` sem achatamento destrutivo (`IosCollector.cs:311-330`)
- Tornar o método `internal static` para poder ser testado diretamente em `T-08`.
- Percorrer **apenas dois níveis**: o `<dict>` de topo (dentro de `<plist>`, descendo ao dict da entrada `IORegistry` quando o raiz tiver um único filho `<dict>`) e os sub-dicionários imediatos `AdapterDetails` e `BatteryData`.
- Regra de precedência: escalares da célula escrevem **sem prefixo** e **nunca** são sobrescritos por um sub-dicionário (topo primeiro, depois `TryAdd`); os sub-dicionários escrevem **sempre** com prefixo — `AdapterDetails.Watts`, `AdapterDetails.AdapterVoltage`, `BatteryData.NominalChargeCapacity`, `BatteryData.DesignCapacity`, …
- **Não** parar antes de `BatteryData` (ao contrário da primeira versão deste ticket): `NominalChargeCapacity`, `DesignCapacity`, `FullChargeCapacity` e `AppleRawMaxCapacity` existem **só** lá. Lidos apenas do topo, `battery.health` desaparecia por completo. `CycleCount`, `Voltage`, `IsCharging`, `InstantAmperage` e `CurrentCapacity` estão no topo.
- Ignorar os restantes sub-dicionários (`ChargerData`, `PowerTelemetryData`, `DeadBatteryBootData`, `AppleRawAdapterDetails`, `AdapterInfo`, `PowerDistribution`, …): são eles que injetam `Watts = 0`, `Current = 0` e `AverageBattSkinTemp = 0`.
- Atualizar os leitores: `ProbeAsync` lê `AdapterDetails.Watts` (não `Watts`) e `CollectBatteryHealth` usa `BatteryData.*` para capacidade e `AppleRawMaxCapacity` como fallback.
- Rejeitar payload que não seja plist (`xml.Contains("<plist")`): `idevicediagnostics` com argumentos inválidos devolve **exit 0** e imprime o *usage* no stdout — hoje isso entra como «plist vazio» e o coletor salta a bateria em silêncio.
- Usar `CultureInfo.InvariantCulture` em todos os `TryParse` numéricos dentro de `IosCollector.cs` (`NumberStyles.Float` / `NumberStyles.Integer`).

### 2. Unificar Contrato de Bateria e Identidade iOS + `Grader.cs`
- Em `IosCollector.CollectBatteryHealth`:
  - Gravar a percentagem (`NominalChargeCapacity / DesignCapacity`) em **`"battery.capacity"`** (`< 60 ? "fail" : < 80 ? "warn" : "pass"`, alinhado com os limiares comerciais do `Grader`, mantendo no detalhe `"{max}/{des} mAh — carga real vs fábrica"`).
- Em `IosCollector` (identidade):
  - Quando `SerialNumber` for lido de `lockdownd` (`linha 109`), atualizar `r.Device.Serial = serial;` (mantendo o UDID apenas se `SerialNumber` falhar).
  - Resolver `var mkt = ModelNameTable.Lookup(r.Device.Model);` (`ProductType`, ex. `iPhone15,2`). Definir `r.Device.MarketingName = mkt;` e gravar em `"identity.model"` o texto comercial `mkt is not null ? $"{mkt} ({r.Device.Model})" : r.Device.Model` — **nunca** gravar `DeviceName` pessoal no relatório/histórico!
- Em `Grader.cs`:
  - Em `CriticalChecks` (`linhas 52-55`), incluir `"battery.health"` ao lado de `"battery.capacity"`. **Não** acrescentar `"identity.serial"`: `DeviceReport.Set` grava sempre a chave (mesmo com valor `null`) nos dois coletores, logo `Results.ContainsKey("identity.serial")` é sempre verdadeiro e o gate `"—"` (`Grader.cs:192`) deixava de funcionar.
  - Manter o `Any(...)` de `Grader.cs:192` — o gate tem de continuar a distinguir "scan falhado sem nenhum check de base" (`"—"`) de "scan falhado com bateria lida" (grau parcial, teto B em `Grader.cs:198-199`).
  - Nas linhas `105-106` (retrocompatibilidade com relatórios históricos que só tenham `battery.health`): se `Stat("battery.health") == "fail"`, aplicar `Cap(3, ...)` + `parts.Add("bateria")`; se `"warn"`, aplicar `Cap(2, ...)` + `parts.Add("bateria")`.

### 3. Sincronização USB por UDID e Cancelamento Limpo (`IosCollector.cs`)
- Criar `private static readonly ConcurrentDictionary<string, SemaphoreSlim> DeviceLocks = new(StringComparer.OrdinalIgnoreCase);` partilhado por `CollectAsync` e `ProbeAsync`.
- Em `ProbeAsync`, usar `if (!await gate.WaitAsync(1500)) return null;` (se um scan estiver em curso, o ticker salta silenciosamente sem bloquear).
- Em `CollectAsync`, adquirir `await gate.WaitAsync(ct)` com timeout de 30s, passar `CancellationToken` a `RunTool` e `CollectCrashLogs`, registar `using var reg = ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } });`, aguardar drenagem segura e fazer `Dispose()` do `CancellationTokenSource` apenas quando a task `collect` terminar (`ContinueWith(_ => cts.Dispose())`).

### 4. Testes Unitários (`GraderTests.cs` & `ScanHistoryTests.cs`)
- Adicionar testes em `GraderTests` para:
  - iPhone com `battery.capacity = "55%"` (`fail`) → Grau `D` + `SuggestedParts` contém `"bateria"`.
  - Relatório legado iOS apenas com `battery.health = "55%"` (`fail`) → Grau `D` + `"bateria"`.
  - iPad Wi-Fi (sem `identity.imei` e sem `security.bootloader`) com `scan: fail` mas com `battery.health` lido → recebe grau parcial (não `"—"`); o mesmo relatório sem `battery.health` nem `battery.capacity` → continua `"—"`.

---

## Critérios de Aceitação
- [x] `ParsePlistDict` nunca deixa `AdapterDetails.*`/`BatteryData.*` sobrescrever `Voltage`, `IsCharging` ou `Current` da célula (fixture iPhone 12 Pro: `Voltage` = 3914 mV, `IsCharging` = `true`). *(prova: `IosCollectorParsingTests.SubDictsNaoPisamACelula`, com a fixture real `apple-smart-battery-iphone13_3.plist`)*
- [x] Sobre a fixture, `Watts` resolve 13 W (não 0) e `battery.temp` **não** é emitido com `0 °C`. *(prova: `IosCollectorParsingTests.FixtureRealDoIphone13_3_TemCapacidadeDoBatteryData`; `Watts`/`temp` confirmados no handoff de 2026-11-12 contra o iPhone 12 Pro real)*
- [x] `battery.health` continua a sair de `BatteryData` (2790/2798 → 100%) depois da reescrita do parser. *(prova: `IosCollectorParsingTests.FixtureRealDoIphone13_3_TemCapacidadeDoBatteryData`)*
- [x] `ScanHistory.BatteryTrend` devolve tendência para iPhones após 2 scans. *(prova: `ScanHistoryTests`)*
- [ ] `DeviceName` pessoal do cliente deixa de aparecer em `identity.model`. *(pendente de teste automático: a correção está implementada e revista por código, mas nenhum teste afirma a ausência de PII)*
- [ ] Nenhum processo `idevicediagnostics.exe` / `idevicecrashreport.exe` fica órfão após timeout. *(pendente de hardware/Windows: só se observa com iPhone ligado e o coletor a correr)*
