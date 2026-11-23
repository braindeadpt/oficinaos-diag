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

/**
 * Payload de referência do iPhone sem permissão de movimento. O mesmo ficheiro
 * é lido pelo TestResultMapperTests (C#): se a página mudar o que envia ou o
 * mapeador mudar o que espera, os dois lados veem a mesma verdade.
 */
const fixtureSemPermissao = JSON.parse(
  readFileSync(path.join(here, 'fixtures', 'ios-sem-permissao.json'), 'utf8'));

/** A página real, com as decisões injetadas tal como o TestServer as serve. */
function openPage({ secureContext = false, deviceMotionPermission = false } = {}) {
  const html = readFileSync(pagePath, 'utf8');
  const inline = html.match(/<script>\r?\n([\s\S]*?)<\/script>/)[1];
  const dom = new JSDOM(html.replace('<script src="decisions.js"></script>', ''), {
    url: 'http://192.168.1.50:8765/abc123/',
    runScripts: 'outside-only',
    pretendToBeVisual: true,
  });
  const w = dom.window;
  Object.defineProperty(w, 'isSecureContext', { value: secureContext, configurable: true });
  if (deviceMotionPermission) {
    w.DeviceMotionEvent = {
      requestPermission: () => deviceMotionPermission === 'denied'
        ? Promise.reject(new Error('denied'))
        : Promise.resolve('granted'),
    };
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

/** Percorre tudo até à fase de testes avançados (câmara, GPU, GPS…). */
function advanceToAdvanced(dom) {
  const doc = advanceToSensors(dom);
  doc.getElementById('advBtn').onclick();
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

  // Movimento recusado: o timeout só arma o que não depende da permissão —
  // accel/gyro/orient ficam de fora («não medido», sem linha no relatório).
  assert.deepEqual(D.armedSensorKeys(false), ['accel', 'gyro', 'orient', 'light']);
  assert.deepEqual(D.armedSensorKeys(true), ['light']);

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
    const doc = advanceToAdvanced(dom);
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
    // O multi-toque vai só no `multiTouch` do topo: o mapper lê dali, e um
    // `sensors.multitouch` era dado morto. Fixado pelo fixture partilhado.
    assert.ok(!('multitouch' in p.sensors));
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
    const doc = advanceToAdvanced(dom); // não toca no permBtn
    assert.deepEqual(timers.atrasos.filter((ms) => ms >= 6000), []);
    timers.dispararLongos();          // nada a disparar: nenhum timer armado
    dom.window.fetch = (url, opts) => {
      chamadas.push(JSON.parse(opts.body));
      return Promise.resolve({ ok: true });
    };
    doc.getElementById('endBtn').onclick();
    await new Promise((r) => setTimeout(r, 0));

    assert.equal(chamadas.length, 1);
    const payload = chamadas[0];
    const sensors = payload.sensors;

    // O contrato do TestResultMapper: chave ausente não vira linha no
    // relatório (nem "sem leitura"); só o que foi mesmo medido vem.
    assert.ok(!('accel' in sensors), `veio accel=${sensors.accel}`);
    assert.ok(!('gyro' in sensors), `veio gyro=${sensors.gyro}`);
    assert.ok(!('orient' in sensors), `veio orient=${sensors.orient}`);

    // E o payload inteiro é o fixture partilhado (menos o userAgent, que
    // carrega a versão do jsdom e o sistema onde os testes correm).
    const { userAgent, ...estavel } = payload;
    assert.equal(typeof userAgent, 'string');
    assert.deepEqual(estavel, fixtureSemPermissao);
  } finally {
    dom.window.close();
  }
});

test('página: iPhone que recusa o diálogo de permissão entrega payload sem accel/gyro/orient', async () => {
  // Diferente de nunca tocar no botão: aqui o técnico toca em PERMITIR
  // SENSORES e escolhe «Não Permitir» no diálogo do iOS. Os timeouts eram
  // armados na mesma e os sensores saíam "no-data" → linhas «sem leitura»
  // no relatório, violando o contrato do fixture partilhado.
  const dom = openPage({ deviceMotionPermission: 'denied' });
  const chamadas = [];
  try {
    const timers = espiarTimers(dom);
    const doc = advanceToAdvanced(dom);
    assert.equal(doc.getElementById('permBtn').style.display, 'block');

    doc.getElementById('permBtn').onclick();
    await new Promise((r) => setTimeout(r, 0));

    assert.equal(dom.window.__motionDenied, true);
    assert.equal(doc.getElementById('s-accel').textContent, 'sem permissão');
    assert.equal(doc.getElementById('s-gyro').textContent, 'sem permissão');
    assert.equal(doc.getElementById('s-orient').textContent, 'sem permissão');

    timers.dispararLongos();
    assert.equal(doc.getElementById('s-accel').textContent, 'sem permissão'); // não vira «sem leitura»

    dom.window.fetch = (url, opts) => {
      chamadas.push(JSON.parse(opts.body));
      return Promise.resolve({ ok: true });
    };
    doc.getElementById('endBtn').onclick();
    await new Promise((r) => setTimeout(r, 0));

    const sensors = chamadas[0].sensors;
    assert.ok(!('accel' in sensors), `veio accel=${sensors.accel}`);
    assert.ok(!('gyro' in sensors), `veio gyro=${sensors.gyro}`);
    assert.ok(!('orient' in sensors), `veio orient=${sensors.orient}`);
    // light não depende da permissão de movimento — continua a ser medido.
    assert.equal(sensors.light, 'unavailable');
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

test('decisões avançadas: média partilhada, GPU, GPS, brilho e taxa de toque', () => {
  // mediaAvailability = getUserMedia + contexto seguro — partilhada por
  // microfone e câmaras (micAvailability delega nela).
  const comMedia = { isSecureContext: true, navigator: { mediaDevices: { getUserMedia() {} } } };
  assert.equal(D.mediaAvailability(comMedia), 'available');
  assert.equal(D.mediaAvailability({ ...comMedia, isSecureContext: false }), 'skip');
  assert.equal(D.micAvailability(comMedia), 'available');

  // Câmara: facingMode é preferência — o lado que conta é o do track real
  // (getSettings().facingMode, label como fallback, null = indeterminável).
  assert.equal(D.cameraSideFromTrack(null), null);
  assert.equal(D.cameraSideFromTrack({}), null);
  assert.equal(D.cameraSideFromTrack({ getSettings: () => ({ facingMode: 'user' }) }), 'front');
  assert.equal(D.cameraSideFromTrack({ getSettings: () => ({ facingMode: 'environment' }) }), 'back');
  assert.equal(D.cameraSideFromTrack({ getSettings: () => ({}), label: 'camera2 0, facing back' }), 'back');
  assert.equal(D.cameraSideFromTrack({ label: 'Front Camera' }), 'front');
  assert.equal(D.cameraSideFromTrack({ label: 'Integrated Camera' }), null);

  // GPU: a decisão é sobre o render, não sobre a API (gpuApiAvailable é
  // testada na página — aqui ficam os limiares). O contexto que a API
  // não cria é 'noctx' na página → skipped; 'fail' é só pós-criação.
  assert.equal(D.gpuApiAvailable({}), false);
  assert.equal(D.gpuApiAvailable({ WebGLRenderingContext: function () {} }), true);
  assert.equal(D.gpuApiAvailable({ WebGL2RenderingContext: function () {} }), true);
  assert.equal(D.gpuStatusFromRender(false, 120, 3000), 'ok');    // 40 fps
  assert.equal(D.gpuStatusFromRender(true, 120, 3000), 'fail');   // contexto perdido
  assert.equal(D.gpuStatusFromRender(false, 10, 3000), 'warn');   // ~3 fps
  assert.equal(D.gpuStatusFromRender(false, 0, 0), 'skip');       // sem decurso

  // GPS: os 3 códigos padrão são ambiente; fora do padrão é falha.
  assert.equal(D.gpsStatusFromError(1), 'skip');
  assert.equal(D.gpsStatusFromError(2), 'skip');
  assert.equal(D.gpsStatusFromError(3), 'skip');
  assert.equal(D.gpsStatusFromError(0), 'fail');

  // Brilho: screen.brightness raramente existe — null = skip.
  assert.equal(D.brightnessReading({ screen: {} }), null);
  assert.equal(D.brightnessReading({}), null);
  assert.equal(D.brightnessReading({ screen: { brightness: 0.5 } }), 0.5);
  assert.equal(D.brightnessReading({ screen: { brightness: { value: 0.8 } } }), 0.8);

  // Taxa de toque: ≥60 Hz ok, abaixo warn, fora de gama skip. O gap que
  // fecha um segmento de movimento (dedo parado) é 200 ms.
  assert.equal(D.TOUCH_GAP_MS, 200);
  assert.equal(D.touchSampleStatus(120), 'ok');
  assert.equal(D.touchSampleStatus(60), 'ok');
  assert.equal(D.touchSampleStatus(59), 'warn');
  assert.equal(D.touchSampleStatus(null), 'skip');
  assert.equal(D.touchSampleStatus(5000), 'skip'); // impossível — medida viciada
});

test('página: fase avançada sem APIs fica toda skipped', async () => {
  const dom = openPage();
  const chamadas = [];
  try {
    const doc = advanceToAdvanced(dom);
    // jsdom não tem WebGLRenderingContext, geolocation, mediaDevices nem
    // screen.brightness — tudo o que a página mede sai 'skip', nunca fail.
    dom.window.fetch = (url, opts) => {
      chamadas.push(JSON.parse(opts.body));
      return Promise.resolve({ ok: true });
    };
    doc.getElementById('endBtn').onclick();
    await new Promise((r) => setTimeout(r, 0));

    const p = chamadas[0];
    assert.equal(p.cameraFront, 'skip');
    assert.equal(p.cameraBack, 'skip');
    assert.equal(p.gpu, 'skip');
    assert.equal(p.gpuFps, null);
    assert.equal(p.gps, 'skip');
    assert.equal(p.gpsAccuracyM, null);
    assert.equal(p.brightness, 'skip');
    assert.equal(p.brightnessValue, null);
    assert.equal(p.touchSample, 'skip');
    assert.equal(p.touchHz, null);
  } finally {
    dom.window.close();
  }
});

test('página: câmara confirmada vira ok e a faixa é libertada', async () => {
  const dom = openPage({ secureContext: true });
  const chamadas = [];
  try {
    const w = dom.window;
    const tracks = [{ stop() { this.stopped = true; } }];
    // facingMode 'user' = frontal — o lado pedido e o real coincidem.
    const videoTrack = { getSettings: () => ({ facingMode: 'user' }) };
    Object.defineProperty(w.navigator, 'mediaDevices', {
      value: { getUserMedia: () => Promise.resolve({
        getTracks: () => tracks,
        getVideoTracks: () => [videoTrack],
      }) },
      configurable: true,
    });
    const doc = advanceToAdvanced(dom);
    doc.getElementById('camFBtn').onclick();
    await new Promise((r) => setTimeout(r, 0));

    assert.equal(doc.getElementById('camStage').style.display, 'block');
    assert.equal(doc.getElementById('a-camf').textContent, 'a confirmar…');
    doc.getElementById('camOk').onclick();

    w.fetch = (url, opts) => {
      chamadas.push(JSON.parse(opts.body));
      return Promise.resolve({ ok: true });
    };
    doc.getElementById('endBtn').onclick();
    await new Promise((r) => setTimeout(r, 0));

    assert.equal(chamadas[0].cameraFront, 'ok');
    assert.equal(chamadas[0].cameraBack, 'skip'); // nunca testada
    assert.equal(tracks[0].stopped, true); // stopCam correu no confirmar
  } finally {
    dom.window.close();
  }
});

test('página: câmara sem permissão fica skip — nunca fail', async () => {
  const dom = openPage({ secureContext: true });
  const chamadas = [];
  try {
    const w = dom.window;
    Object.defineProperty(w.navigator, 'mediaDevices', {
      value: { getUserMedia: () => Promise.reject(new Error('denied')) },
      configurable: true,
    });
    const doc = advanceToAdvanced(dom);
    doc.getElementById('camBBtn').onclick();
    await new Promise((r) => setTimeout(r, 0));

    assert.equal(doc.getElementById('a-camb').textContent, 'sem permissão/dispositivo');
    assert.equal(doc.getElementById('camStage').style.display, 'none');

    w.fetch = (url, opts) => {
      chamadas.push(JSON.parse(opts.body));
      return Promise.resolve({ ok: true });
    };
    doc.getElementById('endBtn').onclick();
    await new Promise((r) => setTimeout(r, 0));
    assert.equal(chamadas[0].cameraBack, 'skip');
  } finally {
    dom.window.close();
  }
});

test('página: câmara confirmada como má vira fail', async () => {
  const dom = openPage({ secureContext: true });
  const chamadas = [];
  try {
    const w = dom.window;
    Object.defineProperty(w.navigator, 'mediaDevices', {
      value: { getUserMedia: () => Promise.resolve({
        getTracks: () => [{ stop() {} }],
        getVideoTracks: () => [{ getSettings: () => ({ facingMode: 'user' }) }],
      }) },
      configurable: true,
    });
    const doc = advanceToAdvanced(dom);
    doc.getElementById('camFBtn').onclick();
    await new Promise((r) => setTimeout(r, 0));
    doc.getElementById('camBad').onclick();

    w.fetch = (url, opts) => {
      chamadas.push(JSON.parse(opts.body));
      return Promise.resolve({ ok: true });
    };
    doc.getElementById('endBtn').onclick();
    await new Promise((r) => setTimeout(r, 0));
    assert.equal(chamadas[0].cameraFront, 'fail');
  } finally {
    dom.window.close();
  }
});

test('página: browser abre a câmara do outro lado — aprova o lado que abriu', async () => {
  // Regressão Devin Review: facingMode é preferência, não garantia. Pedir
  // 'back' num telemóvel com uma só câmara abre a frontal — aprovar a
  // pedida era falso positivo. O lado real vem de getSettings().facingMode.
  const dom = openPage({ secureContext: true });
  const chamadas = [];
  try {
    const w = dom.window;
    Object.defineProperty(w.navigator, 'mediaDevices', {
      value: { getUserMedia: () => Promise.resolve({
        getTracks: () => [{ stop() {} }],
        getVideoTracks: () => [{ getSettings: () => ({ facingMode: 'user' }) }],
      }) },
      configurable: true,
    });
    const doc = advanceToAdvanced(dom);
    doc.getElementById('camBBtn').onclick(); // pede traseira
    await new Promise((r) => setTimeout(r, 0));

    // A pedida sai 'skip' (indisponível); a confirmação escreve no lado real.
    assert.equal(doc.getElementById('a-camb').textContent, 'câmara pedida indisponível');
    assert.equal(doc.getElementById('a-camf').textContent, 'a confirmar…');
    doc.getElementById('camOk').onclick();

    w.fetch = (url, opts) => {
      chamadas.push(JSON.parse(opts.body));
      return Promise.resolve({ ok: true });
    };
    doc.getElementById('endBtn').onclick();
    await new Promise((r) => setTimeout(r, 0));
    assert.equal(chamadas[0].cameraFront, 'ok');   // a que abriu de facto
    assert.equal(chamadas[0].cameraBack, 'skip');  // a pedida, indisponível
  } finally {
    dom.window.close();
  }
});

test('página: pedido de câmara pendente é invalidado no finish', async () => {
  // Regressão Devin Review: um getUserMedia lento não pode abrir stream com
  // o painel já escondido — finish() tem de invalidar o pedido (camReq).
  const dom = openPage({ secureContext: true });
  try {
    const w = dom.window;
    let resolver;
    const tracks = [{ stop() { this.stopped = true; } }];
    Object.defineProperty(w.navigator, 'mediaDevices', {
      value: { getUserMedia: () => new Promise((r) => { resolver = r; }) },
      configurable: true,
    });
    w.fetch = () => Promise.resolve({ ok: true });
    const doc = advanceToAdvanced(dom);
    doc.getElementById('camFBtn').onclick(); // pedido fica pendente
    doc.getElementById('endBtn').onclick();   // finish invalida camReq

    // O browser resolve DEPOIS do finish — o stream morre na hora.
    resolver({
      getTracks: () => tracks,
      getVideoTracks: () => [{ getSettings: () => ({ facingMode: 'user' }) }],
    });
    await new Promise((r) => setTimeout(r, 0));

    assert.equal(tracks[0].stopped, true);
    assert.equal(doc.getElementById('camStage').style.display, 'none');
  } finally {
    dom.window.close();
  }
});

test('página: GPS com fix vira ok com precisão; erro padrão vira skip', async () => {
  const dom = openPage();
  const chamadas = [];
  try {
    const w = dom.window;
    Object.defineProperty(w.navigator, 'geolocation', {
      value: { getCurrentPosition: (ok) => ok({ coords: { accuracy: 23 } }) },
      configurable: true,
    });
    const doc = advanceToAdvanced(dom);
    doc.getElementById('gpsBtn').onclick();
    await new Promise((r) => setTimeout(r, 0));
    // O check prova geolocalização (GPS/Wi-Fi/rede), não o chip GPS.
    assert.equal(doc.getElementById('a-gps').textContent, '±23 m');

    w.fetch = (url, opts) => {
      chamadas.push(JSON.parse(opts.body));
      return Promise.resolve({ ok: true });
    };
    doc.getElementById('endBtn').onclick();
    await new Promise((r) => setTimeout(r, 0));
    assert.equal(chamadas[0].gps, 'ok');
    assert.equal(chamadas[0].gpsAccuracyM, 23);
  } finally {
    dom.window.close();
  }
});

test('página: GPS sem permissão fica skip; erro fora do padrão fica fail', async () => {
  const dom = openPage();
  const chamadas = [];
  try {
    const w = dom.window;
    Object.defineProperty(w.navigator, 'geolocation', {
      value: { getCurrentPosition: (ok, err) => err({ code: 1 }) },
      configurable: true,
    });
    const doc = advanceToAdvanced(dom);
    doc.getElementById('gpsBtn').onclick();
    await new Promise((r) => setTimeout(r, 0));
    assert.equal(doc.getElementById('a-gps').textContent, 'sem fix');

    // Erro fora dos 3 códigos padrão = stack de geolocalização anómala.
    Object.defineProperty(w.navigator, 'geolocation', {
      value: { getCurrentPosition: (ok, err) => err({ code: 0 }) },
      configurable: true,
    });
    doc.getElementById('gpsBtn').onclick();
    await new Promise((r) => setTimeout(r, 0));

    w.fetch = (url, opts) => {
      chamadas.push(JSON.parse(opts.body));
      return Promise.resolve({ ok: true });
    };
    doc.getElementById('endBtn').onclick();
    await new Promise((r) => setTimeout(r, 0));
    assert.equal(chamadas[0].gps, 'fail');
  } finally {
    dom.window.close();
  }
});

test('página: WebGL API presente mas contexto não criado → gpu noctx (skipped)', async () => {
  // Regressão Devin Review: a interface existir ≠ contexto criável. getContext
  // a devolver null é bloqueio da plataforma (blocklist, política, poupança)
  // indistinguível de avaria — nunca 'fail', sai 'noctx' → skipped.
  const dom = openPage();
  const chamadas = [];
  try {
    // gpuApiAvailable passa; o canvas.getContext do jsdom devolve null —
    // o mesmo que um browser a recusar o contexto num telemóvel real.
    dom.window.WebGLRenderingContext = function () {};
    const doc = advanceToAdvanced(dom); // showAdvanced já correu runGpuTest
    assert.equal(doc.getElementById('a-gpu').textContent, 'contexto não criado');

    dom.window.fetch = (url, opts) => {
      chamadas.push(JSON.parse(opts.body));
      return Promise.resolve({ ok: true });
    };
    doc.getElementById('endBtn').onclick();
    await new Promise((r) => setTimeout(r, 0));
    assert.equal(chamadas[0].gpu, 'noctx');
  } finally {
    dom.window.close();
  }
});

/** Intervalo de timeStamps — o que cada PointerEvent coalesced real traz. */
function sampleTimes(from, to, step) {
  const out = [];
  for (let t = from; t <= to; t += step) out.push(t);
  return out;
}

/** Despacha um pointer event; `samples` = timeStamps das amostras do lote
 *  (undefined = sem getCoalescedEvents — pointerdown/up não precisam). */
function pointer(w, type, t, samples) {
  const ev = new w.Event(type);
  Object.defineProperty(ev, 'timeStamp', { value: t });
  if (samples !== undefined) {
    ev.getCoalescedEvents = () => samples.map((ts) => ({ timeStamp: ts }));
  }
  w.document.dispatchEvent(ev);
}

/** Abre a página, alimenta eventos de toque e devolve o payload do finish. */
async function measureTouch(events) {
  const dom = openPage();
  const doc = advanceToSensors(dom); // o sampler está ativo desde o início
  const w = dom.window;
  const chamadas = [];
  w.fetch = (url, opts) => {
    chamadas.push(JSON.parse(opts.body));
    return Promise.resolve({ ok: true });
  };
  for (const [type, t, samples] of events) pointer(w, type, t, samples);
  doc.getElementById('advBtn').onclick();
  doc.getElementById('endBtn').onclick();
  await new Promise((r) => setTimeout(r, 0));
  return { payload: chamadas[0], doc, close: () => dom.window.close() };
}

test('página: taxa de toque — dedo parado e pausas não entram no denominador', async () => {
  // Regressão Devin Review r2: dedo parado não gera amostras e não pode
  // diluir a taxa. Dois gestos a 200 Hz com holds longos entre eles.
  const r = await measureTouch([
    ['pointerdown', 0],
    ['pointermove', 1095, sampleTimes(1000, 1095, 5)],   // 20 amostras a 200 Hz
    ['pointerup', 5000],                                  // dedo parado 4 s — fora
    ['pointerdown', 10000],
    ['pointermove', 10095, sampleTimes(10000, 10095, 5)],
    ['pointerup', 10500],
    ['pointermove', 11000, sampleTimes(10500, 10595, 5)], // hover — ignorado
  ]);
  try {
    // 2 segmentos × 19 intervalos / 95 ms → 38 intervalos / 190 ms = 200 Hz.
    assert.equal(r.payload.touchHz, 200);
    assert.equal(r.payload.touchSample, 'ok');
    assert.equal(r.doc.getElementById('a-touchhz').textContent, '≈200 Hz');
  } finally {
    r.close();
  }
});

test('página: hold no meio do contacto fecha o segmento — não dilui a taxa', async () => {
  // Regressão Devin Review r2: mover, parar >TOUCH_GAP_MS e voltar a mover
  // são DOIS segmentos de movimento — o tempo imóvel fica fora.
  const r = await measureTouch([
    ['pointerdown', 0],
    ['pointermove', 1095, sampleTimes(1000, 1095, 5)],   // swipe A
    ['pointermove', 6095, sampleTimes(6000, 6095, 5)],   // swipe B — gap >200 ms → split
    ['pointerup', 6500],
  ]);
  try {
    // 2 × (19 intervalos / 95 ms) → 200 Hz; os ~5 s de dedo parado fora.
    assert.equal(r.payload.touchHz, 200);
    assert.equal(r.payload.touchSample, 'ok');
  } finally {
    r.close();
  }
});

test('página: gesto a ~55 Hz → warn, com e sem agrupamento coalesced', async () => {
  // Regressão Devin Review r3: N amostras têm N−1 intervalos — dividir N
  // pela janela aprovava ~1 amostra a mais (12 amostras/198 ms → 61 Hz
  // 'ok' em vez de 56 Hz 'warn'). E agrupar amostras noutro dispatch não
  // pode mudar a medida: a taxa usa os timestamps do lote, não os do evento.
  const lento = sampleTimes(1000, 1198, 18); // 12 amostras, ~55,5 Hz
  // Sem agrupamento: 1 amostra por pointermove.
  const semLote = await measureTouch([
    ['pointerdown', 0],
    ...lento.map((t) => ['pointermove', t, [t]]),
    ['pointerup', 1250],
  ]);
  // Com agrupamento: 4 dispatches carregam as mesmas 12 amostras.
  const comLote = await measureTouch([
    ['pointerdown', 0],
    ['pointermove', 1036, lento.slice(0, 3)],
    ['pointermove', 1090, lento.slice(3, 6)],
    ['pointermove', 1144, lento.slice(6, 9)],
    ['pointermove', 1198, lento.slice(9)],
    ['pointerup', 1250],
  ]);
  try {
    for (const r of [semLote, comLote]) {
      // 11 intervalos / 198 ms = 55,5 Hz → 'warn' — na versão anterior
      // (N/janela) dava 12/198 ≈ 61 Hz → 'ok': a falsa aprovação.
      assert.equal(r.payload.touchHz, 56);
      assert.equal(r.payload.touchSample, 'warn');
    }
  } finally {
    semLote.close();
    comLote.close();
  }
});
