import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { JSDOM } from 'jsdom';

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '..', '..');
const wwwroot = path.join(repo, 'src', 'OficinaDiag.Core', 'Tests', 'wwwroot');
const decisionsPath = path.join(wwwroot, 'device-test-decisions.js');
const pagePath = path.join(wwwroot, 'device-test.html');

const D = createRequire(import.meta.url)(decisionsPath);

/** A página real, com as decisões injetadas tal como o TestServer as serve. */
function openPage({ secureContext = false, deviceMotionPermission = false } = {}) {
  const html = readFileSync(pagePath, 'utf8');
  const inline = html.match(/<script>\n([\s\S]*?)<\/script>/)[1];
  const dom = new JSDOM(html.replace('<script src="decisions.js"></script>', ''), {
    url: 'http://192.168.1.50:8765/abc123/',
    runScripts: 'outside-only',
    pretendToBeVisual: true,
  });
  const w = dom.window;
  Object.defineProperty(w, 'isSecureContext', { value: secureContext, configurable: true });
  if (deviceMotionPermission) {
    w.DeviceMotionEvent = { requestPermission: () => Promise.resolve('granted') };
  }
  w.eval(readFileSync(decisionsPath, 'utf8'));
  w.eval(inline);
  return dom;
}

/** Percorre cores → grelha → multi-toque, sem marcar zonas. */
function advanceToSensors(dom) {
  const doc = dom.window.document;
  const stage = doc.getElementById('stage');
  // 13 cores + um toque extra para sair da fase.
  for (let i = 0; i <= D.COLORS.length; i++) stage.onclick();
  doc.getElementById('gridNext').onclick();
  doc.getElementById('mtNext').onclick();
  return doc;
}

test('cores: 13, cinzentos reconhecidos e tinta legível em fundo escuro', () => {
  assert.equal(D.COLORS.length, 13);
  assert.deepEqual(D.COLORS.slice(0, 4), ['#f00', '#0f0', '#00f', '#fff']);
  assert.deepEqual(D.COLORS.slice(8), ['#222', '#555', '#888', '#bbb', '#eee']);

  // Regressão: o padrão antigo esperava dois dígitos (#22) e a página pinta
  // três (#222) — nenhum cinzento era reconhecido e a mensagem de burn-in
  // nunca aparecia.
  for (const cinzento of D.COLORS.slice(8)) assert.equal(D.isGrey(cinzento), true, cinzento);
  assert.equal(D.isGrey('#22'), false);
  for (const cor of ['#f00', '#000', '#fff']) assert.equal(D.isGrey(cor), false, cor);

  for (const escuro of ['#000', '#222', '#555']) assert.equal(D.stageInk(escuro), '#0f0', escuro);
  for (const claro of ['#f00', '#fff', '#eee', '#888']) assert.equal(D.stageInk(claro), '#000', claro);
});

test('grelha: 60 zonas, 28 de borda e o avanço só a partir de 55', () => {
  assert.equal(D.GRID.total, 60);
  assert.equal(D.GRID.cols * D.GRID.rows, D.GRID.total);
  let bordas = 0;
  for (let i = 0; i < D.GRID.total; i++) if (D.isEdgeCell(i, D.GRID.cols, D.GRID.rows)) bordas++;
  // 6 + 6 (topo e fundo) + 8 + 8 (colunas laterais sem os cantos) = 28.
  assert.equal(bordas, 28);
  assert.equal(D.isEdgeCell(7, 6, 10), false);  // canto interior
  assert.equal(D.isEdgeCell(5, 6, 10), true);   // última coluna
  assert.equal(D.shouldAdvanceGrid(54), false);
  assert.equal(D.shouldAdvanceGrid(55), true);
  assert.equal(D.shouldAdvanceGrid(60), true);
});

test('multi-toque: 5 dedos é ok, 1–4 é aviso e 0 é não testado', () => {
  assert.equal(D.multiTouchStatus(5), 'ok');
  assert.equal(D.multiTouchStatus(4), 'warn');
  assert.equal(D.multiTouchStatus(1), 'warn');
  assert.equal(D.multiTouchStatus(0), 'no-data');
  assert.equal(D.multiTouchLabel(5), '5 dedos');
  assert.equal(D.multiTouchLabel(0), 'não testado');
});

test('sensores: permissão do iOS, timeout não apaga leitura e microfone exige contexto seguro', () => {
  const comPermissao = { DeviceMotionEvent: { requestPermission() {} } };
  assert.equal(D.motionPermissionRequired(comPermissao), true);
  assert.equal(D.motionPermissionRequired({ DeviceMotionEvent: {} }), false);
  assert.equal(D.motionPermissionRequired({}), false);

  assert.equal(D.sensorStatusAfterTimeout(undefined), 'no-data');
  assert.equal(D.sensorStatusAfterTimeout('ok'), 'ok');
  assert.equal(D.SENSOR_TIMEOUT_MS, 6000);

  const comMedia = { isSecureContext: true, navigator: { mediaDevices: { getUserMedia() {} } } };
  assert.equal(D.micAvailability(comMedia), 'available');
  assert.equal(D.micAvailability({ ...comMedia, isSecureContext: false }), 'skip');
  assert.equal(D.micAvailability({ isSecureContext: true, navigator: {} }), 'skip');

  // 0.08 é exactamente o limiar: só acima conta como sinal.
  assert.equal(D.micStatusFromPeak(0.08), 'fail');
  assert.equal(D.micStatusFromPeak(0.081), 'ok');
});

test('entrega: 8 tentativas de 1,5 s', () => {
  assert.equal(D.DELIVERY.maxTries, 8);
  assert.equal(D.DELIVERY.delayMs, 1500);
  assert.equal(D.shouldRetryDelivery(1), true);
  assert.equal(D.shouldRetryDelivery(7), true);
  assert.equal(D.shouldRetryDelivery(8), false);
});

test('página: um cinzento mostra a mensagem de burn-in', () => {
  const dom = openPage();
  try {
    const stage = dom.window.document.getElementById('stage');
    for (let i = 0; i < 9; i++) stage.onclick(); // índice 8 = #222, o primeiro cinzento
    assert.match(stage.innerHTML, /burn-in/);
  } finally {
    dom.window.close();
  }
});

test('página: as 13 cores e um toque viram grelha de 60 zonas com 28 de borda', () => {
  const dom = openPage();
  try {
    const doc = dom.window.document;
    const stage = doc.getElementById('stage');
    for (let i = 0; i <= D.COLORS.length; i++) stage.onclick();

    assert.equal(stage.style.display, 'none');
    assert.equal(doc.getElementById('grid').style.display, 'grid');
    const cells = doc.querySelectorAll('#grid .cell');
    assert.equal(cells.length, 60);
    assert.equal(doc.querySelectorAll('#grid .cell.edge').length, 28);
    assert.equal(doc.getElementById('gridCount').textContent, '0/60 zonas');
  } finally {
    dom.window.close();
  }
});

test('página: resultado entregue leva as decisões (mic skip sem https, multi-toque não testado)', async () => {
  const dom = openPage();
  const chamadas = [];
  try {
    const doc = advanceToSensors(dom);
    dom.window.fetch = (url, opts) => {
      chamadas.push({ url, body: JSON.parse(opts.body) });
      return Promise.resolve({ ok: true });
    };
    doc.getElementById('endBtn').onclick();
    await new Promise((r) => setTimeout(r, 0));

    assert.equal(chamadas.length, 1);
    assert.equal(chamadas[0].url, 'result');
    const p = chamadas[0].body;
    assert.equal(p.colorsViewed, 13);
    assert.equal(p.touchTotal, 60);
    assert.equal(p.touchHit, 0);
    assert.equal(p.multiTouch, 0);
    assert.equal(p.sensors.mic, 'skip');
    assert.equal(p.sensors.multitouch, 'no-data');
    assert.equal(p.maxTouchPoints, 0);
  } finally {
    dom.window.close();
  }
});

/**
 * Conta os setTimeout que a página agenda e permite disparar já os longos
 * (os 6 s dos sensores), sem esperar os 6 s reais.
 */
function espiarTimers(dom) {
  const atrasos = [];
  const longos = [];
  const original = dom.window.setTimeout.bind(dom.window);
  dom.window.setTimeout = (fn, ms, ...rest) => {
    atrasos.push(ms);
    if (ms >= 6000) longos.push(fn);
    return original(fn, ms, ...rest);
  };
  return { atrasos, longos, dispararLongos: () => longos.forEach((fn) => fn()) };
}

test('página: no iOS os 6 s só contam a partir do gesto de permissão', async () => {
  const dom = openPage({ deviceMotionPermission: true });
  try {
    const timers = espiarTimers(dom);
    const doc = advanceToSensors(dom);

    assert.equal(doc.getElementById('permBtn').style.display, 'block');
    // Regressão: startSensors chamava armSensorTimeouts() sem condição, logo
    // o timeout de 6 s corria com o diálogo de permissão ainda aberto e o
    // iPhone ficava com "sem leitura" em sensores que ainda nem tinham sido
    // autorizados.
    assert.equal(dom.window.__sensorTimersArmed, undefined);
    assert.deepEqual(timers.atrasos.filter((ms) => ms >= 6000), []);

    doc.getElementById('permBtn').onclick();
    await new Promise((r) => setTimeout(r, 0));

    assert.equal(dom.window.__sensorTimersArmed, true);
    assert.equal(timers.longos.length, 1);
    assert.equal(doc.getElementById('permBtn').style.display, 'none');
  } finally {
    dom.window.close();
  }
});

test('página: iPhone sem permissão de movimento entrega payload sem accel/gyro/orient', async () => {
  // O técnico chega aos sensores e termina sem tocar em PERMITIR SENSORES —
  // passado mais de 6 s, que é quando o timeout marcaria "sem leitura" num
  // sensor que nunca foi autorizado.
  const dom = openPage({ deviceMotionPermission: true });
  const chamadas = [];
  try {
    const timers = espiarTimers(dom);
    const doc = advanceToSensors(dom); // não toca no permBtn
    assert.deepEqual(timers.atrasos.filter((ms) => ms >= 6000), []);
    timers.dispararLongos();          // nada a disparar: nenhum timer armado
    dom.window.fetch = (url, opts) => {
      chamadas.push(JSON.parse(opts.body));
      return Promise.resolve({ ok: true });
    };
    doc.getElementById('endBtn').onclick();
    await new Promise((r) => setTimeout(r, 0));

    assert.equal(chamadas.length, 1);
    const sensors = chamadas[0].sensors;
    // O contrato do TestResultMapper: chave ausente não vira linha no
    // relatório (nem "sem leitura"); só o que foi mesmo medido vem.
    assert.ok(!('accel' in sensors), `veio accel=${sensors.accel}`);
    assert.ok(!('gyro' in sensors), `veio gyro=${sensors.gyro}`);
    assert.ok(!('orient' in sensors), `veio orient=${sensors.orient}`);
    assert.equal(sensors.light, 'unavailable'); // medido: browser não expõe
    assert.equal(sensors.mic, 'skip');         // HTTP LAN, não é hardware
    assert.equal(sensors.multitouch, 'no-data');
  } finally {
    dom.window.close();
  }
});

test('página: sem permissão a pedir, os 6 s começam logo', () => {
  const dom = openPage();
  try {
    const timers = espiarTimers(dom);
    advanceToSensors(dom);
    assert.equal(dom.window.__sensorTimersArmed, true);
    assert.equal(timers.longos.length, 1);

    timers.dispararLongos();
    assert.equal(dom.window.document.getElementById('s-accel').textContent, 'sem leitura');
  } finally {
    dom.window.close();
  }
});
