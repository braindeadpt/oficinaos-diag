# T-07 — Extrair Biblioteca `OficinaDiag.Core` (`net8.0`) e Eliminar `<Compile Link=...>` nos Testes

- **Fase:** 3 (Arquitetura & Engenharia de Produção)
- **Dependências:** `T-01` a `T-06` (concluir todas as correções nos ficheiros atuais antes de os mover de projeto, evitando conflitos de merge)
- **Ficheiros em Escopo:**
  - Novo: `OficinaDiag.sln`
  - Novo: `src/OficinaDiag.Core/OficinaDiag.Core.csproj`
  - Mover para `src/OficinaDiag.Core/`:
    - `Devices/*` (`DeviceReport.cs`, `DeviceDetector.cs`, `AndroidCollector.cs`, `IosCollector.cs`, `PowerSession.cs`, `ModelNameTable.cs`)
    - `Data/models.json`
    - `Cloud/*` (`CloudClient.cs`, `DiagConfig.cs`, `IntakeRules.cs`, `UpdateChecker.cs`)
    - `Grading/Grader.cs`
    - `History/ScanHistory.cs`
    - `Log/AppLog.cs`
    - `Reports/ReportBuilder.cs`, `Reports/InsuranceForm.cs` *(nota: `LabelPrinter.cs` usa `System.Windows.Controls.PrintDialog` / `DrawingVisual`, logo fica em `src/OficinaDiag/Reports/LabelPrinter.cs`!)*
    - `Tests/TestServer.cs`, `Tests/wwwroot/device-test.html`
    - `L10n.cs`
  - Atualizar: `src/OficinaDiag/OficinaDiag.csproj`, `src/OficinaDiag/ThemeManager.cs`, `tests/OficinaDiag.Tests/OficinaDiag.Tests.csproj`, `tools/fetch-tools.ps1` (linha 97 onde lê a versão do `iMobileDevice-net` no csproj!).

---

## Contexto Arquitetural

Atualmente, `tests/OficinaDiag.Tests/OficinaDiag.Tests.csproj` compila apenas 9 ficheiros copiados via `<Compile Include="..\..\src\OficinaDiag\..." Link="Linked\..." />` porque `OficinaDiag.csproj` é `net8.0-windows` (`UseWPF=true`), o que impedia `dotnet test` de correr em Linux (`ubuntu-latest` no CI) ou macOS.

Essa abordagem deixou de fora dos testes os módulos mais críticos do sistema (`IosCollector`, `AndroidCollector`, `DeviceDetector`, `ModelNameTable`, `CloudClient`, `TestServer`, `L10n`, `AppLog`) e duplica referências NuGet manualmente.

---

## Especificação de Implementação

### 1. Desacoplar `L10n.cs` de `ThemeManager` (único ponto de contacto com WPF)
- Em `L10n.cs:39-44`, `L10n.Btn` e `L10n.Section` consultam `ThemeManager.IsTerminal`.
- Substituir por `public static bool UseTerminalChrome { get; set; } = true;` dentro de `L10n`.
- Em `ThemeManager.Apply(string name)` (`src/OficinaDiag/ThemeManager.cs`), sincronizar `L10n.UseTerminalChrome = IsTerminal;` antes de invocar `Changed?.Invoke()`.

### 2. Criar `src/OficinaDiag.Core/OficinaDiag.Core.csproj`
- `TargetFramework`: `net8.0` (sem `UseWPF`, compilável em Linux, macOS e Windows!).
- `RootNamespace`: `OficinaDiag` (preserva todos os namespaces existentes `OficinaDiag`, `OficinaDiag.Devices`, `OficinaDiag.Cloud`, `OficinaDiag.Reports`, etc. — zero alterações de `using` nos ficheiros!).
-Adicionar `<InternalsVisibleTo Include="OficinaDiag" />` e `<InternalsVisibleTo Include="OficinaDiag.Tests" />`.
- Mover para `OficinaDiag.Core.csproj` as `PackageReference`s não-WPF:
  - `iMobileDevice-net` (`1.3.17`)
  - `Markdig` (`1.4.0`)
  - `System.Security.Cryptography.ProtectedData` (`8.0.0`)
- Incluir os `Content` partilhados (`Tests\wwwroot\**` e `Data\models.json` com `CopyToOutputDirectory="PreserveNewest"` e `CopyToPublishDirectory="PreserveNewest"`).
- Manter em `src/OficinaDiag/OficinaDiag.csproj` (`net8.0-windows`, `UseWPF=true`):
  - `<ProjectReference Include="..\OficinaDiag.Core\OficinaDiag.Core.csproj" />`
  - `QRCoder` (`1.8.0`) — ou em `Core` se útil para testes, uma vez que `PngByteQRCode` / `QRCodeData` em `QRCoder` é puro `.NET`!
  - O bundling de `..\..\tools\platform-tools\**` (`ExcludeFromSingleFile="true"`).
- **Atenção Crítica (`tools/fetch-tools.ps1:97`):** `fetch-tools.ps1` faz regex à versão de `iMobileDevice-net` lendo `src\OficinaDiag\OficinaDiag.csproj`! Atualizar `fetch-tools.ps1` para procurar em `src\OficinaDiag.Core\OficinaDiag.Core.csproj` (com fallback para `OficinaDiag.csproj`), garantindo que o CI de release não quebra!
- **Atenção Crítica (`Version` em SingleFile / `UpdateChecker`):** Em `MainWindow.xaml.cs` e `DeviceDetector`/`Collectors`, confirmar que `Assembly.GetEntryAssembly()?.GetName().Version ?? Assembly.GetExecutingAssembly().GetName().Version` lê a versão injetada por `/p:Version=...` no `dotnet publish src/OficinaDiag/OficinaDiag.csproj`.

### 3. Limpar `tests/OficinaDiag.Tests/OficinaDiag.Tests.csproj` & Criar `OficinaDiag.sln`
- Remover todos os `<Compile Include="..\..\src\..." Link="..." />` e as `PackageReference`s transitivas (`Markdig`, `ProtectedData`), substituindo por `<ProjectReference Include="..\..\src\OficinaDiag.Core\OficinaDiag.Core.csproj" />`.
- Criar `OficinaDiag.sln` na raiz referenciando `src/OficinaDiag.Core`, `src/OficinaDiag` e `tests/OficinaDiag.Tests`.

---

## Critérios de Aceitação
- [ ] `dotnet test tests/OficinaDiag.Tests -c Release` compila `OficinaDiag.Core` e passa 100% dos testes em Linux/macOS/Windows sem SDK Windows Desktop.
- [ ] `dotnet publish src/OficinaDiag/OficinaDiag.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o out` continua a produzir `OficinaDiag.exe` + `Data/models.json` + `Tests/wwwroot/device-test.html` + `tools/platform-tools/`.
- [ ] `tools/fetch-tools.ps1` continua a encontrar a versão `1.3.17` do `iMobileDevice-net` e a validar todos os 50 hashes SHA-256.
