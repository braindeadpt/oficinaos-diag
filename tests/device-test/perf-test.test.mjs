import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { JSDOM } from 'jsdom';

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '..', '..');
const pagePath = path.join(repo, 'src', 'OficinaDiag.Core', 'Tests', 'wwwroot', 'perf-test.html');

/**
 * Harness da perf-test.html: jsdom + relógio/RAF manuais. O stress real
 * demora minutos — aqui o tempo é falso e os frames são disparados à mão,
 * o que permite simular ecrã oculto (visibilitychange) sem esperar.
 *
 * Cobre as regressões do review: `finished` tem de existir (sem ele o
 * primeiro frame e o watchdog rebentavam e o PC nunca recebia resultado)
 * e o tempo com document.hidden não pode contar como stress.
 */

/** Contexto Canvas2D mínimo — draw2d só usa fillStyle/fillRect/beginPath/arc/fill. */
const fake2d = () => ({
  fillStyle: null,
  fillRect() {}, beginPath() {}, arc() {}, fill() {},
});

/**
 * Abre a página com getContext controlado. `engine`: 'canvas2d' (2d fake,
 * webgl null), 'none' (todos null) ou 'webgl' (gl fake mínimo).
 */
function openPerfPage({ secs = 30, engine = 'canvas2d' } = {}) {
  const html = readFileSync(pagePath, 'utf8');
  const inline = html.match(/<script>\r?\n([\s\S]*?)<\/script>/)[1];
  const dom = new JSDOM(html, {
    url: `http://192.168.1.50:8765/tok123/?secs=${secs}`,
    runScripts: 'outside-only',
    pretendToBeVisual: true,
  });
  const w = dom.window;
  const doc = w.document;

  // Relógio falso: performance.now e os timestamps do RAF vêm da mesma fonte.
  let fakeNow = 0;
  const clock = { now: () => fakeNow };
  Object.defineProperty(w, 'performance', { value: clock, configurable: true });

  // RAF manual: os callbacks ficam numa fila e são disparados pelo teste.
  const pending = [];
  w.requestAnimationFrame = (cb) => { pending.push(cb); return pending.length; };
  w.cancelAnimationFrame = () => {};
  // O watchdog de 1 s não é preciso aqui — os frames conduzem o fim.
  w.setInterval = () => 0;
  w.clearInterval = () => {};

  // fetch: regista chamadas e responde OK — perf-started e perf-result.
  const fetches = [];
  w.fetch = (url, opts) => {
    fetches.push({ url, body: opts?.body ? JSON.parse(opts.body) : null });
    return Promise.resolve({ ok: true });
  };

  // Canvas: webgl* → null; '2d' → fake conforme o engine pedido.
  w.HTMLCanvasElement.prototype.getContext = function (kind) {
    if (kind === '2d' && engine === 'canvas2d') return fake2d();
    if ((kind === 'webgl2' || kind === 'webgl' || kind === 'experimental-webgl') && engine === 'webgl') {
      // GL fake mínimo: compile/link/draw passthrough.
      return {
        VERTEX_SHADER: 1, FRAGMENT_SHADER: 2, COMPILE_STATUS: 3, LINK_STATUS: 4,
        ARRAY_BUFFER: 5, STATIC_DRAW: 6, FLOAT: 7, TRIANGLES: 8,
        createShader: () => ({}), shaderSource() {}, compileShader() {},
        getShaderParameter: () => true,
        createProgram: () => ({}), attachShader() {}, linkProgram() {},
        getProgramParameter: () => true, useProgram() {},
        createBuffer: () => ({}), bindBuffer() {}, bufferData() {},
        getAttribLocation: () => 0, enableVertexAttribArray() {}, vertexAttribPointer() {},
        getUniformLocation: () => ({}), uniform1f() {}, uniform2f() {},
        drawArrays() {}, finish() {}, isContextLost: () => false,
        addEventListener() {},
      };
    }
    return null;
  };

  w.eval(inline);

  /** Avança o relógio e dispara o próximo frame — como o RAF faria.
      Devolve false quando a página já terminou (finish deixa de pedir RAF). */
  function stepTo(ms) {
    fakeNow = ms;
    const cb = pending.shift();
    if (!cb) return false;
    cb(ms);
    return true;
  }

  /** Corre frames de `from` a `to` a ~16 ms — o stress visível normal. */
  function runFrames(from, to) {
    for (let t = from; t < to; t += 16.7) {
      if (!stepTo(t)) break; // finish() chamado — a página já entregou
    }
  }

  function setHidden(hidden) {
    Object.defineProperty(doc, 'hidden', { value: hidden, configurable: true });
    doc.dispatchEvent(new w.Event('visibilitychange'));
  }

  const result = () => fetches.find((f) => f.url === 'perf-result')?.body ?? null;
  const started = () => fetches.some((f) => f.url === 'perf-started');

  return { w, doc, fetches, clock, stepTo, runFrames, setHidden, result, started,
    setNow: (ms) => { fakeNow = ms; } };
}

test('canvas2d: corre o stress e entrega payload com segundos ativos', async () => {
  const p = openPerfPage({ secs: 30, engine: 'canvas2d' });
  assert.ok(p.started(), 'perf-started tinha de disparar no arranque');

  p.runFrames(0, 30_100); // ~30 s ativos a ~60 fps (passa o limite → finish)
  await new Promise((r) => setImmediate(r)); // fetch .then

  const res = p.result();
  assert.ok(res, 'perf-result nunca foi postado');
  assert.equal(res.perfEngine, 'canvas2d');
  assert.ok(res.perfFrames > 1000, `frames a mais: ${res.perfFrames}`);
  assert.ok(res.perfSecs >= 29 && res.perfSecs <= 31, `perfSecs=${res.perfSecs}`);
  assert.equal(res.perfHiddenSecs, 0);
  assert.ok(res.perfFpsAvg > 30, `fpsAvg=${res.perfFpsAvg}`);
});

test('ecrã oculto não conta como stress — perfSecs são segundos ativos', async () => {
  const p = openPerfPage({ secs: 30, engine: 'canvas2d' });

  p.runFrames(0, 10_000);          // 10 s visível
  p.setNow(10_000); p.setHidden(true);
  // 20 s de ecrã bloqueado: o RAF não dispara — só o relógio anda.
  p.setNow(30_000);
  p.setHidden(false);
  p.runFrames(30_000, 50_100);   // +20 s visível = 30 s ativos → finish
  await new Promise((r) => setImmediate(r));

  const res = p.result();
  assert.ok(res, 'perf-result nunca foi postado');
  assert.ok(res.perfSecs >= 29 && res.perfSecs <= 31,
    `perfSecs=${res.perfSecs} — o tempo oculto não pode contar`);
  assert.ok(res.perfHiddenSecs >= 19 && res.perfHiddenSecs <= 21,
    `perfHiddenSecs=${res.perfHiddenSecs}`);
});

test('sem motor de stress reporta engine none honestamente', async () => {
  const p = openPerfPage({ secs: 30, engine: 'none' });
  await new Promise((r) => setImmediate(r));

  assert.ok(p.started(), 'perf-started tinha de disparar mesmo sem motor');
  const res = p.result();
  assert.ok(res, 'perf-result nunca foi postado');
  assert.equal(res.perfEngine, 'none');
  assert.equal(res.perfFrames, 0);
  // O shape mínimo que o TestServer exige: frames e secs numéricos.
  assert.equal(typeof res.perfFrames, 'number');
  assert.equal(typeof res.perfSecs, 'number');
});

test('webgl: engine webgl/canvas2d com frames medidos', async () => {
  // O caminho WebGL usa getContext('webgl2'|'webgl') — com o fake GL o
  // engine reportado é webgl e os frames contam na mesma.
  const p = openPerfPage({ secs: 30, engine: 'webgl' });
  p.runFrames(0, 30_100);
  await new Promise((r) => setImmediate(r));

  const res = p.result();
  assert.ok(res, 'perf-result nunca foi postado');
  assert.ok(res.perfEngine === 'webgl' || res.perfEngine === 'webgl2',
    `engine=${res.perfEngine}`);
});
