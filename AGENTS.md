# Agent notes — OficinaOS Diag

## Baselines UiShots: gerar no CI, nunca localmente

`tools/UiShots/baselines/*.png` são capturas pixel-exatas da UI WPF.
**Não os regeneres na tua máquina Windows** — os resultados não batem
certo com o runner `windows-2022` do CI:

- Máquinas Win11 locais têm **Segoe UI Variable Text**; o Server 2022 do
  runner não. O Fluent theme declara
  `Segoe UI Variable Text, Segoe UI` — a métrica do texto muda, o layout
  das janelas muda (altura/largura) e o baseline falha sempre com
  "mesmas dimensões" ou "% de pixéis diferentes".

### Fluxo correto quando a UI muda intencionalmente

1. Faz push do código — o job Windows do CI falha nos baselines mas
   **publica o artifact `ui-shots`** com as capturas renderizadas no
   próprio runner (a referência correta).
2. Descarrega o artifact do run falhado:

   ```powershell
   gh run download <run-id> -R braindeadpt/oficinaos-diag -n ui-shots -D out\uishots-ci
   ```

3. Substitui `tools/UiShots/baselines/*.png` pelas capturas do artifact
   (ignora `diff-*`). Confirma que são 60 PNGs.
4. Commit + push — o CI volta a passar porque baseline e render são o
   mesmo ambiente.

`tools/atualizar-baselines-ui.ps1` continua útil para **iterar no
harness** (ver as capturas localmente), mas os PNGs que produz não servem
como baseline de CI numa máquina Win11. Se um dia o harness correr no
próprio CI e escrever os baselines, este workaround deixa de ser
preciso.

### Flakiness conhecida do harness

Os testes de teclado do combo (`combo-terminal-aberto`) e os avisos de
"falha repetida" dependem de foco de janela real — podem falhar de forma
intermitente em runs concorrentes. Uma falha isolada desses não é
regressão: corre outra vez antes de investigar código.
