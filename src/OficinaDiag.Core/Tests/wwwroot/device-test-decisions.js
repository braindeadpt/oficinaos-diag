/* Decisões puras da página de teste on-device (ecrã, toque, sensores).
 *
 * A página é servida ao telemóvel pelo TestServer e não tem build step: isto é
 * um script clássico que define um global (e `module.exports` em Node, para o
 * jsdom o poder carregar sem duplicar código). Tudo o que aqui vive é o que se
 * pode provar sem hardware — limiares, códigos de estado e política de retry.
 * O que fica na página é só DOM: pintar, ouvir eventos, escrever etiquetas.
 */
(function (root) {
  'use strict';

  /* Cores cheias (pixeis mortos) + cinzentos (burn-in / uniformidade). */
  var COLORS = ['#f00', '#0f0', '#00f', '#fff', '#000', '#f0f', '#ff0', '#0ff',
                '#222', '#555', '#888', '#bbb', '#eee'];

  /* Cinzentos: o texto muda para procurar manchas em vez de pixeis mortos.
   * Três dígitos, como as cores de COLORS — com o padrão antigo (#22) isto
   * nunca casava e a mensagem de burn-in não aparecia em #222/#555/#888/
   * #bbb/#eee.
   */
  var GREY = /^#(?:222|555|888|bbb|eee)$/;

  /* Fundos escuros precisam de texto verde; os claros de texto preto. */
  var DARK = { '#000': true, '#222': true, '#555': true };

  /* Grelha 6x10 = 60 zonas; 55 é o mínimo para dar a grelha por coberta, mas
   * o botão "não consigo cobrir tudo" existe porque um ecrã com zonas mortas
   * nunca chega lá. */
  var GRID = { cols: 6, rows: 10, total: 60, advanceAt: 55 };

  /* Sem leitura em 6 s = sensor não exposto (ou sem permissão). */
  var SENSOR_TIMEOUT_MS = 6000;

  /* Pico do microfone acima disto conta como sinal; abaixo é silêncio. */
  var MIC_PEAK_OK = 0.08;

  /* Entrega do resultado ao PC: 8 tentativas, 1,5 s entre cada. */
  var DELIVERY = { maxTries: 8, delayMs: 1500 };

  var SENSOR_OK = 'ok';
  var SENSOR_UNAVAILABLE = 'unavailable';
  var SENSOR_NO_DATA = 'no-data';

  function isGrey(color) { return GREY.test(color); }

  function stageInk(color) { return DARK[color] ? '#0f0' : '#000'; }

  /* Bordas contam à parte: são as primeiras a falhar num ecrã caído. */
  function isEdgeCell(index, cols, rows) {
    var col = index % cols, row = Math.floor(index / cols);
    return col === 0 || col === cols - 1 || row === 0 || row === rows - 1;
  }

  function shouldAdvanceGrid(hitCount) { return hitCount >= GRID.advanceAt; }

  /* Dedos simultâneos vistos: 5 é o mínimo para multi-toque; 0 = saltou a
   * fase, que não é falha — sai como não testado. */
  function multiTouchStatus(fingers) {
    return fingers >= 5 ? SENSOR_OK : fingers > 0 ? 'warn' : SENSOR_NO_DATA;
  }

  function multiTouchLabel(fingers) {
    return fingers > 0 ? fingers + ' dedos' : 'não testado';
  }

  /* iOS 13+ só entrega movimento depois de um gesto + diálogo. */
  function motionPermissionRequired(scope) {
    return typeof scope.DeviceMotionEvent !== 'undefined'
      && typeof scope.DeviceMotionEvent.requestPermission === 'function';
  }

  /* O timeout só marca o que ainda não tem leitura — nunca apaga um "ok". */
  function sensorStatusAfterTimeout(current) { return current || SENSOR_NO_DATA; }

  /* Sensores cujo timeout de 6 s pode marcar "no-data": com o movimento
     recusado no iOS, accel/gyro/orient nunca produzem leitura — as chaves
     ficam de fora do payload (o mapper trata ausente como «não medido»,
     sem linha). O light não depende dessa permissão e mantém o timeout. */
  function armedSensorKeys(motionDenied) {
    return motionDenied ? ['light'] : ['accel', 'gyro', 'orient', 'light'];
  }

  /* Microfone precisa de contexto seguro (https ou 127.0.0.1 via adb reverse). */
  function micAvailability(scope) { return mediaAvailability(scope); }

  /* Câmaras partilham o requisito do microfone: contexto seguro +
     getUserMedia. Em HTTP LAN (Wi-Fi) o browser não expõe — 'skip', e a
     página nunca deixa uma permissão recusada virar 'fail'. */
  function mediaAvailability(scope) {
    var nav = scope.navigator;
    return scope.isSecureContext && nav && nav.mediaDevices
      && typeof nav.mediaDevices.getUserMedia === 'function' ? 'available' : 'skip';
  }

  /* facingMode no getUserMedia é PREFERÊNCIA, não garantia: num telemóvel
     com uma só câmara o browser abre a que existe, mesmo pedindo a outra.
     Aprovar a pedida era falso positivo — o lado que conta é o que abriu.
     getSettings().facingMode ('user'/'environment') é a fonte; o label do
     track é o fallback. Devolve 'front'|'back'|null (null = indeterminável,
     a página fica com o lado pedido — não há como fazer melhor). */
  function cameraSideFromTrack(track) {
    if (!track) return null;
    var s = typeof track.getSettings === 'function' ? track.getSettings() : null;
    var fm = s && s.facingMode;
    if (fm === 'user' || fm === 'environment') return fm === 'user' ? 'front' : 'back';
    var label = (track.label || '').toLowerCase();
    if (/back|rear|traseir|environment|trasera|trás/.test(label)) return 'back';
    if (/front|user|facetime|selfie/.test(label)) return 'front';
    return null;
  }

  function micStatusFromPeak(peak) { return peak > MIC_PEAK_OK ? SENSOR_OK : 'fail'; }

  /* ── testes avançados (câmara, GPU, GPS, brilho, taxa de toque) ── */

  /* GPU: a API existir mas o contexto falhar é defeito de GPU/driver;
     a API nem existir é limitação do browser → 'skip'. */
  var GPU_STRESS_MS = 3000;
  var GPU_MIN_FPS = 5;
  function gpuApiAvailable(scope) {
    return typeof scope.WebGLRenderingContext !== 'undefined'
        || typeof scope.WebGL2RenderingContext !== 'undefined';
  }
  /* ~3 s de stress: menos de 5 fps = RAF estrangulado (poupança de
     bateria, segundo plano) — aviso, não falha: throttling não é defeito. */
  function gpuStatusFromRender(contextLost, frames, elapsedMs) {
    if (contextLost) return 'fail';
    if (typeof elapsedMs !== 'number' || !(elapsedMs > 0)) return 'skip';
    var fps = frames / (elapsedMs / 1000);
    return fps >= GPU_MIN_FPS ? SENSOR_OK : 'warn';
  }

  /* GPS: os 3 códigos padrão (1 permissão, 2 indisponível, 3 timeout) são
     ambiente — «interior sem sinal» não é defeito. Um código fora do
     padrão é a stack de geolocalização a reportar algo anómalo → 'fail'. */
  var GPS_TIMEOUT_MS = 8000;
  function gpsStatusFromError(code) {
    return code === 1 || code === 2 || code === 3 ? 'skip' : 'fail';
  }

  /* Brilho: screen.brightness é um dos APIs menos implementados da web —
     a maioria dos telemóveis devolve null → 'skip' honesto. */
  function brightnessReading(scope) {
    var b = scope.screen && scope.screen.brightness;
    if (typeof b === 'number') return b;
    if (b && typeof b.value === 'number') return b.value;
    return null;
  }

  /* Taxa de amostragem de toque: PointerEvent.getCoalescedEvents expõe os
     eventos que o browser fundiu — existe em Chrome/Android e Safari
     moderno; onde falta → 'skip'. Acima de 1000 Hz a medida é impossível
     (bug de contagem) — ignora. */
  var TOUCH_HZ_OK = 60;
  var TOUCH_HZ_MAX = 1000;
  function touchSampleStatus(hz) {
    if (typeof hz !== 'number' || !isFinite(hz) || hz <= 0 || hz > TOUCH_HZ_MAX)
      return 'skip';
    return hz >= TOUCH_HZ_OK ? SENSOR_OK : 'warn';
  }

  /* `tries` já conta a tentativa feita; 8 é o teto de envios. */
  function shouldRetryDelivery(tries) { return tries < DELIVERY.maxTries; }

  var api = {
    COLORS: COLORS,
    GRID: GRID,
    SENSOR_TIMEOUT_MS: SENSOR_TIMEOUT_MS,
    MIC_PEAK_OK: MIC_PEAK_OK,
    DELIVERY: DELIVERY,
    SENSOR_OK: SENSOR_OK,
    SENSOR_UNAVAILABLE: SENSOR_UNAVAILABLE,
    SENSOR_NO_DATA: SENSOR_NO_DATA,
    isGrey: isGrey,
    stageInk: stageInk,
    isEdgeCell: isEdgeCell,
    shouldAdvanceGrid: shouldAdvanceGrid,
    multiTouchStatus: multiTouchStatus,
    multiTouchLabel: multiTouchLabel,
    motionPermissionRequired: motionPermissionRequired,
    sensorStatusAfterTimeout: sensorStatusAfterTimeout,
    armedSensorKeys: armedSensorKeys,
    micAvailability: micAvailability,
    micStatusFromPeak: micStatusFromPeak,
    mediaAvailability: mediaAvailability,
    cameraSideFromTrack: cameraSideFromTrack,
    GPU_STRESS_MS: GPU_STRESS_MS,
    GPU_MIN_FPS: GPU_MIN_FPS,
    gpuApiAvailable: gpuApiAvailable,
    gpuStatusFromRender: gpuStatusFromRender,
    GPS_TIMEOUT_MS: GPS_TIMEOUT_MS,
    gpsStatusFromError: gpsStatusFromError,
    brightnessReading: brightnessReading,
    TOUCH_HZ_OK: TOUCH_HZ_OK,
    TOUCH_HZ_MAX: TOUCH_HZ_MAX,
    touchSampleStatus: touchSampleStatus,
    shouldRetryDelivery: shouldRetryDelivery
  };

  root.DeviceTestDecisions = api;
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);
