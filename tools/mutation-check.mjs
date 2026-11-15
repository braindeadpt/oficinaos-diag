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

/**
 * Cada entrada é um defeito **real** — todos já estiveram no código ou foram
 * apanhados por uma sonda nesta série de tickets.
 *  - guarda `testes`: `dotnet test` tem de ficar vermelho (corre em qualquer SO).
 *  - guarda `uishots`: o harness tem de falhar com `prova` no log (só Windows).
 */
const mutantes = [
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
  {
    nome: "Paint do checklist com indexer",
    ficheiro: "src/OficinaDiag/ChecklistDialog.xaml.cs",
    de: "        var s = _state.GetValueOrDefault(key);",
    para: "        var s = _state[key]; // mutante: rebenta em item sem marca",
    guarda: "uishots",
    prova: "clique: checklist 'ok' marca o item",
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
    nome: "o track do Win95 pinta como as zonas de página",
    ficheiro: "src/OficinaDiag/Themes/Win95.xaml",
    de: '    <Style TargetType="ScrollBar">\n        <Setter Property="Background" Value="#C0C0C0"/>',
    para: '    <Style TargetType="ScrollBar">\n        <Setter Property="Background" Value="#DFDFDF"/> // mutante: igual às zonas',
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
  };
}

const podeUi = process.platform === "win32";
// `--seco` valida **todos** os mutantes (mesmo os de UI fora do Windows): é a
// única forma de saber que um mutante de harness não ficou órfão até ao dia em
// que alguém o corre.
const alvo = seco
  ? mutantes
  : mutantes.filter((m) => m.guarda === "testes" || (comUi && podeUi));
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
  const ocorrencias = contar(original, m.de);
  if (ocorrencias !== 1) {
    resultados.push({ m, estado: "não aplica", motivo: `${ocorrencias} ocorrência(s) do código original` });
    problemas++;
    continue;
  }

  if (seco) {
    resultados.push({ m, estado: "aplicaria", motivo: "código encontrado uma vez" });
    continue;
  }

  writeFileSync(caminho, original.replace(m.de, m.para));
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
  });
  if (!veredicto.vermelho) problemas++;
}

const largura = Math.max(...resultados.map((r) => r.m.nome.length));
for (const { m, estado, motivo } of resultados) {
  const marca = estado === "morto" || estado === "aplicaria" ? "✔" : "✘";
  console.log(`${marca} ${m.nome.padEnd(largura)}  ${estado} — ${motivo}`);
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
