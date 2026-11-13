# T-04 — Sanitização XSS/HTML em `ReportBuilder`, Timeouts por Endpoint em `CloudClient` e Robustez `ScanHistory`/`AppLog`

- **Fase:** 2 (Hardening, Rede & Persistência Local)
- **Bugs Resolvidos:** `B-08`, `B-12c` (sanitização de nomes de ficheiro), `B-17`, `B-18`
- **Dependências:** Nenhuma
- **Ficheiros em Escopo:**
  - `src/OficinaDiag/Reports/ReportBuilder.cs`
  - `src/OficinaDiag/Cloud/CloudClient.cs`
  - `src/OficinaDiag/History/ScanHistory.cs`
  - `src/OficinaDiag/Log/AppLog.cs`
  - `tests/OficinaDiag.Tests/ReportBuilderTests.cs`
  - `tests/OficinaDiag.Tests/ScanHistoryTests.cs`
- **Fora de Escopo:** Tradução das strings de `CloudClient` para Inglês fica em `T-06` (ou pode preparar já chaves se `T-06` correr depois).

---

## Contexto do Problema

1. **Links `javascript:` em Relatórios IA e HTML Injection em `docRef` (`B-08`):**
   - `ReportBuilder.cs:23-24` configura `Markdig` com `.DisableHtml()`, mas permite links Markdown `[Ver](javascript:alert(1))` ou `data:text/html,...`. Como o LLM recebe dados não confiáveis do aparelho (nomes Wi-Fi, logs de crash), um prompt injection pode gerar um link `javascript:` num ficheiro `.html` que a app abre automaticamente no browser local.
   - `ReportBuilder.cs:408, 442` (`ToInsuranceHtml`) interpola `docRef` (derivado de `Device.Serial`) sem `e(docRef)`. Nas linhas `105` (`ToHtml`) e `316` (`ToClientHtml`), `c.Status` e `grade.Value` entram em `class="..."` sem sanitização alfanumérica.
2. **Timeout único de 150s + Retry em `CloudClient` (`B-17`):**
   - `CloudClient.cs:17, 40-59` usa `Timeout = 150s` com 1 retry em `TaskCanceledException`, fazendo o utilizador esperar **5 minutos (301.5s)** em `SendToShopAsync`, `RedeemPairingAsync` ou `SendLogAsync` se o servidor/rede pendurar. Além disso, os `HttpResponseMessage` devolvidos por `SendWithRetryAsync` não são libertados com `using`.
3. **Mutex infinito em `ScanHistory` e corte UTF-8 em `AppLog` (`B-18`):**
   - `ScanHistory.cs:30` chama `WriteLock.WaitOne()` sem timeout, podendo congelar a UI se outra instância travar.
   - `AppLog.cs:38-50` faz `fs.Seek(-keep, SeekOrigin.End)` (`linha 44`) num ficheiro UTF-8, podendo cortar uma sequência multi-byte (`ã`, `ç`, `—`) a meio na primeira linha retida.

---

## Especificação de Implementação

### 1. Hardening Total em `ReportBuilder.cs` + Helper de Nome de Ficheiro Seguro
- Em `AiMarkdownPipeline` (`ReportBuilder.cs:23-24`), encadear `.UseUrlRewriter(link => ...)` (ou pós-processar `MarkdownDocument` / `LinkInline`) para permitir **apenas** URLs que comecem por `https://`, `http://` ou `mailto:` (qualquer outro esquema como `javascript:`, `vbscript:`, `data:`, `file:` é reescrito para `"#"`).
- Em `ToInsuranceHtml:442`, usar `e(docRef)`.
- Em `ToHtml:105` e `ToClientHtml:316`, criar helper `SafeCssToken(string? s)` que aceita apenas `[a-z0-9-]` (`"pass"`, `"warn"`, `"fail"`, `"info"`, `"a"`, `"b"`, `"c"`, `"d"`), caindo para `"info"` / `"d"` caso contrário.
- Adicionar helper `public static string SafeFileToken(string? raw, string fallback = "device")` em `ReportBuilder` que substitui todos os caracteres de `Path.GetInvalidFileNameChars()` (incluindo `:`, `/`, `\`, `!`, `*`, `?`) por `'_'`, para ser usado em `MainWindow` ao exportar ficheiros (`B-12c`).

### 2. Timeouts Diferenciados e `using` em `CloudClient.cs`
- Alterar `SendWithRetryAsync(Func<HttpRequestMessage> build, TimeSpan attemptTimeout)`:
  - Em cada tentativa criar `using var cts = new CancellationTokenSource(attemptTimeout);` e chamar `await Http.SendAsync(req, cts.Token)`.
  - Passar `TimeSpan.FromSeconds(20)` em `SendToShopAsync`, `SendLogAsync` e `RedeemPairingAsync`, e `TimeSpan.FromSeconds(150)` (sem retry automático em timeout de 150s, apenas retry em erro rápido de ligação/5xx!) em `GenerateAiReportAsync`.
  - Garantir `using var res = await SendWithRetryAsync(...);` nos 4 métodos públicos.

### 3. Robustez em `ScanHistory.cs` e `AppLog.cs`
- Em `ScanHistory.Append`: usar `var locked = false; try { locked = WriteLock.WaitOne(TimeSpan.FromSeconds(5)); if (!locked) return; ... } catch (AbandonedMutexException) { locked = true; ... } finally { if (locked) WriteLock.ReleaseMutex(); }` (tratar `AbandonedMutexException` evita crash se um processo anterior morreu com o mutex!).
- Em `AppLog.TrimIfNeeded`: após ler `buf`, encontrar o primeiro `'\n'` (`var nl = Array.IndexOf(buf, (byte)'\n');`) e escrever a partir de `nl + 1` (se `nl >= 0 && nl + 1 < n`), garantindo início de linha limpo e UTF-8 100% válido.

### 4. Testes Unitários (`ReportBuilderTests.cs`)
- Testar que `[clica](javascript:alert(1))` e `[x]( JaVaScRiPt:void(0) )` em `AiReportToHtml` não produzem `href="javascript:..."`.
- Testar que `Serial = "<img src=x onerror=alert(1)>"` em `ToInsuranceHtml`, `ToClientHtml` e `ToHtml` sai 100% escapado.
- Testar `SafeFileToken("192.168.1.50:5555")` → `"192.168.1.50_5555"`.

---

## Critérios de Aceitação
- [x] Nenhum esquema `javascript:` ou `data:` passa pelo `AiReportToHtml`. *(prova: `ReportBuilderTests` — allowlist `http(s)`/`mailto`)*
- [x] Todos os 4 formatos HTML escapam qualquer input malicioso em `Device`, `Results`, `Notes` e `InsuranceForm`. *(prova: `ReportBuilderTests.SerialMaliciosoEEscapadoEmTodosOsFormatos` cobre `ToHtml`/`ToClientHtml`/`ToInsuranceHtml`, e `AiReportHtmlEscapesRawHtml` o de IA; `SafeCssToken`/`SafeFileToken` à parte)*
- [x] `AbandonedMutexException` é tratada sem crash em `ScanHistory`. *(prova: `ScanHistoryTests.MutexAbandonado_NaoImpedeAEscritaSeguinte` — uma thread toma o mutex do ficheiro e morre sem o libertar; o `Add` seguinte apanha o abandonado, escreve e a instância continua a escrever. Com `catch (AbandonedMutexException) { throw; }` o teste falha com `AbandonedMutexException: The wait completed due to an abandoned mutex`. O nome do mutex passou a derivar do caminho (`LockNameFor`), por isso históricos diferentes não contendem — `MutexEDoFicheiro_NaoBloqueiaOutroHistorico`)*
- [x] Servidor que aceita a ligação e nunca responde não prende o `CloudClient`: 20 s por tentativa nos endpoints rápidos (com **uma** repetição) e 150 s na IA (**sem** repetir — cada tentativa pode ser cobrada). Sem resposta e com 5xx vale a mesma divisão: o idempotente repete uma vez, a IA não; 4xx não repete em nenhum. Exceção de rede (`HttpRequestException`): o idempotente repete sempre, a IA só quando o pedido não chegou a sair (DNS/ligação/TLS) — `ResponseEnded` não repete. *(prova: `CloudClientRetryTests` — handler pendurado, handler com guião de códigos e handler que falha a 1.ª tentativa com um `HttpRequestError` concreto, timeouts acelerados pela seam interna: 2 pedidos + 1 espera no caminho rápido e 1 pedido + 0 esperas na IA, no timeout, no 503 e nas exceções; 400 dá 1 pedido + 0 esperas. Mutações: IA a repetir, 5xx sem repetição, 5xx a repetir na IA, `IsPreSendFailure` sempre verdadeiro, sempre falso na IA, e timeout por defeito a 25 s — todas falham o teste respetivo)*
- [x] `AppLog` nunca deixa bytes UTF-8 órfãos no topo após rotação. *(prova: o caminho passou a ser injetável (`LogFile(filePath)` + `AppLog.RedirectTo` para testes) e `AppLogTests.RotacaoNaoDeixaUtf8OrfaoNoTopo` escreve 1 012 001 bytes cujo corte de 500 000 cai a meio de um `€` de 3 bytes, roda o log e decodifica a cauda com `UTF8Encoding(throwOnInvalidBytes: true)`. Sem o salto até ao `\n` o teste falha com `DecoderFallbackException: Unable to translate bytes [AC] at index 0` — o byte de continuação do `€`)*
