# T-15 — Os botões da barra de acções do `MainWindow` com rede

- **Fase:** 5 (cobertura de UI, pedida depois do `T-14`)
- **Bugs Resolvidos:** nenhum `B-xx` — é buraco de cobertura, não defeito
- **Dependências:** `T-13` (rede dos três caminhos), `T-11` (diálogos e avisos)
- **Ficheiros em Escopo:**
  - `tools/UiShots/Program.cs` (`ProvarBotoes`, `ComHistorico`)
  - `src/OficinaDiag/MainWindow.xaml.cs` (construtor com `ScanHistory`)
  - `tools/mutation-check.mjs`

## Contexto

O `T-13` pôs uma rede por baixo dos cliques: uma exceção engolida aparece como `! UI:` e reprova o run. Isso apanha «rebentou», não apanha «não fez nada» — e os botões da barra de acções da janela principal eram o resto sem prova nenhuma. Os diálogos já tinham `ProvarCliques` (cliques que não gravam) e `ProvarCommits` (cliques que gravam com dados válidos). Falta a barra: SCAN, SCAN+ (BANCADA), COMPARAR e ETIQUETA.

## Decisões que ficam registadas

1. **Prova-se o que a rede pode provar.** Os botões que abrem diálogo nativo (`PrintDialog`, `SaveFileDialog`) ou vão à rede (`_cloud`) ficam de fora — o clique provaria o harness, não a app. O que se prova é o que corre com os dados: as guardas de SCAN/BANCADA sem aparelho e o diff do COMPARAR.
2. **O histórico entra por parâmetro.** Para o COMPARAR abrir com um scan anterior era preciso um histórico com dados. Escrevê-lo no `ScanHistory` por omissão gravaria no histórico de quem corre o harness — e a poda por retenção podia apagar scans reais. `MainWindow(ScanHistory?)` (interno) resolve: a app usa o construtor sem argumentos, o harness passa um ficheiro dentro de `out-store/`.
3. **Comparar é comparar dados.** A janela abre modal dentro do clique; o harness lê e fecha-a por um `DispatcherTimer` e verifica o diff pelas linhas reais (`battery.health` `78% [warn] → 94% [pass]`, `screen.deadpixels` com `—` antes): seis chaves, sem as `grade.*`.
4. **As duas janelas de COMPARAR fecham-se na mesma função.** A primeira sonda (`previous.Count == 0` → `< 0`) pendurou o harness 180 s: sem o aviso, a janela abria e ninguém a fechava, porque só o caso «com anterior» tinha relógio. `Comparar(janela)` lê e fecha qualquer janela que apareça, inclusive quando o esperado é não abrir — a regressão reprova na verificação certa em segundos.
5. **A etiqueta prova-se sem impressora.** `LabelPrinter.BuildVisual` constrói o que a etiqueta leva (modelo, serial, QR) sem `PrintDialog`; o clique fica fora, o conteúdo não.

## Critérios de Aceitação

- [x] SCAN sem aparelho avisa com `log.nodev`, devolve o botão ao serviço e não deixa a app ocupada. *(prova: `tools/UiShots` secção «botões do MainWindow (sem diálogo nativo nem rede)», run `43762481073` — `[OK] botões: SCAN sem aparelho correu`, `[OK] botões: SCAN sem aparelho diz que não há aparelho`, `[OK] botões: SCAN devolve o botão ao serviço`, `[OK] botões: SCAN não deixa a app ocupada`)*
- [x] BANCADA (SCAN+) sem aparelho manda ligar por USB em vez de abrir uma sessão vazia. *(prova: mesmo run — `[OK] botões: BANCADA sem aparelho correu`, `[OK] botões: BANCADA sem aparelho manda ligar por USB`)*
- [x] COMPARAR sem serial no relatório diz o que falta (`log.cmp.noserial`). *(prova: mesmo run — `[OK] botões: COMPARAR sem serial correu`, `[OK] botões: COMPARAR sem serial diz porquê`)*
- [x] COMPARAR com serial mas sem scan anterior diz que não há nenhum (`log.cmp.none`). *(prova: run `43763227239` — `[OK] botões: COMPARAR sem scan anterior diz que não há nenhum (abriu: False)`; mutação: `if (previous.Count < 0)` no `Compare_Click` dá `[FALHA] botões: COMPARAR sem scan anterior diz que não há nenhum (abriu: True)` no run `43763440082` — 8.ª entrada do `tools/mutation-check.mjs`)*
- [x] COMPARAR com um scan anterior abre a janela com esse scan e o diff mostra o antes/depois certo. *(prova: run `43763227239` — `[OK] botões: COMPARAR abriu a janela com o scan anterior (1 scan(s))`, `[OK] botões: COMPARAR lista as seis chaves do diff (6)`, `[OK] botões: COMPARAR mostra 78% [warn] → 94% [pass]`, `[OK] botões: COMPARAR marca o que não existia antes ('—')`)*
- [x] A etiqueta leva o modelo e o serial do relatório. *(prova: run `43763227239` — `[OK] botões: a etiqueta leva o serial do relatório (4 texto(s))`, `[OK] botões: a etiqueta leva o modelo`; mutação: `var serial = "?"` no `LabelPrinter` dá `[FALHA] botões: a etiqueta leva o serial do relatório` no run `43763440082` — 9.ª entrada do `tools/mutation-check.mjs`)*

## O que fica de fora, e porquê

Nenhum destes tem prova de clique; a razão é a mesma dos três caminhos: o clique não é da app.

- **CLIENTE, EXPORTAR, IA** — abrem `SaveFileDialog` (nativo) e depois `Process.Start` no browser. O conteúdo (`ReportBuilder.ToHtml/ToJson/ToClientHtml`) é Core e tem testes próprios; o que não é provado é a escolha do ficheiro.
- **ENVIAR, LOG, IA** — vão à `_cloud` (rede) e a IA é paga; o `SendDialog` em si já é provado no `ProvarCommits`.
- **ETIQUETA (clique)** — `PrintDialog` nativo, precisa de impressora ou de um diálogo que o CI não tem; o conteúdo está provado acima.
- **TESTAR** — abre um `TestServer` (socket) e espera um `POST /result` de um telefone; sem hardware fica à espera. O fluxo via `adb reverse` do `T-10` cobre o lado do aparelho.
- **BANCADA com aparelho** — abre o `BenchWindow` modal com telemetria por adb; a janela já é provada por captura (3 temas) e pelo aviso de perda de trabalho (`T-11`).
- **SCAN com aparelho** — corre adb a sério; o relatório e o grau que saem desse scan são provados por `RelatorioAtual` + `ComRelatorio` nas capturas.
- **LIMPAR** — não existe na barra. A barra tem `LOG` (envio de log à cloud, rede) e não há botão de limpar consola.
