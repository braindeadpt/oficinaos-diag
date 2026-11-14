# T-01 — Isolar MSIX Store vs Portable, Auto-Update Atómico Consciente de ADB e Hardening de Scripts

- **Fase:** 1 (Bloqueadores Críticos)
- **Bugs Resolvidos:** `B-01`, `B-16`
- **Dependências:** Nenhuma
- **Ficheiros em Escopo:**
  - `src/OficinaDiag/Cloud/UpdateChecker.cs`
  - `src/OficinaDiag/MainWindow.xaml.cs` (apenas linhas 118-193 — `CheckForUpdateAsync` / `Update_Click`)
  - `tools/sign-release.ps1`
  - `tools/build-store.ps1`
  - `tools/fetch-tools.ps1`
  - `tests/OficinaDiag.Tests/UpdateCheckerTests.cs`
- **Fora de Escopo:** Não mexer em `SettingsDialog`, `Ai_Click`, `Send_Click` ou coletores Android/iOS.

---

## Contexto do Problema

1. **MSIX / Microsoft Store (`B-01a`):** `MainWindow.OnLoaded` invoca `CheckForUpdateAsync()` incondicionalmente. Na versão empacotada para a Microsoft Store (`packaging/store/AppxManifest.xml`), o diretório `AppContext.BaseDirectory` fica em `C:\Program Files\WindowsApps\...` (read-only), quebrando a extração e violando a Política 10.2.1 da Store (proibição de auto-updater externo).
2. **Corrupção de instalação portátil com `adb.exe` ativo (`B-01b`):** Em `UpdateChecker.cs:163-180`, após um scan Android, o daemon `adb.exe fork-server server` permanece vivo após `Application.Current.Shutdown()`. Passados 3s (`Start-Sleep -Seconds 3`), o `tar -xf` falha ao sobrescrever `adb.exe` / `AdbWinApi.dll` e o bloco de rollback restaura **apenas** `OficinaDiag.exe.bak`, deixando os restantes ficheiros extraídos num estado híbrido.
3. **Scripts de Release e Build (`B-16`):**
   - `tools/sign-release.ps1:80` usa `gh api ... > $sumsPath`, que no PowerShell passa o stream binário pelo formatador de texto (podendo normalizar `\n` → `\r\n` e invalidar a assinatura ECDSA verificada sobre os bytes originais em `UpdateChecker`).
   - `tools/build-store.ps1:28` publica com `dotnet publish` e empacota o `.msix` sem verificar se `tools\platform-tools\adb.exe` existe (`OficinaDiag.csproj:30` tem `Condition="Exists(...)"`, omitindo silenciosamente o suporte Android).

---

## Especificação de Implementação

### 1. Deteção de Pacote MSIX (`UpdateChecker.cs` + `MainWindow.xaml.cs`)
- Em `UpdateChecker.cs` (que também compila em Linux nos testes `net8.0`!), expor `public static bool IsPackagedApp()`:
  - Em `!OperatingSystem.IsWindows()`, devolver `false`.
  - Em Windows, usar P/Invoke para `kernel32.dll` `GetCurrentPackageFullName(ref uint packageFullNameLength, char[]? packageFullName)` — se o retorno for diferente de `15700` (`APPMODEL_ERROR_NO_PACKAGE`), devolver `true`.
  - Como segunda defesa em `DownloadAndStageAsync`, testar se `appDir` é gravável criando/apagando um ficheiro sonda temporário antes de descarregar os 100 MB; se `IsPackagedApp()` for `true`, devolver logo `null` em `CheckAsync` e `(false, ...)` em `DownloadAndStageAsync`.
- Em `MainWindow.xaml.cs:119`, só chamar `_ = CheckForUpdateAsync();` quando `!Cloud.UpdateChecker.IsPackagedApp()`.

### 2. Script de Atualização Atómica e Consciente de Processos (`UpdateChecker.cs:160-190`)
- Passar `Environment.ProcessId` ao script PowerShell gerado:
  1. `Wait-Process -Id <PID> -Timeout 30 -ErrorAction SilentlyContinue` (substituindo o frágil `Start-Sleep -Seconds 3`).
  2. Se existir `tools\platform-tools\adb.exe` dentro de `appDir`, executar `& "$adb" kill-server 2>$null` e `Stop-Process -Name adb,idevicediagnostics,idevicecrashreport -Force -ErrorAction SilentlyContinue`.
  3. Extrair o ZIP para uma pasta temporária limpa `$stage = Join-Path ([IO.Path]::GetTempPath()) "oficinaos-stage-<tag>-<guid>"` com `tar -xf`.
  4. Verificar `$LASTEXITCODE -eq 0` e que `Test-Path (Join-Path $stage 'OficinaDiag.exe')` é verdadeiro **antes** de tocar em `appDir`.
  5. Validar que nenhuma entrada extraída escapa de `$stage` (defesa ZipSlip por symlinks/paths relativos).
  6. Só então fazer backup de `OficinaDiag.exe` → `OficinaDiag.exe.bak` e `Copy-Item -Path "$stage\*" -Destination $appDir -Recurse -Force`, revertendo em `catch` se a cópia falhar, arrancando `Start-Process $exe` e limpando `$stage`, `.bak`, `.zip` e `.ps1`.

### 3. Hardening dos Scripts PowerShell (`tools/*.ps1`)
- **`tools/sign-release.ps1:80`:** Substituir `> $sumsPath` por download binário direto sem re-encoding de texto (ex.: `gh api "repos/$Repo/releases/assets/$($sums.id)" -H 'Accept: application/octet-stream' > ...` substituído por invocação direta `Start-Process gh -ArgumentList ... -RedirectStandardOutput $sumsPath -NoNewWindow -Wait` — que grava os bytes crus da stdout sem pipeline PowerShell!).
- **`tools/build-store.ps1`:** Logo antes do `dotnet publish`, verificar `if (-not (Test-Path (Join-Path $root 'tools\platform-tools\adb.exe'))) { throw "platform-tools em falta — corre .\tools\fetch-tools.ps1 antes de empacotar para a Store." }`.
- **`tools/fetch-tools.ps1:23-34`:** Contabilizar `$checked = 0` no loop `Get-ChildItem` de `platform-tools` e fazer `if ($checked -eq 0) { throw "nenhum binário verificado em platform-tools" }` por defesa em profundidade.

### 4. Testes Unitários (`UpdateCheckerTests.cs`)
- Adicionar testes que validem que `IsPackagedApp()` retorna `false` em execução normal (Linux/macOS/unpackaged Windows) e extrair a geração do script PS1 para um método `internal static string BuildUpdateScript(...)` testando que inclui `Wait-Process -Id`, `kill-server`, extração em pasta `stage` e quoting seguro `Psq`.

---

## Critérios de Aceitação
- [x] `UpdateCheckerTests` passa em `net8.0`. *(prova: `UpdateCheckerTests` na suite — **284/284** em macOS com SDK 8.0.428; Linux/Windows provados no CI, corrida `43536335618` com 284/284 nos dois runners)*
- [x] Em modo MSIX, nenhuma chamada de rede ao GitHub Releases é feita no arranque — gate em `MainWindow.OnLoaded` e em `CheckAsync`. *(prova: gate revisto por código nos dois pontos; não exercitado num MSIX real, que é a limitação registada no STATUS)*
- [x] Uma falha de `tar -xf` nunca toca nos ficheiros existentes em `appDir` — staging + validação antes do backup/cópia. *(prova: `UpdateCheckerTests` — asserção de ordem no staging e o `tar` por caminho absoluto)*
- [x] `sign-release.ps1` preserva os bytes exatos de `SHA256SUMS.txt` (cópia do `BaseStream`) e `build-store.ps1` recusa gerar MSIX sem `adb.exe`. *(prova: sintaxe validada por parser de PowerShell; fluxo `gh`/MSIX não executado aqui — limitação no STATUS)*
