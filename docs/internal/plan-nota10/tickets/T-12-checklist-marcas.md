# T-12 — O checklist voltou a marcar itens em relatórios novos

- **Fase:** 5 (defeito encontrado na verificação visual externa de `71a3206`, depois do `T-11`)
- **Bugs Resolvidos:** nenhum `B-xx` — regressão do lote de temas (`704ff15`), fora dos 18 catalogados
- **Dependências:** `T-06` (checklist e temas), `T-08` (suite de testes e CI), `T-11` (harness de capturas)
- **Ficheiros em Escopo:**
  - `src/OficinaDiag/ChecklistDialog.xaml.cs`
  - `src/OficinaDiag.Core/Grading/ChecklistMarks.cs`
  - `tests/OficinaDiag.Tests/ChecklistMarksTests.cs`
  - `tools/UiShots/Program.cs`

## Contexto

A verificação visual de `71a3206` (fora do CI, com a app a correr) clicou nos botões `ok` · `defeito` · `n/t` do checklist e apanhou o que as 96 asserções do harness não apanhavam, porque o harness nunca lhes tocava: **a marca não pegava nenhuma vez**.

`SetState` fazia `_state[key] = _state[key] == s ? 0 : s;` — indexer direto. Num relatório acabado de recolher `_state` está vazio, logo o primeiro clique lançava `KeyNotFoundException: The given key 'checklist.frame' was not present in the dictionary` dentro do handler de `Click`. A exceção era engolida pelo handler global da UI (ficava só a linha `! UI:` no log), o `Paint` nunca corria e o `APLICAR` iterava um `_state` vazio: **zero linhas no relatório**. `Paint` e `Apply_Click` já usavam `GetValueOrDefault`; `SetState` era o único com o indexer. Passava despercebido porque reabrir um checklist com marcas anteriores funciona — o dicionário já tem as chaves.

## Decisões que ficam registadas

1. **A regra sai do diálogo.** `ChecklistMarks` (`Toggle`, `Status`, `Apply`) vive no Core e é testada sem WPF. Enquanto a regra estava num handler de `Click`, o único caminho para a exercitar era abrir uma janela — e foi por isso que 96 asserções de UI não a viram rebentar.
2. **`Apply` também sai.** A escrita no relatório (o sintoma «zero linhas») passa a `ChecklistMarks.Apply`, chamada pelo `Apply_Click`: um teste prova que duas marcas dão `pass` e `skipped` e que os itens por olhar não entram.
3. **A prova é uma mutação, não uma leitura.** Reintroduzir o indexer em `Toggle` faz três testes falharem com a mensagem exata do relatório.
4. **O harness aprende a clicar.** `ProvarCliques` abre o diálogo com os botões reais do `ItemsPanel` e exige que a marca pegue (peso do botão), que premir de novo desmarque e que marcar outro item não desmarque o primeiro. Fica com `Show()` e não `ShowDialog()`: o `DialogResult` do `APLICAR` só é aceite dentro do `ShowDialog` e a interação modal deixava o run frágil — a escrita no relatório é provada no Core.
5. **Clicar no `ItemsPanel`, não na janela.** As ações (`GUARDAR`/`CANCELAR`) são declaradas antes do painel, por isso são os primeiros botões da árvore; um `Descendentes<Button>(janela).First()` apanhava o `GUARDAR` e o `DialogResult` rebentava fora do `ShowDialog` (dois runs vermelhos antes de dar nisto).

## Critérios de Aceitação

- [x] Clicar `ok`/`defeito`/`n/t` num item sem marca anterior marca o item, em vez de rebentar com `KeyNotFoundException`. *(prova: `ChecklistMarksTests.MarcarItemSemMarcaNaoRebenta` e, na UI, `[OK] clique: checklist 'ok' marca o item` na secção «cliques nos botões dos cinco diálogos» do run `43760596047`; mutação confirmada — o indexer antigo dá `KeyNotFoundException: The given key 'checklist.frame' was not present in the dictionary` em três testes)*
- [x] Premir o mesmo botão desmarca, premir outro troca a marca e marcar um item não mexe nos outros. *(prova: `ChecklistMarksTests.PremirOMesmoBotaoOutraVezDesmarca`, `PremirOutroBotaoTrocaAMarca` e `ItensDiferentesNaoSeAtrapalham`; na UI `[OK] checklist: premir outra vez desmarca` e `[OK] checklist: marcar outro item não desmarca o primeiro`)*
- [x] `APLICAR` grava uma linha `checklist.*` por item marcado — `pass` para `ok`, `fail` para `defeito`, `skipped` para `n/t` — e nenhuma para os que ficaram por olhar. *(prova: `ChecklistMarksTests.AplicarEscreveAsLinhasMarcadasNoRelatorio` e `SemMarcasNaoEscreveNada`, sobre `ChecklistMarks.Apply`, que é o que o `Apply_Click` chama)*
