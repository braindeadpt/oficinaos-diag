# Prompt para agente LLM — hardware real (T-10), UI a 125/150% e os caminhos só-Windows

> Cola o bloco abaixo inteiro no agente que corre na máquina Windows com o telefone ligado.
> Repositório: `braindeadpt/oficinaos-diag`, `main` em `646f0f9` (ou mais recente).
> Actualizado a 2026-11-16: inclui o selo de integridade, a fila de envio e o contrato do `plan-check`, que ainda ninguém viu a correr no Windows.

---

Estás a trabalhar no **oficinaos-diag**, uma app WPF (Windows, `net8.0-windows`) para diagnóstico de telemóveis numa loja. O plano em `plan-nota10/` tem **87 critérios provados e 1 pendente**; a pendência é o primeiro critério do `T-10` (`plan-nota10/tickets/T-10-test-v3-adb-reverse-labels-gdpr.md`) e exige **um telefone Android físico num PC sem Wi-Fi/LAN**. Além disso há três coisas que só se vêem nesta máquina: a UI a **125% e 150%** de escala, a **fila de envio** quando a rede cai, e o **selo de integridade** de um relatório gerado a sério.

O teu trabalho é correr os testes, recolher prova e devolver o plano actualizado num PR. **Regras que não se negociam:** escreve em pt-PT; só marcas um critério `[x]` se viste o comportamento acontecer; se algo falhar, escreves exactamente o que viste em vez de arredondar; não alteras código de produção para "fazer passar" um teste (se encontrares um defeito, abres PR separado com a correção e um teste que o apanhe); não colas tokens nem chaves em lado nenhum.

## Pré-voo (Windows 10/11 x64)

```powershell
git clone https://github.com/braindeadpt/oficinaos-diag.git; cd oficinaos-diag
git log --oneline -1        # 646f0f9 ou mais recente em main
git checkout -b chore/windows-<AAAA-MM-DD>
dotnet --version            # 8.0.4xx (global.json fixa 8.0.100 + latestFeature)
pwsh -File tools/fetch-tools.ps1   # traz tools/platform-tools/adb.exe e verifica assinaturas
dotnet build OficinaDiag.sln -c Release
dotnet test tests/OficinaDiag.Tests -c Release   # esperado: 329 verdes
node tools/plan-check.mjs                        # esperado: 87 provados, 1 pendente
node tools/mutation-check.mjs --seco             # os mutantes ainda aplicam
dotnet run --project src/OficinaDiag/OficinaDiag.csproj -c Release
```

Log da app: `%APPDATA%\OficinaDiag\diag.log`. Guarda a linha `log.screen …` de cada arranque — é a medida objectiva da área útil (work area, largura, altura, left, top).

## Parte A — T-10: `adb reverse` + microfone/auricular (telefone físico)

1. Telefone Android 7+ com depuração USB ligada e autorizada. `adb devices` tem de dizer `device` (não `unauthorized`, não `offline`).
2. **Desliga o Wi-Fi e o Ethernet do PC.** O critério é literalmente "PC sem Wi-Fi/LAN" — é o ponto que o rig do CI nunca pôde provar.
3. Na app, selecciona o telefone e carrega em **TESTAR ECRÃ/TOQUE** (`TestButton`).
4. Com o teste a correr, noutra consola:
   ```powershell
   adb reverse --list        # tem de listar tcp:<porta>
   adb shell dumpsys activity activities | Select-String 127.0.0.1
   ```
5. No telefone, percorre as fases e confirma:
   - o botão **TOM GRAVE (AURICULAR)** aparece e responde (`earYes`/`earNo`);
   - o botão **TESTAR MICROFONE** só aparece em contexto seguro (`http://127.0.0.1:<porta>/`) — fala/sopra e espera o **MICROFONE OK**;
   - o resultado é entregue ("resultado entregue") e a app mostra o resultado.
6. Depois de terminar, `adb reverse --list` tem de estar **vazio** (o túnel é removido no `finally`).
7. Capturas obrigatórias: ecrã do telefone na fase do ecrã/toque, na do microfone e no resultado; a app com o resultado; `adb devices`; `adb reverse --list` durante e depois.
8. Impressão térmica: com Brother QL (62 mm), Dymo (57×32 mm) ou Zebra (50×30 mm), imprime uma etiqueta num rolo real e fotografa. Sem impressora, di-lo claramente — esse ponto fica `[ ]`.

## Parte B — UI a 100% / 125% / 150%

Para **cada** escala (Definições → Ecrã → Escala: 100%, 125%, 150%), **com a app fechada e reaberta**:

1. A janela principal abre inteira dentro da área útil: SCAN, TESTE, BANCADA, COMPARAR e ATUALIZAR visíveis, nada cortado nem fora do ecrã.
2. Copia a linha `log.screen …` do `diag.log` (é a medida que fica no relatório).
3. Abre os cinco diálogos (Opções, Checklist, Seguradora, Envio, Token): botões alcançáveis, texto não cortado, e nas Opções as barras de scroll do Win95 desenhadas com setas.
4. Vê a pré-visualização da etiqueta (70×40 mm): a proporção não muda com a escala.
5. Captura por escala: a janela principal e um diálogo.
6. Com um segundo monitor de escala diferente, arrasta a janela entre os dois e registra o que acontece. O `MainWindow` fixa `MaxWidth`/`MaxHeight` no `OnLoaded` a partir de `SystemParameters.WorkArea` (work area do monitor principal) e não trata `DpiChanged` — um monitor mais pequeno depois disso é o caso suspeito. **Não corrijas isto neste PR**: descreve o que viste e abre issue/PR separado.
7. Distingue **defeito** de **limitação**: texto borrado por virtualização de DPI não é corte de layout, e ambos têm de ser descritos como são.

## Parte C — os caminhos novos, que ainda ninguém viu no Windows

### C1. Selo de integridade do relatório (PR #13)

1. Faz um levantamento qualquer e usa **GUARDAR**, **SEGURADORA** e o relatório do **CLIENTE**. Os três ficheiros levam, antes de `</body>`, uma linha `Integridade SHA-256: <64 hexácifras>…`.
2. Confere o selo com este snippet (é a mesma conta que o código faz — remove a linha do selo, normaliza `CRLF`, calcula SHA-256):

   ```powershell
   $path = "C:\caminho\relatorio.html"
   $h = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8).TrimStart([char]0xFEFF)  # BOM, se houver
   $limpo = [regex]::Replace($h, '<p class="hash">Integridade SHA-256: [^\n]*?</p>', '')
   $norm = $limpo -replace "`r`n", "`n"
   $sha = [Security.Cryptography.SHA256]::Create()
   $calc = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($norm))) -replace '-','').ToLowerInvariant()
   $decl = [regex]::Match($h, '<code>([0-9a-f]{64})</code>').Groups[1].Value
   "declarado=$decl"; "calculado=$calc"; "confere=$($decl -eq $calc)"
   ```

3. Abre o HTML num editor, muda **uma** palavra (ex.: no dano reportado), guarda e repete o snippet: tem de dar `confere=False`. Se der `True`, o selo não cobre o que promete — é defeito, com prova e issue.

### C2. Fila de envio quando a loja está sem rede (PR #14)

1. **Sem rede** (Wi-Fi/Ethernet desligados, como na Parte A), faz um envio à loja com um código qualquer (ex.: `TESTE1`). Espera: a consola diz **«Sem rede agora — o relatório ficou em fila e sai quando a ligação voltar.»** (sem `!` à frente).
2. Confirma o ficheiro em `%APPDATA%\OficinaDiag\outbox\*.json`: tem `ShopCode`, `Corpo` (o JSON do `/intake`, com o nome/telefone/email que escreveste no diálogo) e `Chave`.
3. Liga a rede, **fecha e reabre a app**. No log: `> fila: 1 relatório(s) que esperavam rede seguiram para a loja` e o ficheiro desaparece da pasta. Se a loja não aceitar o código, o envio falha por 4xx e o ficheiro fica — nesse caso diz qual foi a resposta, não apagues nada.
4. Bónus de robustez: escreve `{` num `.json` da outbox e reabre a app — o ficheiro tem de ir para `outbox\erro\` e os outros seguir, sem travar o arranque.

### C3. Contrato do `plan-check` com o harness (PR #16)

Se mexeres em tickets: `node tools/plan-check.mjs` agora exige que quem cita `tools/UiShots` nomeie uma secção `Verificar("…")` que exista (ou uma corrida do CI), e que cada secção do harness tenha um critério `[x]` que a cite. Corre-o depois de cada edição ao plano.

## Fecho — prova e PR

- Se a Parte A passar toda, marca o primeiro critério do `T-10` como `[x]` com `*(prova: …)*` que cite as capturas (caminhos), as linhas de log e a versão do telefone (`adb shell getprop ro.build.fingerprint`). Se não passar, deixa `[ ]` e escreve o que viste e o que falta.
- Actualiza o placar pela ferramenta, nunca à mão:
  ```powershell
  node tools/plan-check.mjs --escrever
  node tools/plan-check.mjs        # tem de ficar verde
  ```
- Acrescenta um handoff novo no topo da secção «Registo de Handoff entre Sessões / Agentes» do `plan-nota10/STATUS.md`: o que se viu, o que não se viu, números e run de CI.
- Commits em pt-PT, Conventional Commits, **sem** footer de atribuição nem `Co-Authored-By`.
- Abre PR para `main` com a tabela abaixo no corpo. Defeito de layout ou de selo: PR separado com correção **e** teste que o apanhe.

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
| C1 selo confere | | | |
| C1 selo deteta edição | | | |
| C2 envio fica em fila | | | |
| C2 fila escoa no arranque | | | |
| C3 `plan-check` verde | | | |

## Não faças

- Não desligues o `plan-check`, não edites o `plan-nota10/placar.json` à mão nem apagues critérios.
- Não uses `git push --force` em `main`.
- Não marques `[x]` sem teres visto o comportamento, e não cites capturas que não existem.
- Não corrijas o DPI nem o flake neste PR — mede, descreve e abre issue.
- Não coloques tokens, chaves ou dados de clientes em texto no chat nem em commits.
