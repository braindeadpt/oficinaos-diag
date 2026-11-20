// Lê o coverage.cobertura.xml que o coverlet.collector deixa em
// tests/OficinaDiag.Tests/TestResults/** e impõe um limiar ratchet:
// o FLOOR só pode subir. Quando a cobertura real subir, sobe-se a
// constante no mesmo commit — assim ninguém a deixa cair sem reparar.
// Uso: node tools/coverage-check.mjs [dir-com-TestResults]

import { readdirSync, readFileSync, statSync, appendFileSync } from "node:fs";
import { join } from "node:path";

// Cobertura medida em 2026-11-20: 68,9% linhas / 45,8% branches do Core.
// Margem de ~4 pontos para oscilação pequena; subir sempre que subir a real.
const FLOOR_LINHAS = 0.65;
const FLOOR_BRANCHES = 0.42;

function encontraCobertura(dir) {
  const encontrados = [];
  const pilha = [dir];
  while (pilha.length) {
    const atual = pilha.pop();
    for (const entrada of readdirSync(atual)) {
      const caminho = join(atual, entrada);
      if (statSync(caminho).isDirectory()) pilha.push(caminho);
      else if (entrada === "coverage.cobertura.xml")
        encontrados.push({ caminho, mtime: statSync(caminho).mtimeMs });
    }
  }
  // Os diretórios têm nomes GUID — a ordem alfabética não é a das execuções.
  return encontrados.sort((a, b) => a.mtime - b.mtime).map((e) => e.caminho);
}

const dir = process.argv[2] ?? "tests/OficinaDiag.Tests/TestResults";
const ficheiros = encontraCobertura(dir);
if (ficheiros.length === 0) {
  console.error(`coverage-check: nenhum coverage.cobertura.xml em ${dir}`);
  process.exit(1);
}
// O dotnet test corre uma vez por job: o mais recente é o deste run.
const ficheiro = ficheiros[ficheiros.length - 1];
const xml = readFileSync(ficheiro, "utf8");
const raiz = xml.match(/<coverage[^>]*>/);
const linhas = parseFloat(raiz[0].match(/line-rate="([\d.]+)"/)[1]);
const branches = parseFloat(raiz[0].match(/branch-rate="([\d.]+)"/)[1]);
const pct = (x) => (x * 100).toFixed(1) + "%";

console.log(`coverage-check: linhas ${pct(linhas)} (mín ${pct(FLOOR_LINHAS)}), branches ${pct(branches)} (mín ${pct(FLOOR_BRANCHES)})`);

if (process.env.GITHUB_STEP_SUMMARY) {
  appendFileSync(
    process.env.GITHUB_STEP_SUMMARY,
    `### Cobertura (Coverlet)\n\n| | coberto | mínimo |\n|---|---|---|\n| Linhas | ${pct(linhas)} | ${pct(FLOOR_LINHAS)} |\n| Branches | ${pct(branches)} | ${pct(FLOOR_BRANCHES)} |\n\n\`${ficheiro}\`\n`,
  );
}

if (linhas < FLOOR_LINHAS || branches < FLOOR_BRANCHES) {
  console.error("coverage-check: cobertura abaixo do limiar — sobe a suite, nunca baixes o FLOOR.");
  process.exit(1);
}
