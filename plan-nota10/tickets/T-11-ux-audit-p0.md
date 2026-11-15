# T-11 — Auditoria UX/UI P0: foco visível, temas escuros coerentes e ações que nunca saem do ecrã

- **Fase:** 5 (auditoria UX/UI, pedida depois do plano original)
- **Bugs Resolvidos:** nenhum `B-xx` — são achados de usabilidade, não os 18 bugs catalogados
- **Dependências:** `T-06` (temas e diálogos), `T-08` (CI, testes de XAML e `L10n`)
- **Ficheiros em Escopo:**
  - `src/OficinaDiag/Themes/Terminal.xaml`, `Themes/Win95.xaml`, `Themes/Fluent.xaml`
  - `src/OficinaDiag/MainWindow.xaml` / `.xaml.cs`, `BenchWindow.xaml.cs`
  - `src/OficinaDiag/ChecklistDialog.xaml` / `.xaml.cs`, `InsuranceDialog.xaml` / `.xaml.cs`, `SettingsDialog.xaml` / `.xaml.cs`, `TokenDialog.xaml.cs`
  - `src/OficinaDiag/ThemeManager.cs`, `src/OficinaDiag.Core/L10n.cs`
  - `tools/UiShots/` (ferramenta de capturas) e `.github/workflows/ci.yml`
  - `tests/OficinaDiag.Tests/L10nTests.cs`

## Contexto

O plano Nota 10 fechou bugs funcionais. Falta a camada que o utilizador vê: foco de teclado invisível, controlos com o aspeto do sistema dentro de temas desenhados à mão, texto cortado, ações que saem do ecrã numa janela pequena e avisos ausentes antes de perder trabalho. Nada disto se apanha a ler código com olhos de compilador — precisa da UI a correr. Como não há Windows nesta máquina, a prova vem de `tools/UiShots`, que abre as janelas reais no CI (`windows-2022`), aplica cada tema e grava PNGs publicados como artefacto `ui-shots` do run.

## Decisões que ficam registadas

1. **A prova visual é artefacto do CI, não de uma máquina.** `tools/UiShots` (`net8.0-windows`, `RenderTargetBitmap`) corre no job `build (windows-2022)`, publica `ui-shots` e **falha o job** quando uma verificação falha. Um PNG que ninguém guarda não é prova.
2. **Perder trabalho pede confirmação.** Fechar a bancada a meio de uma sessão e o shutdown a meio de uma atualização perguntam antes.
3. **As ações ancoram em baixo.** Checklist, Opções, relatório de seguro e diálogos de envio/token deixam de ter os botões dentro do `ScrollViewer`: só os campos rolam. É o que garante que o botão de guardar nunca sai do ecrã.
4. **O limite é a work area, e o piso cede-lhe.** `MaxWidth`/`MaxHeight` passam a `Min(valor, work area)` e `MinWidth`/`MinHeight` a `Min(piso, limite)`. Sem isto, um `MinHeight="480"` ganha ao `MaxHeight` de uma work area de 400 e volta a empurrar as ações para fora do ecrã (visto em `checklist-*-400.png`).
5. **Temas escuros não herdam controlos do sistema.** `ScrollBar` e `ComboBox` têm template próprio em `Terminal.xaml` e `Win95.xaml`; os estilos dos itens do combo ficam **inline** no `ItemContainerStyle`, porque `L10nAndThemesTests.OsTresTemasDefinemOsMesmosXKey` exige o mesmo conjunto de `x:Key` nos três temas.
6. **Teclas a sério, condições a sério.** O `ComboBox` é dirigido com `keybd_event` (a fila de mensagens, como o teclado físico) e cada espera é `Esperar(condição, timeout)` com bombagem do dispatcher. `InputManager.ProcessInput` não entrega teclas ao `ComboBox`, e pausas fixas davam um teste intermitente em runner carregado.

## Critérios de Aceitação

- [x] A janela principal abre nos três temas, a 1000×740 e no mínimo 700×480, sem exceção e sem conteúdo fora do cliente. *(prova: artefacto `ui-shots` do run `43747332095` — `main-*.png` e `main-*-min.png`; no log nenhuma linha `FORA DA JANELA`)*
- [x] O foco de teclado vê-se nos três temas, sem depender do `FocusVisualStyle` do sistema. *(prova: `main-*.png` do mesmo run com o botão focado em vídeo inverso e caixa dupla, e `foco: sim` no log)*
- [x] O `ComboBox` dos temas escuros abre e navega por teclado: F4 ou Alt+Down abrem, Down+Enter seleciona o item seguinte, Esc fecha sem mudar a seleção. *(prova: 9 verificações por tema em `combo por teclado (terminal|win95)` no run `43747332095`, e `combo-terminal-aberto.png`/`combo-win95-aberto.png`)*
- [x] O checklist mantém `APLICAR`/`cancelar`/`ANEXAR FOTOS` inteiros a 620, 480, 614 e 400 DIP de work area. *(prova: `checklist-*-default/baixa/614/400.png` e as asserções `ApplyBtn`/`CancelBtn`/`PhotoBtn inteiro` do run `43747332095`)*
- [x] O relatório de seguro e as Opções mantêm as ações ancoradas e inteiras a 614 e 400 DIP, mesmo com os campos a rolar. *(prova: `seguradora-614.png`, `seguradora-400.png`, `opcoes-*-614.png`, `opcoes-*-400.png`, `opcoes-*-614-curta.png` — esta última com o scroll forçado a 200 DIP)*
- [x] Trocar o tema com as janelas abertas repinta o fundo, a `ResultsList` (cor **e** formato) e os cinco diálogos sem os reabrir. *(prova: linhas `troca:` do run `43747332095` — `#FF050A05 → #FFC0C0C0`, texto `#FF33FF33 → #FF008000`, formato `'[pass]' → 'pass'` e cinco `repinta aberto`; imagens `troca-terminal.png`, `troca-win95.png`, `troca-opcoes-fluent.png`)*
- [x] Os cinco diálogos abrem nos três temas sem rebentar no `InitializeComponent`. *(prova: `checklist-*`, `opcoes-*`, `seguradora-*`, `envio-*` e `token-*` em terminal, win95 e fluent, sem nenhuma linha `FALHOU` no log do run `43747332095`)*
- [x] Todas as chaves de `L10n` usadas na UI existem em PT e EN. *(prova: `L10nTests`, que lê os `L10n.T/F/Btn/Section` do projeto WPF e exige as chaves nos dois idiomas; mutação confirmada — acrescentar `chk.ok` só a `pt` dá `pt:chk.ok, en:chk.ok`)*
- [ ] Os avisos de perda de trabalho (fechar a bancada a meio de uma sessão, shutdown a meio de uma atualização) vistos a correr. *(pendente de interação: o `MessageBox` nativo não é capturável pelo `RenderTargetBitmap` e o `UiShots` não clica em `X` nem em `FECHAR`; a correção existe e foi revista por código)*
- [ ] Scrollbar do tema Win95 com setas visíveis e zonas de página distintas do track. *(pendente de decisão de design: as zonas de página funcionam mas pintam com o mesmo brush do track e não há setas; cosmético, ~20 linhas de template)*
