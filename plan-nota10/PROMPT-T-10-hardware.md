# Prompt para agente LLM — T-10 em hardware real + UI a 125/150%

> Cola o bloco abaixo inteiro no agente que vai correr na máquina Windows com o telefone ligado.
> O repositório está em `braindeadpt/oficinaos-diag`, `main` em `330d224`.

---

Estás a trabalhar no **oficinaos-diag**, uma app WPF (Windows, `net8.0-windows`) para diagnóstico de telemóveis numa loja. O plano em `plan-nota10/` tem **87 critérios provados e 1 pendente**; a pendência é o primeiro critério do `T-10` (`plan-nota10/tickets/T-10-test-v3-adb-reverse-labels-gdpr.md`), que exige **um telefone Android físico e um PC sem Wi-Fi/LAN**. Além disso tens de avaliar a UI a **125% e 150%** de escala, que nunca foi vista fora de 100%.

O teu trabalho é correr os testes, recolher prova e devolver o plano atualizado num PR. **Regras que não se negociam:** escreve em pt-PT; só marcas um critério `[x]` se viste o comportamento acontecer; se algo falhar, escreves exactamente o que viste em vez de arredondar; não alteras código de produção para "fazer passar" um teste (se encontrares um defeito, abres PR separado com a correção e um teste que o apanhe); não colas tokens nem chaves em lado nenhum.

## Pré-voo (Windows 10/11 x64)

```powershell
git clone https://github.com/braindeadpt/oficinaos-diag.git; cd oficinaos-diag
git log --oneline -1        # confirma 330d224 ou mais recente em main
git checkout -b chore/t10-hardware-<AAAA-MM-DD>
dotnet --version            # tem de ser 8.0.4xx (global.json fixa 8.0.100 + latestFeature)
pwsh -File tools/fetch-tools.ps1   # traz tools/platform-tools/adb.exe
dotnet build OficinaDiag.sln -c Release
dotnet test tests/OficinaDiag.Tests -c Release   # esperado: 313 verdes
node tools/plan-check.mjs                        # esperado: 87 provados, 1 pendente
dotnet run --project src/OficinaDiag/OficinaDiag.csproj -c Release
```

O log da app é `%APPDATA%\OficinaDiag\diag.log`. Guarda a linha `log.screen …` do arranque — é a tua medida objectiva da área útil (work area, largura, altura, left, top) em cada escala.

## Parte A — T-10: `adb reverse` + microfone/auricular (telefone físico)

1. Telefone Android 7+ com depuração USB ligada e autorizada. `adb devices` tem de dizer `device` (não `unauthorized`, não `offline`).
2. **Desliga o Wi-Fi e o Ethernet do PC.** O critério é literalmente "PC sem Wi-Fi/LAN" — este é o ponto que o rig Android do CI nunca pôde provar.
3. Na app, selecciona o telefone e carrega em **TESTAR ECRÃ/TOQUE** (botão `TestButton`).
4. Com o teste a correr, noutra consola:
   ```powershell
   adb reverse --list        # tem de listar tcp:<porta>
   adb shell dumpsys activity activities | Select-String 127.0.0.1
   ```
5. No telefone, percorre as fases. Confirma especificamente:
   - o botão **TOM GRAVE (AURICULAR)** aparece e responde (`earYes`/`earNo`);
   - o botão **TESTAR MICROFONE** só aparece em contexto seguro (`http://127.0.0.1:<porta>/`) — fala/sopra e espera o **MICROFONE OK**;
   - o resultado é entregue ("resultado entregue") e a app mostra o resultado do scan.
6. Depois de terminar: `adb reverse --list` tem de estar **vazio** (o túnel é removido no `finally`).
7. Capturas obrigatórias: ecrã do telefone na fase do ecrã/toque, na fase do microfone e no resultado; a app com o resultado; `adb devices`; `adb reverse --list` durante e depois.
8. Impressão térmica: se tiveres Brother QL (62 mm), Dymo (57×32 mm) ou Zebra (50×30 mm), imprime uma etiqueta num rolo real e fotografa. Se não houver impressora, di-lo claramente — esse ponto do ticket fica `[ ]`.

## Parte B — UI a 100% / 125% / 150%

Para **cada** escala (Definições → Ecrã → Escala: 100%, 125%, 150%), **com a app fechada e reaberta**:

1. A janela principal abre inteira dentro da área útil: SCAN, TESTE, BANCADA, COMPARAR e ATUALIZAR visíveis, sem nada cortado nem fora do ecrã.
2. Copia a linha `log.screen …` do `diag.log` (é a medida que fica no relatório).
3. Abre os cinco diálogos (Opções, Checklist, Seguradora, Envio, Token): botões alcançáveis, texto não cortado, e nas Opções as barras de scroll do Win95 desenhadas com setas.
4. Vê a pré-visualização da etiqueta (70×40 mm): a proporção não pode mudar com a escala.
5. Captura por escala: a janela principal e um diálogo.
6. Se tiveres um segundo monitor com escala diferente, arrasta a janela entre os dois e registra o que acontece. O `MainWindow` fixa `MaxWidth`/`MaxHeight` no `OnLoaded` com `SystemParameters.WorkArea` — um monitor mais pequeno depois disso é o caso suspeito.
7. Distingue **defeito** de **limitação**: texto borrado por virtualização de DPI não é corte de layout, e ambos têm de ser descritos como são.

## Fecho — prova e PR

- Se a Parte A passar toda, marca o primeiro critério do `T-10` como `[x]` com `*(prova: …)*` que cite as capturas (caminhos), as linhas de log e a versão/hash do telefone (`adb shell getprop ro.build.fingerprint`).
- Se não passar, deixa `[ ]` e escreve o que viste e o que falta — a frase já existente no ticket tem de continuar verdadeira.
- Atualiza o placar pela ferramenta, nunca à mão:
  ```powershell
  node tools/plan-check.mjs --escrever
  node tools/plan-check.mjs        # tem de ficar verde
  ```
- Acrescenta um handoff novo no topo da secção «Registo de Handoff entre Sessões / Agentes» do `plan-nota10/STATUS.md`, com datas, o que se viu, o que não se viu e os números.
- Commits em pt-PT, Conventional Commits, **sem** footer de atribuição nem `Co-Authored-By`.
- Abre PR para `main` com a tabela abaixo no corpo. Se a Parte B revelar um defeito de layout, abre PR separado com a correção e um teste.

## Tabela a devolver

| Passo | Resultado (ok/falhou/não testado) | Prova (ficheiro, linha de log, hash) | Em falta |
|---|---|---|---|
| A2 PC sem rede | | | |
| A3 túnel `adb reverse` | | | |
| A5 tom grave | | | |
| A5 microfone | | | |
| A6 túnel removido | | | |
| A8 etiqueta impressa | | | |
| B1 100% | | | |
| B1 125% | | | |
| B1 150% | | | |

## Não faças

- Não desligues o `plan-check`, não edites o `plan-nota10/placar.json` à mão nem apagues critérios.
- Não uses `git push --force` em `main`.
- Não marques `[x]` sem teres visto o comportamento, e não cites capturas que não existem.
- Não coloques tokens, chaves ou dados de clientes em texto no chat nem em commits.
