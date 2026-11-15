# T-13 — A rede que faltava: o harness reprova em qualquer «! UI:» e clica nos cinco diálogos

- **Fase:** 5 (endurecimento do harness, pedido depois do `T-12`)
- **Bugs Resolvidos:** nenhum `B-xx` — é a lacuna de prova que deixou o `T-12` passar dois lotes
- **Dependências:** `T-11` (harness de capturas), `T-12` (o checklist morto)
- **Ficheiros em Escopo:**
  - `tools/UiShots/Program.cs`
  - `src/OficinaDiag.Core/OficinaDiag.Core.csproj` (`InternalsVisibleTo` do harness)
  - `.github/workflows/ci.yml` (sem alterações — o `diag.log` viaja no artefacto `ui-shots`)

## Contexto

O `T-12` mostrou o mecanismo: um handler de `Click` rebentava, o `App` registava `! UI: …` no `diag.log` e seguia com `args.Handled = true`. A app ficava viva e errada, e o harness — que criava uma `Application` sem esse handler — nunca via a linha; quando a exceção acontecia, matava o processo. Pior: as 96 asserções não tocavam nos botões dos diálogos, por isso nem havia onde rebentar.

Duas lacunas, portanto: o harness não olhava para o sinal (`! UI:`), e não exercitava os botões que podiam produzi-lo.

## Decisões que ficam registadas

1. **O harness passa a ter o mesmo handler do `App`.** `DispatcherUnhandledException` escreve `! UI: tipo: mensagem` no log e marca `Handled = true`, como a app. O log vai para `<destino>/diag.log` (`AppLog.RedirectTo`), por isso viaja no artefacto e não suja o log da máquina.
2. **Uma linha chega para reprovar.** No fim, o harness lê o log e falha com `[FALHA] log da UI sem exceções engolidas (N linhas «! UI:»)`, imprimindo as linhas. As exceções dos handlers síncronos passam pelo `Clicar`, que escreve a mesma linha — as `async void` vão pelo handler do dispatcher.
3. **Clicar é chamar `OnClick`, não `RaiseEvent`.** `RaiseEvent(ButtonBase.ClickEvent)` não alterna o `IsChecked` de um `RadioButton` (o toggle vive no `OnClick`), e os cliques de idioma/tema das Opções não corriam. Dois vermelhos por um clique que não era clique.
4. **O `MessageBox` é achado pelo PID.** `FindWindow("#32770", null)` apanhou um diálogo **do sistema do runner** («Windows created a temporary paging file…»): o responder respondeu a esse e o `MessageBox` da app ficou aberto a bloquear a thread da UI. Passou a `EnumWindows` + `GetWindowThreadProcessId`, e o vigia de 180 s fecha o processo com a marca do último ponto — um run pendurado deixa de arder até ao limite do GitHub.
5. **Ficam de fora os botões que fecham a janela ou abrem diálogo nativo** (`APLICAR`/`cancelar`, `ANEXAR FOTOS`, `GUARDAR`): `DialogResult` só é aceite dentro de `ShowDialog` e um `OpenFileDialog` não tem quem o responda. Clicá-los provava o harness, não a app.

## Critérios de Aceitação

- [x] Uma única linha «! UI:» no log do run reprova o harness, mesmo quando nenhuma asserção a apanha. *(prova: sonda `179dc60` — exceção no dispatcher sem verificação associada — dá `! UI: InvalidOperationException: sonda de rede`, `[FALHA] log da UI sem exceções engolidas (1 linhas «! UI:»)` e `FALHAS: 1` no run `43754441587`; com a sonda revertida em `9dd4cb6`, `[OK] … (0 linhas «! UI:»)` no run `43754590504`)*
- [x] Os cinco diálogos têm botões clicados a sério e cada clique corre o handler: marcas do checklist, idioma e tema nas Opções, e o aviso de validação do seguro, do envio e do token. *(prova: 17 linhas `[OK] clique:` no run `43754227907` — «'English' muda o idioma», «'Português' volta ao PT», «muda o tema a quente», «seguro/envio/token avisou …» e «continua aberto depois do aviso»)*
- [x] O harness não pendura: o `MessageBox` certo é achado pelo PID do processo e há um vigia que fecha o run com a marca do ponto. *(prova: o run `43753596360` ficou preso no aviso da bancada — `[FALHA] harness pendurado em: aviso da bancada` ao fim de 172 s em vez do limite do GitHub — e depois da correção o mesmo passo dá `[OK] bancada: responder Sim fecha a janela` no run `43754227907`)*
