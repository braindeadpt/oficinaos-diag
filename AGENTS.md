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

1. Abre a PR **antes** de esperar pelo CI — o workflow só corre em
   `pull_request`, por isso um push para uma branch sem PR não gera
   capturas nenhumas.
2. Quando o job Windows terminar (pass ou fail — o artifact
   `ui-shots` é publicado com `if: always()`), descarrega-o. O run ID
   está no link do check `build (windows-2022)` em `gh pr checks <PR>`
   — **não filtres só por branch**: o workflow também corre em
   `schedule` e `workflow_dispatch`, e um run manual na mesma branch
   não é o da PR.

   ```powershell
   gh run download <run-id> -R braindeadpt/oficinaos-diag -n ui-shots -D out\uishots-ci
   ```

   **Atenção**: nem todo o run vermelho tem capturas. O passo `Test`
   (`dotnet test`) corre **antes** do UiShots — se ele falhar, o
   harness nunca gera PNGs e o artifact sai vazio ou nem existe.
   Só há capturas para copiar quando a falha é na **comparação de
   baselines** (o harness chegou a correr). Se o artifact estiver
   vazio, corrige primeiro a falha de testes.
3. Substitui `tools/UiShots/baselines/*.png` pelas capturas do artifact
   (ignora `diff-*`). O conjunto de nomes tem de bater com o que o
   harness produz — **não assumas um número fixo**: se adicionaste ou
   removeste capturas no código, há baselines a mais ou a menos de
   propósito; apaga os órfãos e garante que cada captura nova tem o
   seu baseline.
4. Commit + push — o CI volta a passar porque baseline e render são o
   mesmo ambiente.

Notas:

- Se o CI passa, os baselines já batem certo — não precisas de fazer
  nada (o artifact existe na mesma, mas não há o que atualizar).
- **`tools/atualizar-baselines-ui.ps1` NUNCA é o caminho para atualizar
  baselines**: corre o harness local (que produz renders diferentes do
  CI em Win11) e, se correr sem falhas, **apaga os baselines
  versionados e substitui-os pelos renders locais** — exatamente o que
  não queremos commitar.
- Para **iterar** no harness sem tocar nos baselines, corre-o
  diretamente para uma pasta de saída separada (o `--baselines`
  compara mas não escreve nada lá):

  ```powershell
  dotnet run --project tools/UiShots/UiShots.csproj -c Release -- out\ui-shots-local --baselines tools/UiShots/baselines
  ```

- Se já executaste o script por engano, `git checkout --` **não chega**:
  ele restaura os PNGs rastreados mas não apaga os PNGs novos que o
  script copiou (untracked) nem desfaz o que já foi `git add`. O
  restauro completo é:

  ```powershell
  git restore --staged --worktree tools/UiShots/baselines/
  git clean -fd tools/UiShots/baselines/
  ```

### Flakiness conhecida do harness

Os testes de teclado do combo (`combo-terminal-aberto`) e os avisos de
"falha repetida" dependem de foco de janela real — podem falhar de forma
intermitente em runs concorrentes. Uma falha isolada desses não é
regressão: corre outra vez antes de investigar código.
