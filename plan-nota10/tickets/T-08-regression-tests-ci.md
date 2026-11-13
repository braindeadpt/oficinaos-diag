# T-08 — Suite Completa de Testes de Regressão (iOS, Android, Locale `pt-PT`, XSS, `L10n` e Temas XAML) + CI Windows

- **Fase:** 3 (Qualidade & Prevenção de Regressões)
- **Dependências:** `T-07` (`OficinaDiag.Core` extraído e acessível por `OficinaDiag.Tests`)
- **Ficheiros em Escopo:**
  - Novos em `tests/OficinaDiag.Tests/`:
    - `IosCollectorParsingTests.cs`
    - `AndroidCollectorParsingTests.cs`
    - `TestServerTests.cs`
    - `L10nAndThemesTests.cs`
  - Atualizar:
    - `tests/OficinaDiag.Tests/ReportBuilderTests.cs`
    - `.github/workflows/ci.yml`

---

## Contexto

Com `OficinaDiag.Core` referenciado diretamente em `OficinaDiag.Tests` (`T-07`), podemos blindar com testes rápidos (< 2 segundos em Linux/macOS/Windows) todas as classes onde residiam os bugs críticos e altos desta auditoria, além de validar que o projeto WPF (`src/OficinaDiag`) nunca deixa de compilar num PR.

---

## Especificação de Implementação

### 1. `IosCollectorParsingTests.cs`
- Testar `IosCollector.ParsePlistDict` com uma fixture XML realista de `ioregentry AppleSmartBattery` contendo:
  - No topo: `<key>Voltage</key><integer>4152</integer>`, `<key>IsCharging</key><true/>`, `<key>NominalChargeCapacity</key><integer>2450</integer>`, `<key>DesignCapacity</key><integer>4323</integer>`, `<key>AverageBattSkinTemp</key><real>31.456</real>`, `<key>Serial</key><string>F8Y1234567</string>`.
  - Aninhados: `<key>AdapterDetails</key><dict><key>Voltage</key><integer>9000</integer><key>Watts</key><integer>20</integer><key>Description</key><string>usb host</string><key>Serial</key><string>ADP999</string></dict>` e `<key>ChargerData</key><dict><key>IsCharging</key><integer>0</integer></dict>`.
- Asserções obrigatórias (a correr dentro de um helper `WithCulture("pt-PT", () => ...)`):
  - `Voltage` lido é `4152` (4.152 V) e **nunca** `9000` (9.0 V).
  - `IsCharging` é `"true"` e **nunca** `"0"`.
  - `Serial` é `"F8Y1234567"` e `Watts` (`20`) continua disponível.
  - `AverageBattSkinTemp` é `31.456 °C` (e `"31.5"` é `31.5 °C`, nunca `3.15 °C` nem `314.56 °C`).
  - `Grader.Compute` sobre o relatório resultante (57% capacidade) devolve Grau `"D"` e `SuggestedParts` contém `"bateria"`.

### 2. `AndroidCollectorParsingTests.cs`
- Correr sob `CultureInfo("pt-PT")` e `CultureInfo("en-US")`:
  - `thermal_zone` com `"38.5"`, `"41200"` e `"125000"` (zona dummy ignorada) → `38.5 °C` / `41.2 °C`.
  - `Probe` com `current_now = "-1450000"`, `voltage_now = "4210000"`, `temp = "334"`, `status = "Charging"` → `Milliamps = 1450`, `Charging = true`, `Volts = 4.21`, `TempC = 33.4`, e `PowerAnalyzer.Analyze` reporta `ChargeStatus == "pass"`.
  - `dumpsys account` com 1 conta `Account {name=loja@gmail.com, type=com.google}` repetida em 5 blocos de sync → conta exatamente `1`.
  - Capacidade `4850000` / `5000000` → texto `"4850 / 5000 mAh"` e `"97%"`.

### 3. `TestServerTests.cs`
- Testar `PickBestLanIp`: interface Wi-Fi `10.10.2.15` com gateway `10.10.2.1` ganha sempre contra `VirtualBox Host-Only Network` `192.168.56.1` sem gateway.
- Arrancar uma instância real de `TestServer` em `IPAddress.Loopback`:
  - `GET /{token}` (sem barra final) e `GET /{token}/?qr=1` devolvem `200 OK` com HTML.
  - `POST /{token}/result` com JSON válido resolve `WaitForResultAsync` e encerra o listener (one-shot).
  - Pedido com header malformado sem `\r\n\r\n` ou path errado não levanta exceção não tratada.

### 4. `L10nAndThemesTests.cs`
- Verificar por reflexão/acesso `internal` ao dicionário `L10n.D`:
  - Conjunto de chaves de `"pt"` é **100% idêntico** ao de `"en"` (`Assert.Empty(ptKeys.Except(enKeys))` e vice-versa).
  - Para cada chave, contar os placeholders `{0}`, `{1}`, ... em `"pt"` e confirmar que `"en"` usa exatamente os mesmos índices e que `string.Format` com argumentos dummy não lança `FormatException`.
- Ler por XML (`XDocument.Load`) os 3 ficheiros XAML em `src/OficinaDiag/Themes/` (`Terminal.xaml`, `Fluent.xaml`, `Win95.xaml`) e provar que os 3 definem exatamente o mesmo conjunto de `x:Key`!

### 5. CI Build WPF Windows (`.github/workflows/ci.yml`)
- Manter o job rápido `test` em `ubuntu-latest` (`dotnet test tests/OficinaDiag.Tests -c Release`) e acrescentar um job `build-wpf` (pode até correr em `ubuntu-latest` com `-p:EnableWindowsTargeting=true` em 15 segundos sem gastar minutos caros de runner Windows! Testar se `dotnet build src/OficinaDiag/OficinaDiag.csproj -c Release -p:EnableWindowsTargeting=true` compila limpo em Linux; caso algum target exija Windows, usar `windows-latest`).

---

## Critérios de Aceitação
- [x] Todos os novos testes passam em `dotnet test -c Release`. *(prova: **214/214**, exit 0)*
- [x] Qualquer chave `L10n` adicionada apenas em PT (ou `x:Key` adicionada apenas num tema XAML) quebra imediatamente o teste unitário. *(prova: `L10nAndThemesTests` — paridade PT/EN e os 3 temas com o mesmo conjunto de `x:Key`)*
- [ ] O CI valida tanto a suite `OficinaDiag.Tests` como a compilação completa do `OficinaDiag` WPF. *(pendente de CI: o job `build-wpf` está escrito e validado localmente, mas nunca correu no GitHub)*
