# T-06 — Estabilidade de Diálogos WPF, Troca de Tema ao Vivo, Performance `ScopeView` e Inglês 100% (`L10n`)

- **Fase:** 2 (WPF UI, Temas & Internacionalização)
- **Bugs Resolvidos:** `B-07`, `B-12a/b`, `B-14`, `B-15`
- **Dependências:** `T-01` e `T-04` (partilham `MainWindow.xaml.cs` e `CloudClient.cs`)
- **Ficheiros em Escopo:**
  - `src/OficinaDiag/MainWindow.xaml.cs`
  - `src/OficinaDiag/SettingsDialog.xaml.cs` e `SettingsDialog.xaml`
  - `src/OficinaDiag/BenchWindow.xaml.cs`
  - `src/OficinaDiag/ChecklistDialog.xaml`
  - `src/OficinaDiag/InsuranceDialog.xaml`
  - `src/OficinaDiag/SendDialog.xaml`
  - `src/OficinaDiag/TokenDialog.xaml` e `TokenDialog.xaml.cs`
  - `src/OficinaDiag/CompareWindow.xaml.cs`
  - `src/OficinaDiag/ScopeView.cs`
  - `src/OficinaDiag/Themes/Win95.xaml`
  - `src/OficinaDiag/L10n.cs`
  - `src/OficinaDiag/Cloud/CloudClient.cs` (substituição de strings hardcoded por `L10n`)

---

## Contexto do Problema

1. **Crash `InvalidOperationException` e mutação prematura em `SettingsDialog.Save_Click` (`B-07`):**
   - Linhas 82-85 alteram `_config.CloudUrl`, `_config.Language` e `_config.Theme` antes de validar/resgatar `TokenBox.Password`. Se o código falhar e o utilizador cancelar, `_config` em memória fica corrompido.
   - Se o utilizador fechar o diálogo (X/Esc) enquanto `await cloud.RedeemPairingAsync(pasted)` aguarda rede, a linha 114 executa `DialogResult = true` numa janela já fechada, crashando a UI thread.
2. **Bug `Detached` com 2 telemóveis, `lang="pt"` fixo no IA e filenames (`B-12`):**
   - `MainWindow.xaml.cs:108-117`: desligar um 2.º telemóvel (`d.Id != _current?.Id`) limpa `StatusText`, cancela `_telemetryCts` e desativa `TestButton`/`BenchButton` apesar de o aparelho ativo continuar ligado!
   - `MainWindow.xaml.cs:681-684`: quando `token is not null`, `Ai_Click` força `lang = "pt"` em vez de `L10n.Current`.
   - `MainWindow.xaml.cs:570, 595`: usar `ReportBuilder.SafeFileToken(_report.Device.Serial)` (criado em `T-04`) nos `SaveFileDialog`.
   - Em `Ai_Click:710`, desativar `AiButton.IsEnabled = false` antes de `await _cloud.RedeemPairingAsync(token)` (dentro do `try/finally`) para impedir duplo clique durante o pairing.
3. **Temas WPF & `ScopeView` (`B-14`):**
   - Mudar do tema `Terminal` para `Fluent` deixa `ResultsList` com verde néon `#33ff33` ilegível em fundo branco porque `ApplyStrings()` não chama `RefreshResults()`.
   - Os 5 diálogos XAML usam `<Style TargetType="Button" BasedOn="{StaticResource TermButton}"/>`, congelando o estilo no tema antigo, e `Win95.xaml:37` ignora `TemplateBinding Background`/`BorderBrush` quando personalizados.
   - `MainWindow.OnClosing` não remove `L10n.Changed -= ApplyStrings` nem `ThemeManager.Changed -= ApplyStrings`.
   - `ScopeView.cs:14-20` cria ~18 instâncias de `Pen` não congeladas a cada `OnRender`.
4. **Textos hardcoded em Português (`B-15`):**
   - `CloudClient.cs` (todas as mensagens de retorno), `CompareWindow.xaml.cs:54,56` (`"grau"`, `"checks"`), `MainWindow.xaml.cs:367` (`"device-test.html não encontrado"`) e `TokenDialog` (`LangBox.Text = "pt"` em vez de `L10n.Current`).

---

## Especificação de Implementação

1. **`SettingsDialog.xaml.cs`:**
   - Adicionar `_closing = false` e `CancellationTokenSource _cts` cancelado em `OnClosing`.
   - Validar `url` para uma variável local `validatedUrl` primeiro; se houver `pasted`, resgatar `RedeemPairingAsync` usando um `CloudClient` apontado para `validatedUrl`; se `_closing || !IsLoaded` após o `await`, sair imediatamente (`return;`). Só gravar `validatedUrl`, `Language`, `Theme` e `SetShopToken` em `_config` imediatamente antes de `DialogResult = true`.
2. **`MainWindow.xaml.cs` & `BenchWindow.xaml.cs`:**
   - Em `Detached`: se `_current?.Id != d.Id`, apenas registar o `Log(...)` e fazer `return;` sem tocar no estado do dispositivo ativo!
   - Em `Ai_Click`: `lang = L10n.Current;` quando `token is not null`, e mover `AiButton.IsEnabled = false; try { ... } finally { AiButton.IsEnabled = true; }` para cobrir também o `RedeemPairingAsync`.
   - Em `ApplyStrings()`: chamar `if (_report is not null) RefreshResults();`.
   - Em `OnClosing`: remover `L10n.Changed -= ApplyStrings; ThemeManager.Changed -= ApplyStrings;`.
   - Em `BenchWindow.RunPhase`: capturar `OperationCanceledException` separadamente de falhas reais para não mostrar erro assustador quando o utilizador simplesmente fecha a janela `SCAN+`.
3. **Diálogos XAML, `Win95.xaml` e `ScopeView.cs`:**
   - Nos 5 diálogos (`ChecklistDialog`, `InsuranceDialog`, `SendDialog`, `SettingsDialog`, `TokenDialog`), aplicar `Style="{DynamicResource TermButton}"` nos `Button`s.
   - Em `ScopeView.cs`, guardar `_gridPen`, `_ampPen`, `_voltPen`, `_tempPen` em campos privados com `.Freeze()`, recriando-os quando `ThemeManager.Changed` disparar (ou quando a referência do `Brush` mudar).
4. **Completar `L10n.cs` (PT + EN):**
   - Criar chaves `cloud.*`, `cmp.grade`, `cmp.checks`, `err.test.htmlmissing` em `pt` e `en` em `L10n.cs` e substituir todas as strings hardcoded em `CloudClient.cs`, `CompareWindow.xaml.cs`, `MainWindow.xaml.cs` e `TokenDialog.xaml.cs` (`LangBox.Text = L10n.Current`).

---

## Critérios de Aceitação
- [ ] Fechar `SettingsDialog` durante um pedido de pairing nunca lança exceção nem altera `_config`.
- [ ] Mudar o tema para `Fluent` ou `Win95` atualiza imediatamente as cores e formato de `ResultsList` e de todos os diálogos.
- [ ] Em modo Inglês (`en`), nenhuma mensagem da Cloud, `CompareWindow` ou Relatório IA sai em Português por engano.
- [ ] `ScopeView.OnRender` faz zero alocações de `Pen` entre frames do mesmo tema.
