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
  function micAvailability(scope) {
    var nav = scope.navigator;
    return scope.isSecureContext && nav && nav.mediaDevices
      && typeof nav.mediaDevices.getUserMedia === 'function' ? 'available' : 'skip';
  }

  function micStatusFromPeak(peak) { return peak > MIC_PEAK_OK ? SENSOR_OK : 'fail'; }

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
    shouldRetryDelivery: shouldRetryDelivery
  };

  root.DeviceTestDecisions = api;
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);
