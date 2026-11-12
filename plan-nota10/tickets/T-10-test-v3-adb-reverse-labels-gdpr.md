# T-10 — On-Device Test v3 (`adb reverse` Zero-Wi-Fi + Microfone/Auricular), Etiquetas Térmicas & Retenção RGPD

- **Fase:** 4 (Diferenciadores "Nota 10" para Bancada Profissional)
- **Dependências:** `T-07`, `T-08` (e idealmente após `T-09` por partilhar `MainWindow.xaml.cs` e `L10n.cs`)
- **Ficheiros em Escopo:**
  - `src/OficinaDiag.Core/Tests/TestServer.cs`
  - `src/OficinaDiag.Core/Tests/wwwroot/device-test.html`
  - `src/OficinaDiag.Core/Grading/Grader.cs`
  - `src/OficinaDiag.Core/Reports/ReportBuilder.cs` (`FriendlyLabel`)
  - `src/OficinaDiag.Core/Cloud/DiagConfig.cs`
  - `src/OficinaDiag.Core/History/ScanHistory.cs`
  - `src/OficinaDiag.Core/L10n.cs`
  - `src/OficinaDiag/Reports/LabelPrinter.cs`
  - `src/OficinaDiag/MainWindow.xaml.cs`
  - `src/OficinaDiag/SettingsDialog.xaml` e `src/OficinaDiag/SettingsDialog.xaml.cs`
  - `tests/OficinaDiag.Tests/`

---

## Objetivo ("Nota 10" Operacional & Legal)

Fechar os 3 maiores atritos práticos numa loja real:
1. **Redes Wi-Fi de loja com isolamento de cliente (AP Isolation) ou telemóveis de cliente sem password do Wi-Fi:** no Android o cabo USB já está ligado — com `adb reverse` o teste abre em `http://127.0.0.1:<port>/` **sem precisar de Wi-Fi nenhum** e ganha **Secure Context** no browser para testar o microfone!
2. **Impressoras térmicas de bancada (Brother QL / Dymo / Zebra):** ajustar layout ao rolo real.
3. **Conformidade RGPD em loja:** retenção temporal automática e botão de limpeza do histórico local com IMEIs/seriais.

---

## Especificação de Implementação

### 1. Modo Dual `Loopback + LAN` no `TestServer` + Túnel USB `adb reverse` para Android
- Em `TestServer`:
  - Permitir escutar em `IPAddress.Any` (ou dois `TcpListener`: `IPAddress.Loopback` + `_bindAddress` LAN na mesma porta, rejeitando qualquer IP remoto que não seja Loopback nem pertença à sub-rede privada RFC1918!), para que tanto `127.0.0.1` (túnel ADB USB) como `<lanIp>` (QR code iPhone/Wi-Fi) funcionem na mesma sessão!
- Em `MainWindow.Test_Click` (quando `_current.Kind == DeviceKind.Android`):
  - Executar `adb -s <serial> reverse tcp:<port> tcp:<port>`.
  - Abrir no telefone `http://127.0.0.1:<port>/<token>/` via `adb shell am start -a android.intent.action.VIEW -d ...` (mantendo o QR code LAN visível no PC como alternativa caso o utilizador queira usá-lo, e limpando `adb -s <serial> reverse --remove tcp:<port>` no `finally`!).
  - Se o PC nem sequer tiver Wi-Fi/LAN ligada mas o telemóvel for Android por USB, `TestServer` arranca em `127.0.0.1` sem falhar com `"Sem IP na rede local"`!

### 2. On-Device Test v3: Auricular (300 Hz) + Microfone (`getUserMedia` em Contexto Seguro)
- Em `device-test.html`:
  - **Teste de Auricular / Altifalante grave (`earpiece`):** além do tom agudo de `880 Hz` (altifalante multimédia), adicionar botão rápido de tom suave `320 Hz` a volume moderado para testar distorção/vibração de membrana, gravando `result.sensors.earpiece` (`"ok"` | `"fail"` | `"skip"`).
  - **Teste de Microfone (`mic`):** quando `window.isSecureContext && navigator.mediaDevices?.getUserMedia` for verdadeiro (automático via `http://127.0.0.1` no túnel `adb reverse`!), mostrar passo opcional `[ TESTAR MICROFONE ]` com medidor visual de pico (`AnalyserNode` WebAudio): se detetar voz/sopro acima do limiar de ruído em 5s, marca automaticamente `result.sensors.mic = "ok"` (e liberta imediatamente `stream.getTracks().forEach(t => t.stop())`!). Em HTTP LAN não-seguro (iOS sem TLS), marcar como omitido sem erro (`"skip"`).
- Em `MainWindow.ApplyTestResult`, `ReportBuilder.FriendlyLabel` e `Grader.cs`:
  - Mapear `sensor.mic` e `sensor.earpiece` (`fail` → `Cap(2, ...)` + peça `"microfone"` / `"auricular"`).

### 3. Presets de Impressora Térmica em `LabelPrinter.cs`
- Ler `dlg.PrintableAreaWidth` e `dlg.PrintableAreaHeight` após `dlg.ShowDialog() == true`:
  - Se a impressora selecionada for um rolo térmico compacto (ex.: menor dimensão $\le 320$ DIPs $\approx 85\text{ mm}$, como Brother 62mm, Dymo 57x32mm ou Zebra 50x30mm), centrar/escalar automaticamente a etiqueta para preencher a área útil do rolo (rodando 90° se `PrintableAreaWidth < PrintableAreaHeight` e a etiqueta for horizontal!).
  - Em impressoras A4 normais, manter o tamanho físico exacto de `70 × 40 mm` com margem limpa.

### 4. Retenção Temporal RGPD no `ScanHistory` + Opções (`DiagConfig` / `SettingsDialog`)
- Em `DiagConfig`: adicionar `public int HistoryRetentionDays { get; set; } = 90;` (`0` = nunca expirar).
- Em `ScanHistory.Append` / `Prune`: filtrar também entradas cujo `CollectedAt < DateTimeOffset.UtcNow.AddDays(-retentionDays)` quando `retentionDays > 0`, e adicionar método `public void ClearAll()` (apaga `history.jsonl` sob `WriteLock`).
- Em `SettingsDialog`: adicionar seletor simples de retenção do histórico (`30 dias`, `90 dias`, `Sem limite`) + botão `[ LIMPAR HISTÓRICO ]` com confirmação (traduzidos em PT e EN em `L10n.cs`).

---

## Critérios de Aceitação
- [ ] Num Android ligado por USB a um PC **sem Wi-Fi/LAN**, `[ TESTAR ECRÃ/TOQUE ]` funciona 100% via `adb reverse` (`127.0.0.1`) e testa também o microfone.
- [ ] `adb reverse --remove` é sempre executado ao terminar ou cancelar o teste.
- [ ] `ScanHistory` expira registos antigos segundo `HistoryRetentionDays` (coberto por teste unitário em `ScanHistoryTests`).
- [ ] `LabelPrinter` adapta-se tanto a rolos térmicos estreitos como a folhas A4.
