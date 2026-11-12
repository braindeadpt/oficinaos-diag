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

---

## Especificação de Implementação

### 1. Reescrever `ParsePlistDict` sem achatamento destrutivo (`IosCollector.cs:311-330`)
- Tornar o método `internal static` para poder ser testado diretamente em `T-08`.
- Localizar o `<dict>` de topo dentro de `<plist>` (ou da entrada `AppleSmartBattery` caso `ioregentry` envolva num dict contentor; se o dict raiz tiver apenas 1 filho `<dict>`, descer a esse dict da entrada IORegistry, mas **parar antes** de sub-dicionários como `AdapterDetails`, `ChargerData`, `BatteryData`, `FedDetails`).
- Ler todos os escalares do `<dict>` principal primeiro. Para sub-dicionários imediatos (ex.: `AdapterDetails`), usar `dict.TryAdd(key, val)` **e** guardar também com prefixo `AdapterDetails.Watts`, `AdapterDetails.Description`, `AdapterDetails.Voltage` — assim `Watts` e `Description` continuam acessíveis, mas `Voltage`, `IsCharging`, `Serial` e `Manufacturer` da célula **nunca** são sobrescritos!
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
- [ ] `ParsePlistDict` nunca deixa `AdapterDetails.Voltage` (9000 mV) sobrescrever `Voltage` da célula (~4150 mV).
- [ ] `ScanHistory.BatteryTrend` devolve tendência para iPhones após 2 scans.
- [ ] `DeviceName` pessoal do cliente deixa de aparecer em `identity.model`.
- [ ] Nenhum processo `idevicediagnostics.exe` / `idevicecrashreport.exe` fica órfão após timeout.
