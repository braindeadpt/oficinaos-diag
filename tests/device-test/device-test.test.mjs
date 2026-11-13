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

test('página: com requestPermission mostra o botão e arma os timeouts na mesma', async () => {
  const dom = openPage({ deviceMotionPermission: true });
  try {
    const doc = advanceToSensors(dom);
    assert.equal(doc.getElementById('permBtn').style.display, 'block');

    // Bug conhecido preservado: o comentário acima do permBtn diz que os
    // timeouts de 6 s só correm depois da permissão, mas startSensors chama
    // armSensorTimeouts() sem condição — num iPhone o diálogo pode estar
    // aberto quando os sensores passam a "sem leitura". O teste fixa o que a
    // página faz hoje; corrigir isto é mudar comportamento (T-05).
    assert.equal(dom.window.__sensorTimersArmed, true);

    doc.getElementById('permBtn').onclick();
    await new Promise((r) => setTimeout(r, 0));
    assert.equal(doc.getElementById('permBtn').style.display, 'none');
  } finally {
    dom.window.close();
  }
});
