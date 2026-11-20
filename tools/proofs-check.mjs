#!/usr/bin/env node
// Compara as verificações **declaradas** no harness (`Verificar("…", …)` em
// tools/UiShots/Program.cs) com as que **correram** de facto (linhas `[OK]` /
// `[FALHA]` do run).
//
// Porque isto existe: esta série já apanhou três provas falsas — o checklist
// morto, a captura cinzenta e o `FALHAS: 1` do `finally`. Nas três, a
// verificação existia no código e não media nada: ou não corria, ou corria tão
// longe do defeito que passava sempre. Um `[OK]` que falta no log é a única
// evidência directa de «esta prova foi saltada», e até aqui ninguém a olhava.
//
//   node tools/proofs-check.mjs out-store/ui-shots/provas.txt
//   node tools/proofs-check.mjs <log-do-CI>          (aceita o log do run)
//
// Sai com 1 quando uma verificação declarada não aparece, e com 1 também quando
// uma excepção da lista de «pode não correr» já não existe no código (lista
// podre esconde provas novas).
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

// `new URL(...).pathname` no Windows dá `/D:/…` e o caminho saía `D:\D:\…`
// (run 43899052215). `fileURLToPath` é o que sabe traduzir o esquema.
const fonte = join(dirname(fileURLToPath(import.meta.url)), "UiShots", "Program.cs");

const args = process.argv.slice(2);
const provas = args.find((a) => !a.startsWith("--")) ?? "out-store/ui-shots/provas.txt";

// Verificações que legitimamente podem não correr: estão dentro de um caminho
// que só acontece quando algo falta (um botão que não existe, um `FindName`
// nulo). Cada uma leva o motivo e o tool recusa entradas que já não existam.
const condicionais = [
  { trecho: "atualização: o botão existe", porque: "só corre quando o UpdateButton não é encontrado" },
  { trecho: "seletor: os controlos existem", porque: "só corre quando falta um controlo do seletor" },
  { trecho: "falha repetida: o botão existe", porque: "só corre quando o ScanButton não existe" },
  { trecho: "produção: o botão existe", porque: "só corre quando a janela do App não tem ScanButton" },
  { trecho: "setas: as Opções têm um ScrollViewer", porque: "só corre quando o diálogo não tem ScrollViewer" },
  { trecho: "setas: a barra vertical apareceu", porque: "só corre quando a barra não é visível" },
  { trecho: "clique: o checklist tem ok/defeito/n/t", porque: "só corre quando há menos de 6 marcas" },
  { trecho: "clique: opções tem o botão", porque: "só corre quando o rádio do idioma/tema não existe" },
  { trecho: "clique: {nome} tem o botão", porque: "só corre quando o diálogo não tem o botão" },
  { trecho: "{tema}: combo encontrado", porque: "só corre quando a janela não tem ComboBox" },
  { trecho: "{rotulo}: {nome} ligado à árvore", porque: "só corre quando o `TransformToAncestor` rebenta" },
  { trecho: "captura {nome}: legível", porque: "só corre quando o PNG não abre para leitura" },
  { trecho: "baselines: a pasta", porque: "só corre com --baselines e a pasta não existe" },
  { trecho: "baselines: {comparadas} capturas comparadas", porque: "só corre com --baselines e a pasta existe" },
  { trecho: "baselines: nenhuma baseline órfã", porque: "só corre com --baselines e a pasta existe" },
  { trecho: "baseline {nome}:", porque: "só corre quando a captura tem baseline na pasta" },
];

const texto = readFileSync(fonte, "utf8");

const escapar = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");

/**
 * Com que é que uma verificação declarada se reconhece no log. Uma mensagem
 * interpolada (`$"setas: {n} setas"`) só se pode reconhecer por padrão: cada
 * `{…}` vira `.*` e o resto é comparado à letra.
 */
function padraoDe(bruto, interpolada) {
  if (!interpolada) return (v) => v === bruto;
  let re = "";
  for (let i = 0; i < bruto.length; ) {
    const abre = bruto.indexOf("{", i);
    if (abre < 0) {
      re += escapar(bruto.slice(i));
      break;
    }
    re += escapar(bruto.slice(i, abre)) + ".*";
    // Salta o placeholder (com especificador de formato e tudo).
    let profundidade = 1;
    let j = abre + 1;
    while (j < bruto.length && profundidade > 0) {
      if (bruto[j] === "{") profundidade++;
      else if (bruto[j] === "}") profundidade--;
      j++;
    }
    i = j;
  }
  const regex = new RegExp(`^${re}$`);
  return (v) => regex.test(v);
}

/**
 * Lê a mensagem de um `Verificar(`, juntando literais concatenados com `+`.
 * Uma mensagem partida em duas linhas (`"…" + "…"`) é uma mensagem só: ler
 * apenas a primeira parte dava um padrão que nunca casava (run 43904930317).
 * Devolve `null` quando o argumento não é um literal (uma variável).
 */
function lerMensagem(src, inicio) {
  let interpolada = false;
  let out = "";
  let j = inicio;
  for (;;) {
    while (j < src.length && /\s/.test(src[j])) j++;
    if (src[j] === "$") {
      interpolada = true;
      j++;
    }
    if (src[j] !== '"') return null;
    j++;
    while (j < src.length && src[j] !== '"') {
      if (src[j] === "\\") {
        out += src[j] + src[j + 1];
        j += 2;
        continue;
      }
      out += src[j++];
    }
    if (j >= src.length) return null;
    j++; // fecha as aspas
    let k = j;
    while (k < src.length && /\s/.test(src[k])) k++;
    if (src[k] === "+") {
      j = k + 1;
      continue;
    }
    return { texto: out, interpolada };
  }
}

/** Verificações declaradas no código, na ordem em que aparecem. */
const declaradas = [];
for (const m of texto.matchAll(/Verificar\(/g)) {
  const lida = lerMensagem(texto, m.index + m[0].length);
  if (lida === null) continue; // argumento que não é literal: não é declarada
  const bruto = lida.texto
    .replace(/\\"/g, '"')
    .replace(/\\\\/g, "\\")
    .replace(/\\n/g, "\n");
  declaradas.push({
    texto: bruto,
    reconhece: padraoDe(bruto, lida.interpolada),
    linha: texto.slice(0, m.index).split("\n").length,
  });
}

let registo;
try {
  registo = readFileSync(provas, "utf8");
} catch {
  console.error(`✗ não consegui ler «${provas}» — o harness correu?`);
  process.exit(1);
}

const vistas = [];
// `\r?\n` e não `\n`: no Windows o ficheiro vem em CRLF e o `$` da regex
// não casa com o `\r` — 135 provas «faltavam» que estavam lá (run 43899490840).
for (const linha of registo.split(/\r?\n/)) {
  const m = /\[(?:OK|FALHA)\]\s+(.*)$/.exec(linha);
  if (m) vistas.push(m[1].trim());
}

const satisfaz = (d) => vistas.some((v) => d.reconhece(v));

const faltam = [];
const condicionaisUsadas = new Set();
const condicionalDe = (d) => condicionais.find((c) => d.texto.startsWith(c.trecho));
for (const d of declaradas) {
  if (satisfaz(d)) continue;
  const cond = condicionalDe(d);
  if (cond) {
    condicionaisUsadas.add(cond.trecho);
    continue;
  }
  faltam.push(d);
}

let erros = 0;
if (faltam.length > 0) {
  erros++;
  console.error(`✗ ${faltam.length} verificação(ões) declarada(s) não apareceram no log:`);
  for (const f of faltam) {
    console.error(`    Program.cs:${f.linha}  «${f.texto}»`);
  }
}

// Uma entrada da lista que já não existe no código é uma licença a mais.
for (const c of condicionais) {
  if (!declaradas.some((d) => d.texto.startsWith(c.trecho))) {
    erros++;
    console.error(`✗ a lista de condicionais tem «${c.trecho}», que já não existe no harness`);
  }
}

if (erros === 0) {
  console.log(
    `✓ ${declaradas.length} verificações declaradas, ${vistas.length} no log` +
      ` (${condicionaisUsadas.size} condicional(is) legítima(s) por correr)`,
  );
}
process.exit(erros === 0 ? 0 : 1);
