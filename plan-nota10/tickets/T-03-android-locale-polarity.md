# T-03 — Cultura Invariante (`pt-PT`), Polaridade de Corrente OEM Android e Normalização ADB

- **Fase:** 1 (Bloqueadores Críticos & Altos)
- **Bugs Resolvidos:** `B-05`, `B-06`, `B-11`, `B-19`, `B-20`
- **Dependências:** `T-02` (caso toque nos `TryParse` de `IosCollector.cs`; em `AndroidCollector.cs` é independente)
- **Ficheiros em Escopo:**
  - `src/OficinaDiag/Devices/AndroidCollector.cs`
  - `src/OficinaDiag/Devices/DeviceDetector.cs` (kill process tree + serials seguros)
  - `src/OficinaDiag/Devices/PowerSession.cs` (validação de compatibilidade com `Charging`)
  - `tests/OficinaDiag.Tests/PowerAnalyzerTests.cs`
- **Fora de Escopo:** Não adicionar ainda `mSavedBatteryAsoc` nem modos `fastboot`/`recovery` (reservados para `T-09`).

---

## Contexto do Problema

1. **Vírgula decimal em Windows `pt-PT` (`B-05`):** Em `AndroidCollector.cs:219, 245, 370, 372, 397`, `double.TryParse` sem `CultureInfo.InvariantCulture` interpreta `"."` como separador de milhares! Se `/sys/class/thermal/thermal_zone*/temp` devolver `"38.5"`, em `pt-PT` é lido como `385`, passa `raw <= 500` (`linha 398`) e reporta **385 °C** de temperatura de SoC, disparando falsos alertas térmicos no `PowerSession` / `Grader`.
2. **Polaridade de `current_now` invertida em Samsung Exynos, MediaTek e Huawei (`B-06`):** `AndroidCollector.ProbeAsync` (`linhas 362-369`) define `charging = cur < 0 ? false : cur > 0 ? true : null`. Em muitos kernels OEM, corrente **negativa** significa **a carregar**! Resultado: `PowerAnalyzer` vê 0 amostras com `Charging != false` e emite falso alerta `"warn: não entra carga pela porta"`, penalizando telemóveis novos para Grau C + peça `"porta de carga"`.
3. **Contas multiplicadas, capacidade em `µAh` e `ArgumentList` ADB (`B-11`):**
   - `dumpsys account` (`linha 150`, regex na `153`): a regex global conta todas as repetições de `type=com.google` em secções de sync/cache (ex.: reporta 14 contas quando só existe 1).
   - `charge_full` / `charge_full_design` (`linhas 265-276`): imprime valores crus em microampere-hora (`Capacidade real 4850000 / design 5000000`) sem converter para `mAh`.
   - `AdbAsync` (`linhas 18-32`): interpola `-s {serial}` numa string única sem quoting seguro (`linha 22`) e faz `p.Kill()` sem `entireProcessTree: true` (`linha 32`).
4. **Falso positivo de MDM (`B-19`, detetado no rig Android):** `AndroidCollector.cs:167` decide com `policy.Contains("Device Owner") && !policy.Contains("No device owner")`. Num Android sem gestão o dump traz a linha `Device Owner Type: -1` — a substring «Device Owner» casa e o check sai `warn "gestão remota ativa"`. Como `security.mdm` está em `Grader.HardLocks`, o grau cai para **D** (`Cap(3)`) num aparelho limpo.
5. **Ausência de leitura vira valor (`B-20`, detetado no rig Android):** dois padrões distintos:
   - `PropAsync` (`:38`) devolve `""` quando a prop não existe (o `getprop` imprime só `\n`) e os consumidores só testam `is null`: `security.bootloader` (`:120`) sai `warn` com valor vazio em vez de `skipped`; `device.baseband` e `device.sim` ficam `info` com valor vazio.
   - `dumpsys battery` (`:212`) traz `present: false` e o coletor ignora-o: `health: 1` (`BATTERY_HEALTH_UNKNOWN`) cai no `_ => health` de `:216` e sai `warn "1"`; `voltage: 0` (`:244-249`) vira `warn "0,00 V — fora da faixa normal"` (e sugere a peça «porta de carga»); `temperature: 0` (`:219`) vira `info "0°C"`; `battery.capacity_learned` (`:282`) apanha o default do `batterystats` («Estimated battery capacity: 1000 mAh»).
   - Observado em 2026-11-12 na `diag-android` (Android 14 `redroid14_x86_64`, sem bateria): 23 checks, `scan=pass`, **Grau D** com `pecas=[]` e razões «gestão MDM ativa; …; estado da bateria: 1».

---

## Especificação de Implementação

### 1. `CultureInfo.InvariantCulture` em 100% dos Parses (`AndroidCollector.cs`)
- Substituir **todos** os `int.TryParse`, `long.TryParse` e `double.TryParse` em `AndroidCollector.cs` para passarem explicitamente `NumberStyles.Integer, CultureInfo.InvariantCulture` ou `NumberStyles.Float, CultureInfo.InvariantCulture`.
- Em `ProbeSocTempAsync` (`linhas 396-402`), descartar leituras implausíveis (`c is < -10 or > 115`) para evitar zonas térmicas dummy que certos kernels Qualcomm reportam como `125000` ou `-273000` m°C!

### 2. Ler `status` Real da Bateria em `ProbeAsync` (`AndroidCollector.cs:357-379`)
- Alterar o comando `adb shell` de `ProbeAsync` para ler 4 ficheiros numa única invocação:
  `for f in current_now voltage_now temp status; do cat /sys/class/power_supply/battery/$f 2>/dev/null || echo _; done`
- Determinar `bool? charging` a partir de `status` (`"Charging"` ou `"Full"` → `true`; `"Discharging"` ou `"Not charging"` → `false`), usando o sinal de `current_now` apenas como último recurso quando `status` for `"_"`!

### 3. Contas Únicas, Conversão `µAh → mAh` e Processos ADB Seguros
- **Contas (`linha 154`):** Usar regex `Account\s*\{\s*name=([^,}]+),\s*type=(com\.google|com\.samsung|com\.huawei|com\.xiaomi|com\.oppo)\b` e contar `.Select(m => (Name: m.Groups[1].Value.Trim(), Type: m.Groups[2].Value)).Distinct(StringComparer.OrdinalIgnoreCase).Count()`.
- **Capacidade (`linhas 270-277`):** Normalizar `cf` e `cd` separadamente: `var cfMah = cf > 100_000 ? cf / 1000.0 : cf;` e `var cdMah = cd > 100_000 ? cd / 1000.0 : cd;` (protegendo também kernels que reportam um em µAh e outro em mAh!) e formatar `$"Capacidade real {cfMah:0} / design {cdMah:0} mAh"`.
- **Segurança de Processo (`AndroidCollector.cs:17-36` e `DeviceDetector.cs:54`):** Usar `p.Kill(entireProcessTree: true)` em todos os timeouts e validar/escapar `serial` antes de invocar `adb`.

### 4. Falsos positivos por ausência de dado (`B-19`, `B-20`)
- **`PropAsync` (`:38`):** devolver `null` quando o valor é vazio/branco — `var v = (await ...)?.Trim(); return string.IsNullOrEmpty(v) ? null : v;`. Corrige `security.bootloader` → `skipped` e omite `device.baseband`/`device.sim` em vez de linhas em branco.
- **MDM (`:167`):** trocar a substring por evidência de owner real — ex.: `Regex.Match(policy, @"Device Owner Type:\s*(-?\d+)")` com `hasOwner = m.Success && int.Parse(m.Groups[1].Value) > -1` (ou exigir componente `pacote/serviço` numa linha `Device Owner:`). Fixture: `Device Owner Type: -1` → `pass "sem gestão remota"`.
- **Bateria ausente (`:212-249`):** ler `present: (true|false)`; se `false`, marcar `battery.*` como `skipped` («sem bateria exposta») e não emitir level/health/voltage/temp. Com `present: true`: `health: 1` → `skipped` (UNKNOWN, não é defeito); `voltage: 0` → omitir (nunca `warn`); `temperature: 0` → omitir.
- **`capacity_learned` (`:282`):** só aceitar com bateria presente e sem ser o default de 1000 mAh do `batterystats` quando não há `design` conhecido.

### 5. Testes Unitários (`PowerAnalyzerTests.cs`)
- Extrair os helpers de parsing (`ParseSocTempLines`, `ParseAccountCount`, `NormalizeMah`, `ParseBatteryProbeLines`) como métodos `internal static` puros (ou adicionar testes em `PowerAnalyzerTests`) testando-os sob `CultureInfo.CurrentCulture = new CultureInfo("pt-PT")`.

---

## Critérios de Aceitação
- [x] Sob `CultureInfo("pt-PT")`, `"38.5"` em `thermal_zone` resulta em `38.5 °C` (nunca `385 °C`). *(prova: `AdbParsingTests.SocTemp_PtPt_LePontoComoDecimal`)*
- [x] Um Samsung/MediaTek com `current_now = -1200000` e `status = Charging` tem `Charging == true` e `Milliamps == 1200`. *(prova: `AdbParsingTests.CorrenteNegativaComStatusCharging_ECarga`)*
- [x] `dumpsys account` com 1 conta Google repetida 12 vezes nos logs de sync reporta `1 conta(s)`. *(prova: `AdbParsingTests.ContasRepetidasNoDump_ContamUma`)*
- [x] Capacidade real/design aparece sempre em `mAh` legíveis (`4850 / 5000 mAh`). *(prova: `AdbParsingTests.Capacidade_EmMicroAh_ViraMah`)*
- [x] Com `Device Owner Type: -1`, `security.mdm` é `pass` e o `Grader` não aplica `Cap(3)`. *(prova: `AdbParsingTests.DeviceOwnerTypeMenos1_NaoEMdm` — o B-19 vivia neste predicado —, `GraderTests.MdmWarn_EHardLock_D` para o gating e `AndroidDecisionsTests.Mdm_SoComDeviceOwnerReal`/`Mdm_SemGestaoNaoTemNotaDeRisco` para a tradução em `pass`/`warn`, que saiu do coletor para `AndroidDecisions`)*
- [x] Sem bateria (`present: false`), nenhum `battery.*` sai `warn` nem com valor inventado, e `security.bootloader` sai `skipped` com a prop vazia. *(prova: a decisão saiu do coletor para `AndroidDecisions` e está testada — `AndroidDecisionsTests.BatteryPresence_AusenteEDescritaComoSkipped` (present=false → `skipped`, `null`/`true` → sem registo) e `Bootloader_PropAusenteESkippedEDesbloqueadoEWarn` (prop ausente → `skipped`, `green` → `pass`, outro → `warn`); o coletor só aplica o `Reading`. Complementado por `AdbParsingTests.PresentFalse_EBancada`/`BatteryProbe_TemperaturaZero_NaoEVirouMedicao` e `GraderTests.SkippedNaoDisparaHardLock`)*
