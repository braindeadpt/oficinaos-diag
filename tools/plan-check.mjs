#!/usr/bin/env node
// Guardião dos critérios do plan-nota10.
//
// Falha quando o plano deixa de ser verificável: placar que não bate com as
// contagens, critério [x] sem prova declarada, critério [ ] sem dizer o que
// falta, ou prova que cita um teste que já não existe (o caso que interessa —
// apagar o teste não pode deixar o critério verde).
//
// Uso: node tools/plan-check.mjs   (da raiz do repositório)

import { readFileSync, readdirSync } from "node:fs";
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

// --- 3. placar do STATUS.md bate com a realidade --------------------------
const status = readFileSync(join("plan-nota10", "STATUS.md"), "utf8");
const placar = /Placar: \*\*(\d+) provados, (\d+) pendentes\*\*/.exec(status);
if (!placar) {
  erros.push("STATUS.md sem «Placar: **N provados, M pendentes**»");
} else {
  const [, x, aberto] = placar;
  if (Number(x) !== provados.length || Number(aberto) !== pendentes.length) {
    erros.push(
      `STATUS.md diz ${x}/${aberto} mas os tickets têm ${provados.length} [x] e ${pendentes.length} [ ]`,
    );
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
  `✓ plano coerente: ${provados.length} provados, ${pendentes.length} pendentes ` +
    `em ${tickets.length} tickets, todos com prova ou motivo`,
);
