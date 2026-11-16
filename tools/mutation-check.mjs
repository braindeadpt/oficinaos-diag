#!/usr/bin/env node
// Guardião da regra «provado por mutação».
//
// O plano diz que uma guarda só conta como provada se a suite ficar vermelha
// com o defeito de volta. Isso era feito à mão, uma vez por ticket, e é o tipo
// de coisa que se esquece — foi assim que duas «provas emprestadas» chegaram à
// revisão. Este script aplica defeitos conhecidos, um a um, exige que a
// verificação fique vermelha e repõe o ficheiro sempre.
//
// Uso:
//   node tools/mutation-check.mjs              mutantes cobertos por `dotnet test`
//   node tools/mutation-check.mjs --uishots    + os do harness (precisa de Windows)
//   node tools/mutation-check.mjs --seco       só confirma que cada mutante aplica
//   node tools/mutation-check.mjs --completo   corre a suite inteira (mais lento)
//
// Saída: 0 = todos mortos · 1 = algum sobreviveu ou não aplicou ·
//        2 = não consegui repor um ficheiro (para tudo e grita).

import { readFileSync, writeFileSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { join } from "node:path";

const raiz = process.cwd();
const flags = new Set(process.argv.slice(2));
const comUi = flags.has("--uishots");
const seco = flags.has("--seco");
const completo = flags.has("--completo");
// `--so=a,b` corre só os mutantes cujo nome contém um dos trechos: um job de
// UI completo são ~40 min, e triagem de um mutante não pode custar isso.
const so = [...flags]
  .filter((f) => f.startsWith("--so="))
  .flatMap((f) => f.slice(5).split(","))
  .map((s) => s.trim())
  .filter(Boolean);

/**
 * Cada entrada é um defeito **real** — todos já estiveram no código ou foram
 * apanhados por uma sonda nesta série de tickets.
 *  - guarda `testes`: `dotnet test` tem de ficar vermelho (corre em qualquer SO).
 *  - guarda `uishots`: o harness tem de falhar com `prova` no log (só Windows).
 */
const mutantes = [
  {
    // Geometria: se a conta deixar de encolher para a área útil, a janela volta
    // a sair do ecrã a 150% — e a prova tem de morrer por isso.
    nome: "a janela ignora a área útil e fica maior que o ecrã",
    ficheiro: "src/OficinaDiag.Core/Ui/WindowFit.cs",
    de: "        var largura = Math.Min(Math.Max(larguraPedida, 0), a.Width);",
    para: "        var largura = Math.Max(larguraPedida, 0); // mutante: sem teto da área",
    guarda: "testes",
    filtro: "WindowFitTests",
    prova: "ACaixaCabeSempreNaAreaUtil",
  },
  {
    // A assinatura do DpiChanged: sem ela a janela nunca reencaixa quando a
    // escala muda — e o harness não levanta o evento à mão, por isso a prova
    // observa a ligação por reflexão.
    nome: "mudar a escala deixa de reencaixar a janela",
    ficheiro: "src/OficinaDiag/MainWindow.xaml.cs",
    de: "        DpiChanged += (_assinaturaDpi = AoMudarEscala);",
    para: "        // mutante: sem assinatura do DpiChanged",
    guarda: "uishots",
    prova: "dpi: o DpiChanged está ligado ao reencaixe",
  },
  {
    // Prazo por prova: sem espera, o aviso tem de falhar **com o motivo** em
    // vez de dar `false` mudo (run 43929190520).
    nome: "o aviso da bancada deixa de ser esperado",
    ficheiro: "tools/UiShots/Program.cs",
    de: `            if (hwnd == IntPtr.Zero)
            {
                Thread.Sleep(100);
                continue;
            }`,
    para: `            if (hwnd == IntPtr.Zero)
            {
                return (null, "o diálogo não apareceu");
            }`,
    guarda: "uishots",
    prova: "bancada: o aviso apareceu (o diálogo não apareceu)",
  },
  {
    // Máscaras: um `?? 0` que inventa um veredicto tranquilizador.
    nome: "um dump de contas ilegível vira «sem contas»",
    ficheiro: "src/OficinaDiag.Core/Devices/AdbParsing.cs",
    de: '            return dump.Contains("Account {", StringComparison.Ordinal) ? null : 0;',
    para: '            return 0; // mutante: ilegível vira zero',
    guarda: "testes",
    filtro: "AdbParsingTests",
    prova: "ContasComDumpIlegivel_NaoViramSemContas",
  },
  {
    // Máscara: `catch { }` (e um `continue`) que perdem prova sem rasto.
    nome: "as fotos ilegíveis somem do relatório",
    ficheiro: "src/OficinaDiag.Core/Reports/ReportBuilder.cs",
    de: '        var ilegiveis = 0;',
    para: '        var ilegiveis = -1; // mutante: a contagem nunca chega a 1',
    guarda: "testes",
    filtro: "ReportBuilderTests",
    prova: "FotosIlegiveis_NaoDesaparecemSemRasto",
  },
  {
    // Máscara com consequência física: uma etiqueta impressa sem serial.
    nome: "a etiqueta imprime ? no lugar do serial",
    ficheiro: "src/OficinaDiag/Reports/LabelPrinter.cs",
    de: 'var serial = r.Device.Serial ?? "?";',
    para: 'var serial = "?"; // mutante: etiqueta sem serial',
    guarda: "uishots",
    prova: "botões: a etiqueta leva o serial do relatório",
  },
  {
    nome: "indexer do SetState (o checklist morto)",
    ficheiro: "src/OficinaDiag.Core/Grading/ChecklistMarks.cs",
    de: "var atual = marcas.TryGetValue(chave, out var v) ? v : 0;",
    para: "var atual = marcas[chave]; // mutante: indexer direto",
    guarda: "testes",
    filtro: "ChecklistMarksTests",
    prova: "ChecklistMarksTests",
  },
  {
    nome: "a terceira falha deixa de avisar",
    ficheiro: "src/OficinaDiag.Core/Log/FailureGuard.cs",
    de: "Avisar: total == Limite,",
    para: "Avisar: total == int.MaxValue, // mutante: nunca avisa",
    guarda: "testes",
    filtro: "FailureGuardTests",
    prova: "FailureGuardTests",
  },
  // Não há mutante para o rollback: com um ficheiro em uso o Copy-Item falha
  // antes de escrever nada, e desligar as duas vias de reposição deixa o
  // harness verde (runs 43921766213, 43922457138 e 43923007685). O rollback
  // fica como defesa para a cópia que já escreveu — caso que o runner não
  // consegue reproduzir.
  {
    nome: "o backup do staging não copia a árvore",
    ficheiro: "src/OficinaDiag.Core/Cloud/UpdateChecker.cs",
    de: `            "  Copy-Item $appDir $bak -Recurse -Force -ErrorAction Stop",`,
    para: `            "  New-Item -ItemType Directory -Force $bak | Out-Null",`,
    guarda: "uishots",
    prova: "staging: falha a meio deixa o build anterior",
  },
  {
    nome: "a falha do staging fica sem aviso",
    ficheiro: "src/OficinaDiag.Core/Cloud/UpdateChecker.cs",
    de: `            "    Avisar-Falha 'a cópia do staging falhou a meio'",`,
    para: `            "    # sem aviso para o técnico",`,
    guarda: "uishots",
    prova: "staging: a falha fica no marcador que o arranque mostra",
  },
  {
    nome: "Paint do checklist com indexer",
    ficheiro: "src/OficinaDiag/ChecklistDialog.xaml.cs",
    de: "        var s = _state.GetValueOrDefault(key);",
    para: "        var s = _state[key]; // mutante: rebenta em item sem marca",
    guarda: "uishots",
    // A prova é o log, não o clique: o indexer rebenta a pintar, o guarda do
    // `App` apanha e escreve «! UI:», e o harness nunca chega ao clique — foi
    // assim que este mutante parecia sobreviver (runs 43910480533/43911085972).
    prova: "log sem exceções engolidas",
  },
  {
    nome: "GUARDAR deixa de gravar o idioma",
    ficheiro: "src/OficinaDiag/SettingsDialog.xaml.cs",
    de: "        _config.Language = L10n.Current;",
    para: "        // mutante: não grava o idioma escolhido",
    guarda: "uishots",
    prova: "commit: o config ficou com o idioma do preview",
  },
  {
    nome: "o 2.º telemóvel rouba a bancada",
    ficheiro: "src/OficinaDiag/MainWindow.xaml.cs",
    de: "        if (_current is null) SelectDevice(d, announce: false);",
    para: "        SelectDevice(d, announce: false); // mutante: rouba a bancada",
    guarda: "uishots",
    prova: "seletor: o 2º aparelho não rouba a bancada",
  },
  {
    nome: "o aviso de reinício mostra outra mensagem",
    ficheiro: "src/OficinaDiag/MainWindow.xaml.cs",
    de: 'MessageBox.Show(this, L10n.T("log.update.ready"), Title,',
    para: 'MessageBox.Show(this, L10n.T("log.update.dl"), Title, // mutante: texto trocado',
    guarda: "uishots",
    prova: "atualização: o aviso de reinício é o do L10n",
  },
  {
    nome: "a falha repetida não desliga o botão",
    ficheiro: "src/OficinaDiag/MainWindow.xaml.cs",
    de: "        if (FindName(acao) is UIElement alvo) alvo.IsEnabled = false;",
    para: "        // mutante: deixa o botão ligado",
    guarda: "uishots",
    prova: "falha repetida: o botão ficou desligado",
  },
  {
    nome: "COMPARAR abre sem scan anterior",
    ficheiro: "src/OficinaDiag/MainWindow.xaml.cs",
    de: "        if (previous.Count == 0)",
    para: "        if (previous.Count < 0) // mutante: nunca diz que não há anterior",
    guarda: "uishots",
    prova: "botões: COMPARAR sem scan anterior diz que não há nenhum",
  },
  {
    nome: "a etiqueta perde o serial",
    ficheiro: "src/OficinaDiag/Reports/LabelPrinter.cs",
    de: '        var serial = r.Device.Serial ?? "?";',
    para: '        var serial = "?"; // mutante: sem o serial do relatório',
    guarda: "uishots",
    prova: "botões: a etiqueta leva o serial do relatório",
  },
  {
    nome: "a seta para cima do Win95 perde o glifo",
    ficheiro: "src/OficinaDiag/Themes/Win95.xaml",
    de: 'Data="M 0 4 L 8 4 L 4 0 Z"',
    para: 'Data="M 0 0 L 0 0 Z"',
    guarda: "uishots",
    prova: "setas: as duas setas têm glifo",
  },
  {
    // Um valor inválido no trigger não serve de mutante: rebenta o XAML com
    // `XamlParseException` e o harness fica vermelho pela razão errada (run
    // 43934235588). A regressão realista é a zona de página pintar como o
    // track — a barra fica um bloco liso e ninguém distingue onde carregar.
    nome: "a zona de página horizontal pinta como o track",
    ficheiro: "src/OficinaDiag/Themes/Win95.xaml",
    de: `                                        <RepeatButton Command="ScrollBar.PageLeftCommand" Focusable="False">
                                            <RepeatButton.Template>
                                                <ControlTemplate TargetType="RepeatButton">
                                                    <Border Background="#DFDFDF"/>`,
    para: `                                        <RepeatButton Command="ScrollBar.PageLeftCommand" Focusable="False">
                                            <RepeatButton.Template>
                                                <ControlTemplate TargetType="RepeatButton">
                                                    <Border Background="#C0C0C0"/>`,
    guarda: "uishots",
    prova: "setas-h: a zona de página distingue-se do track",
  },
  {
    nome: "a seta esquerda do 95 perde o glifo",
    ficheiro: "src/OficinaDiag/Themes/Win95.xaml",
    de: 'Data="M 4 0 L 4 8 L 0 4 Z"',
    para: 'Data="M 0 0 L 0 1 L 0 1 Z"',
    guarda: "uishots",
    prova: "setas-h: as duas setas têm glifo",
  },
  {
    nome: "o track do Win95 pinta como as zonas de página",
    ficheiro: "src/OficinaDiag/Themes/Win95.xaml",
    de: '    <Style TargetType="ScrollBar">\n        <Setter Property="Background" Value="#C0C0C0"/>',
    // Nota: nada de comentários aqui — isto é XAML, e um `//` dentro da tag
    // deixa o ficheiro inválido (o harness «não compila» e o mutante
    // sobrevive por engano). O nome do mutante já diz o que ele faz.
    para: '    <Style TargetType="ScrollBar">\n        <Setter Property="Background" Value="#DFDFDF"/>',
    guarda: "uishots",
    prova: "setas: a zona de página distingue-se do track",
  },
  {
    nome: "o App deixa de delegar no MainWindow",
    ficheiro: "src/OficinaDiag/App.xaml.cs",
    de: "            if (Current.MainWindow is MainWindow janela) janela.RegistarFalha(args.Exception);",
    para: "            if (false && Current.MainWindow is MainWindow janela) janela.RegistarFalha(args.Exception); // mutante: regista e não avisa",
    guarda: "uishots",
    prova: "produção: o handler do App avisou à terceira",
  },
  {
    nome: "o descarregador por omissão escreve fora da pasta da app",
    ficheiro: "src/OficinaDiag/MainWindow.xaml.cs",
    de: "    internal static string PastaDeInstalacao => AppContext.BaseDirectory;",
    para: "    internal static string PastaDeInstalacao => System.IO.Path.GetTempPath(); // mutante: fora da pasta da app",
    guarda: "uishots",
    prova: "produção: a atualização escreve na pasta da app",
  },
  {
    nome: "o Terminar por omissão deixa de fechar a app",
    ficheiro: "src/OficinaDiag/MainWindow.xaml.cs",
    de: "    internal static void TerminarDeProducao() => Application.Current.Shutdown();",
    para: "    internal static void TerminarDeProducao() { } // mutante: não fecha",
    guarda: "uishots",
    prova: "produção: o TerminarDeProducao chama o Application.Shutdown",
  },
  {
    nome: "o Baixar por omissão deixa de ser o de produção",
    ficheiro: "src/OficinaDiag/MainWindow.xaml.cs",
    de: "        BaixarDeProducao;",
    para: "        (info, current) => BaixarPara(PastaDeInstalacao, info, current); // mutante: lambda",
    guarda: "uishots",
    prova: "produção: o Baixar por omissão é o BaixarDeProducao",
  },
  {
    nome: "a assinatura da release deixa de ser verificada",
    ficheiro: "src/OficinaDiag.Core/Cloud/UpdateChecker.cs",
    de: "        if (!VerifySumsSignature(sumsBytes, sigB64, publicKeyB64))",
    para: "        if (false && !VerifySumsSignature(sumsBytes, sigB64, publicKeyB64)) // mutante: aceita tudo",
    // `ValidateSums` tem testes unitários: o mutante morre aí, antes de o
    // harness correr. O que o harness apanha é o `DownloadAndStageAsync` a
    // ignorar o `ValidateSums` (sonda do run 43896607750).
    guarda: "testes",
    // Tem de ser um teste do `ValidateSums`: os do `VerifySumsSignature`
    // chamam a função directamente e não vêem este `if` (foi assim que este
    // mutante sobreviveu ao run 43897606802).
    filtro: "UpdateCheckerTests.RejectsValidVersionWithBadSignature",
    prova: "UpdateCheckerTests",
  },
  {
    nome: "o replay de um SUMS antigo deixa de ser recusado",
    ficheiro: "src/OficinaDiag.Core/Cloud/UpdateChecker.cs",
    de: "        if (declared <= current)",
    para: "        if (false && declared <= current) // mutante: aceita replay",
    guarda: "testes",
    filtro: "UpdateCheckerTests.RejectsSignedSumsNotNewerThanLocal",
    prova: "UpdateCheckerTests",
  },
  {
    nome: "o checksum do zip deixa de ser conferido",
    ficheiro: "src/OficinaDiag.Core/Cloud/UpdateChecker.cs",
    de: "            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))",
    para: "            if (false && !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)) // mutante: aceita o zip errado",
    guarda: "uishots",
    prova: "instalação: o checksum do zip não confere",
  },
  {
    nome: "a verificação ignora a chave injectada",
    ficheiro: "src/OficinaDiag.Core/Cloud/UpdateChecker.cs",
    de: "            if (ValidateSums(sumsBytes, sigB64, info.Tag, current, ChavePublicaDaRelease) is { } err)",
    para: "            if (ValidateSums(sumsBytes, sigB64, info.Tag, current, ReleaseSigningPublicKey) is { } err) // mutante: chave embutida",
    guarda: "uishots",
    prova: "instalação: o download e o staging correram",
  },
];

const mundo = (cmd, args, opts = {}) => {
  const r = spawnSync(cmd, args, { cwd: raiz, encoding: "utf8", ...opts });
  return {
    codigo: r.status === null ? 1 : r.status,
    saida: `${r.stdout ?? ""}${r.stderr ?? ""}`,
    erro: r.error,
  };
};

const gitLimpo = (ficheiro) =>
  mundo("git", ["status", "--porcelain", "--", ficheiro]).saida.trim() === "";

const contar = (texto, agulha) => texto.split(agulha).length - 1;

/** Corre a guarda do mutante e devolve `{ vermelho, motivo }`. */
function correrGuarda(m) {
  if (m.guarda === "testes") {
    const alvo = completo
      ? ["test", "tests/OficinaDiag.Tests", "-c", "Release", "--nologo"]
      : ["test", "tests/OficinaDiag.Tests", "-c", "Release", "--nologo",
         "--filter", `FullyQualifiedName~${m.filtro}`];
    const r = mundo("dotnet", alvo);
    if (r.erro) return { vermelho: false, motivo: `dotnet não arrancou: ${r.erro.message}` };
    const provou = r.saida.includes(m.prova);
    return {
      vermelho: r.codigo !== 0 && provou,
      motivo: r.codigo === 0
        ? "suite continuou verde"
        : provou ? `vermelho (${m.prova})` : "vermelho, mas sem o teste esperado",
    };
  }

  // uishots: construir e correr o harness; o job falha com [FALHA] no log.
  const build = mundo("dotnet", ["build", "tools/UiShots/UiShots.csproj", "-c", "Release",
    "-p:EnableWindowsTargeting=true", "--nologo"]);
  if (build.codigo !== 0) return { vermelho: false, motivo: "o harness não compila" };
  const exe = join(raiz, "tools", "UiShots", "bin", "Release", "net8.0-windows", "UiShots.exe");
  const destino = join(raiz, "out-store", "mutation-ui-shots");
  const r = mundo(exe, [destino]);
  if (r.erro) return { vermelho: false, motivo: `não arrancou: ${r.erro.message}` };
  const falha = r.saida.includes("[FALHA]") && r.saida.includes(m.prova);
  return {
    vermelho: r.codigo !== 0 && falha,
    motivo: r.codigo === 0
      ? "harness continuou verde"
      : falha ? `[FALHA] ${m.prova}` : "harness vermelho, mas sem a verificação esperada",
    falhas: r.saida.split("\n").filter((l) => l.includes("[FALHA]")),
    cauda: r.saida.split("\n").filter((l) => l.trim().length > 0),
  };
}

const podeUi = process.platform === "win32";
// `--seco` valida **todos** os mutantes (mesmo os de UI fora do Windows): é a
// única forma de saber que um mutante de harness não ficou órfão até ao dia em
// que alguém o corre.
const alvoBase = seco
  ? mutantes
  : mutantes.filter((m) => m.guarda === "testes" || (comUi && podeUi));
const alvo = so.length > 0
  ? alvoBase.filter((m) => so.some((t) => m.nome.toLowerCase().includes(t.toLowerCase())))
  : alvoBase;
const saltados = mutantes.filter((m) => !alvo.includes(m));

if (alvo.length === 0) {
  console.error("✗ nenhum mutante a correr (usa --uishots num Windows para os da UI)");
  process.exit(1);
}

let problemas = 0;
const resultados = [];

for (const m of alvo) {
  const caminho = join(raiz, m.ficheiro);
  const original = readFileSync(caminho, "utf8");

  // `--seco` não escreve nada, por isso não precisa da árvore limpa — e é
  // precisamente antes de commitar que apetece correr isto.
  if (!seco && !gitLimpo(m.ficheiro)) {
    resultados.push({ m, estado: "saltado", motivo: "ficheiro com alterações por commitar" });
    problemas++;
    continue;
  }
  // O `de` é escrito com `\n`, mas no Windows o `checkout` entrega CRLF —
  // aplicar sobre o texto normalizado e devolver o EOL original é o que faz o
  // mesmo mutante valer nos dois sistemas (run 43907212436: «0 ocorrência»).
  const crlf = original.includes("\r\n");
  const normalizado = crlf ? original.replace(/\r\n/g, "\n") : original;
  const ocorrencias = contar(normalizado, m.de);
  if (ocorrencias !== 1) {
    resultados.push({ m, estado: "não aplica", motivo: `${ocorrencias} ocorrência(s) do código original` });
    problemas++;
    continue;
  }

  if (seco) {
    resultados.push({ m, estado: "aplicaria", motivo: "código encontrado uma vez" });
    continue;
  }

  const mutado = normalizado.replace(m.de, m.para);
  writeFileSync(caminho, crlf ? mutado.replace(/\n/g, "\r\n") : mutado);
  let veredicto;
  try {
    veredicto = correrGuarda(m);
  } finally {
    writeFileSync(caminho, original);
    if (readFileSync(caminho, "utf8") !== original) {
      console.error(`✗ não consegui repor ${m.ficheiro} — para tudo aqui. Confirma com «git diff».`);
      process.exit(2);
    }
  }

  resultados.push({
    m,
    estado: veredicto.vermelho ? "morto" : "sobreviveu",
    motivo: veredicto.motivo,
    // Sem isto o resumo perdia as linhas do harness e ficava só o «sobreviveu»
    // — que é o que se está a tentar diagnosticar.
    falhas: veredicto.falhas,
    cauda: veredicto.cauda,
  });
  if (!veredicto.vermelho) problemas++;
}

const largura = Math.max(...resultados.map((r) => r.m.nome.length));
for (const { m, estado, motivo, falhas, cauda } of resultados) {
  const marca = estado === "morto" || estado === "aplicaria" ? "✔" : "✘";
  console.log(`${marca} ${m.nome.padEnd(largura)}  ${estado} — ${motivo}`);
  // Um mutante que só deixa o harness vermelho por um caminho genérico é o
  // sítio onde uma prova emprestada se esconde: diz-se o que o harness disse,
  // em vez de obrigar a outra sonda de 40 minutos para descobrir.
  if (estado === "sobreviveu" && falhas?.length) {
    for (const f of falhas.slice(0, 5)) console.log(`      ${f.trim()}`);
  } else if (estado === "sobreviveu" && cauda?.length) {
    // Sem `[FALHA]` nenhuma: o harness rebentou ou foi o vigia. As últimas
    // linhas dizem qual dos dois, e poupam outra sonda.
    console.log("      (sem [FALHA] — fim da saída do harness)");
    for (const l of cauda.slice(-6)) console.log(`      ${l.trim()}`);
  }
}
for (const m of saltados) {
  console.log(`– ${m.nome.padEnd(largura)}  saltado — precisa de --uishots num Windows`);
}

if (seco) {
  console.log(problemas === 0
    ? `\n✓ os ${alvo.length} mutantes aplicam (não foi corrida nenhuma suite)`
    : `\n✗ ${problemas} mutante(s) órfão(s) — o código mudou e o mutante procura um texto que já não existe`);
  process.exit(problemas === 0 ? 0 : 1);
}

console.log(problemas === 0
  ? `\n✓ ${alvo.length}/${alvo.length} mutantes mortos: a suite morde onde diz morder`
  : `\n✗ ${problemas} mutante(s) sobreviveram ou não aplicaram — a prova desse critério está emprestada`);
process.exit(problemas === 0 ? 0 : 1);
