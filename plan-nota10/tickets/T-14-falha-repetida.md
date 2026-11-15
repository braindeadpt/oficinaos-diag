# T-14 — Uma ação que rebenta três vezes avisa o técnico e desliga o botão

- **Fase:** 5 (produto, pedido depois do `T-13`)
- **Bugs Resolvidos:** nenhum `B-xx` — é a consequência do mecanismo que escondeu o `T-12`
- **Dependências:** `T-12` (o checklist morto), `T-13` (a rede do harness)
- **Ficheiros em Escopo:**
  - `src/OficinaDiag.Core/Log/FailureGuard.cs`, `tests/OficinaDiag.Tests/FailureGuardTests.cs`
  - `src/OficinaDiag/MainWindow.xaml.cs`, `src/OficinaDiag/App.xaml.cs`
  - `src/OficinaDiag.Core/L10n.cs` (`log.err.disabled`, `err.repeated`, `err.repeated.title`)
  - `tools/UiShots/Program.cs`

## Contexto

O `T-12` deixou o mecanismo à vista: um handler de `Click` rebenta, o `App` escreve `! UI: …` no `diag.log`, marca `Handled = true` e segue. A app fica viva e **errada**, e o técnico vê um botão que não faz nada. O `T-13` protegeu o repositório (a rede reprova o run), mas quem está na bancada continua sem sinal nenhum — e o checklist esteve assim dois lotes.

## Decisões que ficam registadas

1. **A política vive no Core.** `FailureGuard` conta falhas seguidas por ação e devolve o que fazer: escrever no log (até 3), avisar (exatamente na 3.ª) e desligar (daí em diante). Testável sem WPF, porque o handler global não é sítio para guardar estado sem prova.
2. **O log deixa de crescer.** A mesma exceção a cada clique enchia o `diag.log` e enterrava a causa. A partir da terceira, as linhas param e fica uma linha dizendo que o botão foi desligado.
3. **Quem foi o botão.** O handler global não recebe o remetente do clique. A janela guarda a ação no *preview* do gesto (rato e teclado) e só a culpa se a falha chegar até 5 s depois — e hoje isso é provado ponta a ponta (último critério) — uma exceção vinda de um diálogo não desliga o último botão do ecrã principal.
4. **O aviso é modal e diz o que fazer.** `MessageBox` com o rótulo real do botão (não o `x:Name`), a contar que falhou três vezes, que foi desligado para não gravar dados errados, e que o `diag.log` é o sítio para ver. Desligar o botão antes de mostrar o aviso garante que fica desligado mesmo que o aviso seja fechado de imediato.
5. **Um sucesso limpa a contagem.** Um clique que corre sem rebentar repõe a ação a zero, senão três falhas espaçadas num dia desligavam o botão para sempre. Depois de desligado não há recuperação sem reiniciar a app — é deliberado: um botão que rebenta três vezes não deve voltar sozinho ao serviço.

## Critérios de Aceitação

- [x] À terceira falha seguida na mesma ação aparece um aviso com o rótulo do botão e o botão fica desativado; às duas primeiras nada acontece. *(prova: `tools/UiShots` secção «falha repetida na mesma ação» no run `43759217828` — `[OK] duas falhas ainda não desligam o botão`, `[OK] à terceira aparece o aviso`, `[OK] o aviso é o do L10n`, `[OK] o botão ficou desligado`, `[OK] a consola diz que o botão foi desligado`; mutação: comentar o `IsEnabled = false` dá `[FALHA] falha repetida: o botão ficou desligado` no run `43759396833`, e nenhum teste unitário vê essa linha)*
- [x] O log escreve as três primeiras falhas e para: a quarta não acrescenta linha nem abre outro aviso. *(prova: mesmo run — `[OK] duas falhas escrevem duas linhas (2)`, `[OK] a 3.ª falha escreve a 3.ª linha (3)`, `[OK] a 4.ª não escreve mais nada (dedupe: 3)`, `[OK] a 4.ª não abre outro aviso`; a política está em `FailureGuardTests.ApartirDaTerceiraOLogFicaEmDedupe`)*
- [x] Um clique da mesma ação que corre sem rebentar recomeça a contagem. *(prova: `FailureGuardTests.UmSucessoRecomecaAContagem` e `FailureGuardTests.AsDuasPrimeirasFalhasNaoAvisamNemDesligam`)*
- [x] A falha chega pelo caminho de produção: exceção no dispatcher → `DispatcherUnhandledException` do `App` → `RegistarFalha` → `Falhou`. *(prova: `tools/UiShots` secção «falha repetida pelo handler do App», no run `43764310199` — a janela onde o handler cai é a do próprio `App` (`Application.Current.MainWindow`), a atribuição é feita com o botão do gesto e três exceções no dispatcher dão `[OK] produção: o gesto ficou atribuído ao botão`, `[OK] produção: o handler do App avisou à terceira`, `[OK] produção: o aviso é o do L10n`, `[OK] produção: o botão da janela do App ficou desligado`, `[OK] produção: as três falhas passaram pelo log (3)`, `[OK] produção: a via «! UI:» aparece na consola da janela`; mutação: o `App` a registar sem delegar no `MainWindow` dá quatro `[FALHA] produção:` e `FALHAS: 4` no run `43764525857` — 10.ª entrada do `tools/mutation-check.mjs`)*
- [x] Só se culpa o botão quando a falha chega perto do gesto: fora da janela de 5 s fica só no log. *(prova: `FailureGuardTests.SoSeCulpaOBotaoSeAFalhaChegouPertoDoGesto` — 0 e 4999 ms culpam a ação, 5001 ms e ação vazia devolvem `null`)*
