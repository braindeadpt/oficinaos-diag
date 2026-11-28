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
2. Quando o job Windows terminar **com falha**, descarrega o artifact
   `ui-shots`. Atenção às duas situações em que ele existe — desde que
   os artifacts passaram a publicar-se só em falha ou em execução
   manual (retenção curta, quota partilhada da conta):

   - **falha na comparação de baselines**: é o caso normal deste fluxo —
     o harness correu, há PNGs, e o artifact tem as capturas para copiar;
   - **run manual** (`workflow_dispatch`): publica mesmo com tudo verde —
     serve para pedir capturas de referência sem provocar falha nenhuma.

   **Confirma no log que a falha foi mesmo na comparação** antes de
   substituir baselines: os artifacts publicam-se no fim do job, por isso
   uma falha posterior às capturas (testes jsdom, por exemplo) também
   publica `ui-shots` — com a comparação verde. Copiar essas capturas não
   estraga nada (são iguais aos baselines), mas é trabalho perdido e
   esconde a falha verdadeira, que está noutro passo.

   Numa PR **verde** não há artifact nenhum — e também não precisas dele,
   porque os baselines já batem certo. O run ID está no link do check
   `build (windows-2022)` em `gh pr checks <PR>` — **não filtres só por
   branch**: o workflow também corre em `schedule` e `workflow_dispatch`,
   e um run manual na mesma branch não é o da PR.

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
  nada (e num run automático verde nem há artifact publicado).
- **`tools/atualizar-baselines-ui.ps1` NUNCA é o caminho para atualizar
  baselines**: corre o harness local (que produz renders diferentes do
  CI em Win11) e, se correr sem falhas, **apaga os baselines
  versionados e substitui-os pelos renders locais** — exatamente o que
  não queremos commitar.
- Para **iterar** no harness sem tocar nos baselines, corre-o
  diretamente para uma pasta de saída separada (o `--baselines`
  compara mas não escreve nada lá). **Limpa a pasta de saída antes de
  cada corrida** — capturas de runs anteriores ficam lá e podem
  mascarar uma captura que já não é produzida:

  ```powershell
  Remove-Item out\ui-shots-local\*.png -ErrorAction SilentlyContinue
  dotnet run --project tools/UiShots/UiShots.csproj -c Release -- out\ui-shots-local --baselines tools/UiShots/baselines
  ```

- Se já executaste o script por engano, `git checkout --` **não chega**:
  ele restaura os PNGs rastreados mas não apaga os PNGs novos que o
  script copiou (untracked) nem desfaz o que já foi `git add`. Como o
  script **só toca em `*.png`**, o restauro seguro também é — repor a
  pasta inteira apagaria trabalho local que o script não alterou
  (README, PNGs novos por commitar):

  ```powershell
  git status -- tools/UiShots/baselines/   # inspeção primeiro
  # repõe SÓ os PNGs rastreados (README e outros ficheiros ficam intactos)
  git restore --source=HEAD --staged --worktree -- ":(glob)tools/UiShots/baselines/*.png"
  # remove SÓ os PNGs não rastreados que o script copiou
  git clean -f -- ":(glob)tools/UiShots/baselines/*.png"
  ```

  Na dúvida, o caminho mais seguro é clonar o repo de novo — é só uma
  pasta de baselines.

### Flakiness conhecida do harness

Os testes de teclado do combo (`combo-terminal-aberto`) e os avisos de
"falha repetida" dependem de foco de janela real — podem falhar de forma
intermitente em runs concorrentes. Uma falha isolada desses não é
regressão: corre outra vez antes de investigar código.
