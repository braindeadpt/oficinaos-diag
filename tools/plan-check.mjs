#!/usr/bin/env node
// Guardião dos critérios do plan-nota10.
//
// Falha quando o plano deixa de ser verificável: placar que não bate com as
// contagens, critério [x] sem prova declarada, critério [ ] sem dizer o que
// falta, ou prova que cita um teste que já não existe (o caso que interessa —
// apagar o teste não pode deixar o critério verde).
//
// Uso: node tools/plan-check.mjs   (da raiz do repositório)

import { existsSync, readFileSync, readdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";

const erros = [];
const avisos = [];

// --- tickets -------------------------------------------------------------
const dirTickets = join("plan-nota10", "tickets");
const tickets = readdirSync(dirTickets)
  .filter((f) => f.endsWith(".md"))
  .sort();

const criterios = []; // {ticket, linha, feito, texto}
for (const f of tickets) {
  const linhas = readFileSync(join(dirTickets, f), "utf8").split(/\r?\n/);
  linhas.forEach((linha, i) => {
    const m = /^- \[( |x)\] (.+)$/.exec(linha);
    if (m) criterios.push({ ticket: f, n: i + 1, feito: m[1] === "x", texto: m[2] });
  });
}

if (criterios.length === 0) erros.push("nenhum critério encontrado em plan-nota10/tickets");

// --- 1. cada [x] declara a prova e cita algo verificável ------------------
const provados = criterios.filter((c) => c.feito);
const pendentes = criterios.filter((c) => !c.feito);

for (const c of provados) {
  if (!c.texto.includes("*(prova:")) {
    erros.push(`${c.ticket}:${c.n} está [x] sem *(prova: …) — critério feito precisa de prova`);
  }
}
for (const c of pendentes) {
  if (!/pendente de/i.test(c.texto)) {
    erros.push(`${c.ticket}:${c.n} está [ ] sem «pendente de …» — dizer do que depende`);
  }
}

// --- 2. provas que citam testes: o teste tem de existir -------------------
// Apenas identificadores claros: `FooTests` ou `FooTests.Metodo`. Provas que
// citam handoffs, corridas do CI ou ficheiros .mjs não entram nesta regra.
const ficheirosCs = [];
(function varrer(dir) {
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    const p = join(dir, e.name);
    if (e.isDirectory()) {
      if (e.name === "bin" || e.name === "obj" || e.name === "node_modules") continue;
      varrer(p);
    } else if (e.name.endsWith(".cs")) {
      ficheirosCs.push(p);
    }
  }
})("tests");

const fontes = new Map(ficheirosCs.map((p) => [p, readFileSync(p, "utf8")]));

const RE_TESTE = /`([A-Za-z_]\w*)Tests(?:\.([A-Za-z_]\w*))?`/g;
for (const c of provados) {
  for (const m of c.texto.matchAll(RE_TESTE)) {
    const [, classe, metodo] = m;
    const ficheiro = `tests/OficinaDiag.Tests/${classe}Tests.cs`;
    const fonte = fontes.get(ficheiro);
    if (!fonte) {
      erros.push(
        `${c.ticket}:${c.n} cita \`${classe}Tests\` mas ${ficheiro} não existe — prova órfã`,
      );
      continue;
    }
    if (metodo && !new RegExp(`\\b${metodo}\\b`).test(fonte)) {
      erros.push(
        `${c.ticket}:${c.n} cita \`${classe}Tests.${metodo}\`, que não aparece em ${ficheiro}`,
      );
    }
    if (!metodo && !new RegExp(`\\bclass ${classe}Tests\\b`).test(fonte)) {
      avisos.push(`${c.ticket}:${c.n}: ${ficheiro} não declara class ${classe}Tests`);
    }
  }
}

// --- 2b. contrato com o harness de UI ------------------------------------
// A prova de um critério pode ser uma secção do `tools/UiShots`. Nada ligava
// os dois lados: renomear uma `Verificar("…")` deixava o critério [x] a citar
// uma secção que já não existia — o «prova emprestada» outra vez. Duas regras,
// as duas verificadas aqui:
//   1. quem cita o harness cita uma secção que existe (ou uma corrida do CI);
//   2. nenhuma secção do harness fica sem um critério que a cite — prova que
//      ninguém usa é prova que ninguém vê a correr.
const caminhoHarness = join("tools", "UiShots", "Program.cs");
let secoesHarness = [];
if (existsSync(caminhoHarness)) {
  const fonte = readFileSync(caminhoHarness, "utf8");
  const literais = [...fonte.matchAll(/Verificar\(\s*"((?:[^"\\]|\\.)*)"/g)].map((m) => m[1]);
  secoesHarness = [
    ...new Set(literais.filter((l) => l.includes(":")).map((l) => l.slice(0, l.indexOf(":")))),
  ];
  for (const c of provados) {
    if (!c.texto.includes("UiShots")) continue;
    const citaSecao = secoesHarness.some((s) => c.texto.includes(`${s}:`));
    const citaCorrida = /run `\d+`/.test(c.texto);
    if (!citaSecao && !citaCorrida) {
      erros.push(
        `${c.ticket}:${c.n} cita \`tools/UiShots\` sem nomear uma secção que exista ` +
          `(secções: ${secoesHarness.join(", ")}) nem uma corrida do CI`,
      );
    }
  }
  for (const s of secoesHarness) {
    if (!provados.some((c) => c.texto.includes(`${s}:`))) {
      erros.push(
        `tools/UiShots tem a secção «${s}:» mas nenhum critério [x] a cita — ` +
          `prova do harness sem critério no plano`,
      );
    }
  }
}

// --- 3. placar: o número vive em placar.json, não na prosa ----------------
// Antes o script arrancava os números de uma linha de prosa do STATUS.md, com
// uma regex que já tinha engolido um placar histórico do handoff. Agora a
// fonte é estruturada: `--escrever` recria-a a partir dos tickets, e o STATUS
// continua a citá-la — mas quem manda é o JSON.
const caminhoPlacar = join("plan-nota10", "placar.json");
const status = readFileSync(join("plan-nota10", "STATUS.md"), "utf8");

if (process.argv.includes("--escrever")) {
  writeFileSync(
    caminhoPlacar,
    JSON.stringify({
      provados: provados.length,
      pendentes: pendentes.length,
      nota:
        "O placar vive aqui. plan-check compara com os [x]/[ ] dos tickets e exige " +
        "que o STATUS.md cite os mesmos números; `node tools/plan-check.mjs --escrever` " +
        "recria este ficheiro a partir dos tickets.",
    }, null, 2) + "\n",
  );
  console.log(
    `placar.json escrito: ${provados.length} provados, ${pendentes.length} ` +
      `${pendentes.length === 1 ? "pendente" : "pendentes"}`,
  );
}

let placar;
try {
  placar = JSON.parse(readFileSync(caminhoPlacar, "utf8"));
} catch (e) {
  erros.push(
    `plan-nota10/placar.json ilegível ou ausente (${e.message}) — ` +
      `corre \`node tools/plan-check.mjs --escrever\``,
  );
}

const inteiro = (v) => Number.isInteger(v) && v >= 0;
if (placar) {
  if (!inteiro(placar.provados) || !inteiro(placar.pendentes)) {
    erros.push("placar.json precisa de «provados» e «pendentes» inteiros ≥ 0");
  } else if (placar.provados !== provados.length || placar.pendentes !== pendentes.length) {
    erros.push(
      `placar.json diz ${placar.provados}/${placar.pendentes} mas os tickets têm ` +
        `${provados.length} [x] e ${pendentes.length} [ ] — se a mudança é para ficar, ` +
        `corre \`node tools/plan-check.mjs --escrever\` e atualiza o STATUS.md`,
    );
  } else {
    // A prosa continua a citar o placar: um leitor não abre um JSON para
    // saber como está o plano. Só que agora é conferida, não a fonte.
    const prosa = /Placar: \*\*(\d+) provados?, (\d+) pendentes?\*\*/.exec(status);
    if (!prosa) {
      erros.push("STATUS.md sem «Placar: **N provados, M pendentes**» — cite o placar.json");
    } else if (Number(prosa[1]) !== placar.provados || Number(prosa[2]) !== placar.pendentes) {
      erros.push(
        `STATUS.md diz ${prosa[1]}/${prosa[2]} mas placar.json diz ` +
          `${placar.provados}/${placar.pendentes}`,
      );
    }
  }
}

// --- saída ---------------------------------------------------------------
for (const a of avisos) console.log(`· aviso: ${a}`);
if (erros.length) {
  console.error(`✗ plano inconsistente (${erros.length} erro(s)):`);
  for (const e of erros) console.error(`  - ${e}`);
  process.exit(1);
}
console.log(
  `✓ plano coerente: ${provados.length} provados, ${pendentes.length} ` +
  `${pendentes.length === 1 ? "pendente" : "pendentes"} ` +
    `em ${tickets.length} tickets (placar: plan-nota10/placar.json), todos com prova ou motivo`,
);
