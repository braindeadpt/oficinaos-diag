#!/usr/bin/env node
// Guarda do <Version> dos csproj contra o tag v* mais recente.
//
// Os builds dev reportam o <Version> do csproj ao updater
// (CheckForUpdateAsync lê Assembly.GetName().Version). Se esse número ficar
// atrás do último release, o updater oferece "actualização" para uma release
// mais velha em features — foi o que aconteceu no incidente #62: um build
// dev 0.3.2 foi oferecido a v0.4.0, que não tinha os merges posteriores.
//
// A regra é >= e não ==: o bump pode entrar antes do tag (csproj 0.5.0 com
// v0.4.0 ainda o último release) — aí builds dev já se dizem 0.5.0 e nunca
// são "actualizados" para trás. Se o tag sair sem bump, o CI fica vermelho
// até um PR de bump entrar — é a pressão que faltava em 2026-11.
//
// Os dois csproj têm de dizer o mesmo: são o mesmo produto.
//
// Uso: node tools/version-check.mjs   (da raiz do repositório)

import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";

const erros = [];

// --- tags v* do remote ----------------------------------------------------
// ls-remote não precisa de fetch nem de histórico: pergunta directamente
// ao origin e funciona em clone shallow como o do CI.
let lsRemote = "";
try {
  lsRemote = execFileSync(
    "git",
    ["ls-remote", "--tags", "--refs", "origin", "v*"],
    { encoding: "utf8" },
  );
} catch (e) {
  erros.push(`git ls-remote --tags origin falhou: ${e.message.split("\n")[0]}`);
}

const tags = [...lsRemote.matchAll(/refs\/tags\/v(\d+)\.(\d+)\.(\d+)\s*$/gm)]
  .map((m) => [Number(m[1]), Number(m[2]), Number(m[3])]);
const ultimo = tags.reduce(
  (max, t) => (max === null || comparar(t, max) > 0 ? t : max),
  null,
);

function comparar(a, b) {
  for (let i = 0; i < 3; i++) {
    if (a[i] !== b[i]) return a[i] - b[i];
  }
  return 0;
}

const fmt = (v) => `v${v.join(".")}`;

// --- <Version> dos csproj --------------------------------------------------
const csproj = [
  "src/OficinaDiag/OficinaDiag.csproj",
  "src/OficinaDiag.Core/OficinaDiag.Core.csproj",
];
const versoes = {};
for (const f of csproj) {
  const m = /<Version>([^<]+)<\/Version>/.exec(readFileSync(f, "utf8"));
  if (!m) {
    erros.push(`${f}: sem <Version> — a guarda não consegue comparar`);
    continue;
  }
  const v = /^(\d+)\.(\d+)\.(\d+)/.exec(m[1].trim());
  if (!v) {
    erros.push(`${f}: <Version>${m[1]}</Version> não é x.y.z — não comparável`);
    continue;
  }
  versoes[f] = [Number(v[1]), Number(v[2]), Number(v[3])];
}

const valores = Object.values(versoes);
if (valores.length === csproj.length && comparar(valores[0], valores[1]) !== 0) {
  erros.push(
    `os csproj divergem: ${csproj[0]}=${valores[0].join(".")} vs ${csproj[1]}=${valores[1].join(".")}`,
  );
}

if (ultimo !== null) {
  for (const f of csproj) {
    const v = versoes[f];
    if (v && comparar(v, ultimo) < 0) {
      erros.push(
        `${f}: <Version>${v.join(".")}</Version> está atrás do último release ${fmt(ultimo)} — ` +
          `builds dev seriam "actualizados" para uma release mais velha em features (#62). ` +
          `Bump o <Version> para >= ${fmt(ultimo)} antes de seguir.`,
      );
    }
  }
}

if (erros.length > 0) {
  for (const e of erros) console.error(`ERRO: ${e}`);
  process.exit(1);
}
console.log(
  `OK: versão dos csproj ${valores.length ? valores[0].join(".") : "?"} >= último tag ${ultimo ? fmt(ultimo) : "(nenhum)"}`,
);
