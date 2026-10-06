using System.Globalization;

namespace OficinaDiag;

/// <summary>
/// UI strings PT/EN. Report-detail strings produced by the collectors stay PT —
/// they land in the HTML/PDF reports sent to the (PT-market) shop.
/// Btn()/Section() add terminal-only chrome ([ X ], ── ──) so the same label
/// reads natively in the other themes.
/// </summary>
public static class L10n
{
    public static string Current { get; private set; } = "pt";

    /// <summary>Fired when the language changes — open windows re-apply strings.</summary>
    public static event Action? Changed;

    /// <summary>OS language → default app language (pt unless English OS).</summary>
    public static string AutoDetect() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "en" ? "en" : "pt";

    public static void Set(string code)
    {
        code = code is "en" ? "en" : "pt";
        if (code == Current) return;
        Current = code;
        Changed?.Invoke();
    }

    public static string T(string key) =>
        D[Current].TryGetValue(key, out var s) ? s
        : D["pt"].TryGetValue(key, out s) ? s
        : key;

    public static string F(string key, params object?[] args) =>
        string.Format(CultureInfo.InvariantCulture, T(key), args);

    /// <summary>Button caption — terminal theme wraps it in [ ].</summary>
    public static string Btn(string key) =>
        ThemeManager.IsTerminal ? $"[ {T(key)} ]" : T(key);

    /// <summary>Section header — terminal theme draws ── dashes.</summary>
    public static string Section(string key) =>
        ThemeManager.IsTerminal ? $"── {T(key)} ────────────────" : T(key).ToUpperInvariant();

    private static readonly Dictionary<string, Dictionary<string, string>> D = new()
    {
        ["pt"] = new()
        {
            // chrome — janela principal
            ["status.watching"] = "a vigiar USB…",
            ["status.detected"] = "{0} detetado",
            ["results.header"] = "RESULTADOS",
            ["btn.scan"] = "SCAN",
            ["btn.bench"] = "SCAN+",
            ["btn.test"] = "ECRÃ+TOQUE",
            ["btn.export"] = "EXPORTAR",
            ["btn.send"] = "→LOJA (PRO)",
            ["btn.ai"] = "IA (PRO)",
            ["btn.insurance"] = "SEGURADORA",
            ["btn.update"] = "↓ ATUALIZAR {0}",
            ["btn.log"] = "ENVIAR LOG",
            ["btn.settings"] = "⚙",
            ["btn.checklist"] = "CHECKLIST",
            ["btn.compare"] = "ANTES/DEPOIS",
            ["btn.label"] = "ETIQUETA",
            ["btn.client"] = "CLIENTE",

            // log consola
            ["log.screen"] = "> ecrã útil {0}×{1} · janela {2}×{3} @{4},{5}",
            ["log.boot"] = "> boot sequence…",
            ["log.tagline"] = "> oficinaos-diag — scan USB grátis · relatórios Pro via cloud",
            ["log.adbmissing"] = "! adb não encontrado em tools\\platform-tools — corre tools\\fetch-tools.ps1",
            ["log.attached"] = "> dispositivo ligado: {0} [{1}]",
            ["log.detached"] = "> dispositivo removido: {0}",
            ["log.update.avail"] = "> nova versão {0} disponível — carrega ATUALIZAR",
            ["log.update.dl"] = "> a descarregar {0}…",
            ["log.update.fail"] = "! atualização falhou: {0}",
            ["log.scan.run"] = "> scan a correr…",
            ["log.nodev"] = "! nenhum dispositivo — liga um telefone por USB (ADB debug / Confiar)",
            ["log.nodev.usb"] = "! nenhum dispositivo — liga um telefone por USB",
            ["log.scan.done"] = "> scan completo: {0} checks",
            ["log.err"] = "! erro: {0}",
            ["log.test.received"] = "> resultados do teste no telefone recebidos (ecrã, toque, sensores)",
            ["log.test.url"] = "> teste de ecrã em {0}",
            ["log.test.android"] = "> página de teste aberta no Android — segue as instruções no telefone",
            ["log.test.qr"] = "> QR mostrado — lê com a câmara do iPhone na mesma rede Wi-Fi",
            ["log.test.fail"] = "! teste falhou: {0}",
            ["log.bench.saved"] = "> SCAN+ gravado: {0} · {1} amostras",
            ["log.export.saved"] = "> relatório guardado: {0}",
            ["log.ins.saved"] = "> relatório de seguradora guardado: {0}",
            ["log.send.start"] = "> a enviar à loja {0}…",
            ["log.send.fail"] = "! envio falhou: {0}",
            ["log.log.none"] = "! ainda não há log para enviar",
            ["log.log.truncated"] = "[… início cortado …]",
            ["log.log.sending"] = "> a enviar log de diagnóstico à cloud…",
            ["log.log.fail"] = "! envio do log falhou: {0}",
            ["log.ai.gen"] = "> a gerar relatório IA…",
            ["log.ai.fail"] = "! IA falhou: {0}",
            ["log.ai.saved"] = "> relatório IA guardado: {0}",
            ["log.grade"] = "> grau {0} — {1}",
            ["log.checklist.saved"] = "> checklist físico gravado no relatório",
            ["log.cmp.none"] = "! sem scans anteriores deste serial — o antes/depois precisa de um scan antigo",
            ["log.cmp.noserial"] = "! relatório sem serial — o antes/depois precisa de identificar o aparelho",
            ["log.label.printed"] = "> etiqueta enviada para a impressora",
            ["log.label.cancel"] = "> impressão da etiqueta cancelada",
            ["log.client.saved"] = "> relatório de cliente guardado: {0}",

            // resultados do teste no telefone (escritos no relatório via UI)
            ["test.touch.zones"] = "{0}/{1} zonas",
            ["test.touch.detail"] = "grelha de toque preenchida no telefone",
            ["test.screen.val"] = "cores ok",
            ["test.screen.detail"] = "utilizador confirmou as cores — dead pixels seriam visíveis",
            ["sensor.accel"] = "acelerómetro",
            ["sensor.gyro"] = "giroscópio",
            ["sensor.orient"] = "orientação",
            ["sensor.light"] = "luz ambiente",
            ["sensor.multitouch"] = "multi-toque",
            ["sensor.responding"] = "a responder",
            ["sensor.nodata"] = "sem leitura",
            ["sensor.noapi"] = "browser não expõe",
            ["sensor.nodata.detail"] = "o browser não devolveu dados do {0} — re-testar ou verificar hardware",
            ["sensor.reading.detail"] = "leitura via browser ({0})",
            ["sensor.vibrate.ok"] = "vibrou",
            ["sensor.vibrate.ok.detail"] = "utilizador confirmou vibração no telefone",
            ["sensor.vibrate.fail"] = "não vibrou",
            ["sensor.vibrate.fail.detail"] = "utilizador não sentiu vibração — verificar motor",

            // diálogos de ficheiro / InputBox IA
            ["dlg.export.filter"] = "Relatório HTML|*.html|JSON|*.json",
            ["dlg.ins.filter"] = "Relatório seguradora|*.html",
            ["dlg.ai.filter"] = "Relatório IA|*.html",
            ["dlg.client.filter"] = "Relatório cliente|*.html",
            ["dlg.ai.token"] = "Token da loja (oficinaos-cloud):",
            ["dlg.ai.token.title"] = "Relatório IA — PRO",
            ["dlg.ai.lang"] = "Idioma do relatório (pt/en/fr/es):",
            ["dlg.token.remember"] = "guardar token neste PC (cifrado)",
            ["dlg.ai.privacy"] = "O relatório IA envia os dados do scan — incl. logs de crash com paths e nomes de utilizador — para a cloud/LLM. Continuar?",
            ["set.token"] = "token Pro (relatórios IA)",
            ["set.token.set"] = "guardado — deixa vazio para manter, escreve para trocar",
            ["set.token.unset"] = "vazio — pedido a cada relatório IA",
            ["set.err.url"] = "URL inválido — usa um endereço completo tipo https://cloud.oficinaos.app",
            ["set.warn.http"] = "http:// sem encriptação — o token e dados do cliente viajam em claro na rede. Continuar mesmo assim?",

            // bancada (SCAN+)
            ["bench.title"] = "OFICINA-OS // BANCADA",
            ["bench.header"] = "BANCADA — sessão de potência",
            ["bench.instr.idle"] = "cabo ligado ao PC · telemóvel quieto · carrega INICIAR",
            ["bench.phase1"] = "fase 1/3 — base: deixa o telemóvel quieto (a carregar do cabo)",
            ["bench.phase2.auto"] = "fase 2/3 — a aplicar carga: ecrã ligado + brilho máximo",
            ["bench.phase2.ios"] = "fase 2/3 — LIGA o ecrã do iPhone e ABRE a Câmara",
            ["bench.phase3.auto"] = "fase 3/3 — a retirar carga: observa a recuperação",
            ["bench.phase3.ios"] = "fase 3/3 — desliga o ecrã e deixa o telemóvel quieto",
            ["bench.done"] = "sessão completa",
            ["bench.verdict.ir"] = " · resistência interna ~{0} mΩ",
            ["bench.verdict.sag"] = " · queda {0} mV sob carga",
            ["bench.aborted"] = "! sessão abortada: {0}",
            ["bench.elapsed"] = "{0}s · restam {1}s",
            ["bench.legend.temp"] = "— temp",
            ["btn.start"] = "INICIAR",
            ["btn.saveclose"] = "GRAVAR+FECHAR",
            ["btn.close"] = "FECHAR",

            // enviar à loja
            ["send.title"] = "Enviar à loja",
            ["send.header"] = "ENVIAR DIAGNÓSTICO À LOJA",
            ["send.desc"] = "A loja deu-te um código de 6 letras (ex.: 54424B). Os teus dados ficam associados ao pedido na app da loja.",
            ["send.code"] = "código da loja *",
            ["send.name"] = "o teu nome *",
            ["send.phone"] = "o teu telefone *",
            ["send.email"] = "email (opcional)",
            ["send.purpose"] = "motivo do envio",
            ["send.purpose.repair"] = "Reparação — a loja avalia o problema",
            ["send.purpose.sale"] = "Vender o telemóvel — pedir orçamento à loja",
            ["btn.send.ok"] = "ENVIAR",
            ["btn.cancel"] = "cancelar",
            ["send.err.code"] = "Falta o código da loja (6 letras).",
            ["send.err.contact"] = "Precisamos do teu nome e telefone — a loja usa-os para te identificar.",

            // seguradora
            ["ins.title"] = "Relatório para seguradora",
            ["ins.header"] = "RELATÓRIO PARA SEGURADORA",
            ["ins.desc"] = "Documento formal (A4) com o estado do equipamento — para o cliente entregar à seguradora. Imprime ou grava em PDF no browser.",
            ["ins.insured"] = "segurado (nome do cliente) *",
            ["ins.insurer"] = "seguradora",
            ["ins.policy"] = "nº apólice / processo de sinistro",
            ["ins.damage"] = "dano reportado *",
            ["ins.repair"] = "reparação proposta",
            ["ins.repair.tip"] = "ex.: Substituição de conjunto ecrã + junta de vedação",
            ["ins.cost"] = "custo estimado (€)",
            ["ins.cost.tip"] = "ex.: 89,90",
            ["ins.tech"] = "técnico responsável",
            ["ins.shopsec"] = "dados da loja (ficam guardados)",
            ["ins.shopname"] = "nome da loja *",
            ["ins.shopnif"] = "NIF da loja",
            ["ins.shopphone"] = "telefone da loja",
            ["ins.shopaddr"] = "morada da loja",
            ["btn.generate"] = "GERAR",
            ["ins.err.insured"] = "Falta o nome do segurado.",
            ["ins.err.damage"] = "Descreve o dano — é o que a seguradora pede primeiro.",
            ["ins.err.shop"] = "O nome da loja é o carimbo do relatório — obrigatório.",

            // checklist físico
            ["chk.title"] = "Checklist físico",
            ["chk.header"] = "CHECKLIST FÍSICO — o que o USB não vê",
            ["chk.desc"] = "Marca cada item em segundos: ok · defeito · n/t (não testado). Entra no relatório e pesa no grau.",
            ["chk.notes"] = "notas do técnico (entram no relatório)",
            ["chk.photo"] = "ANEXAR FOTOS",
            ["chk.photos"] = "{0} foto(s) anexada(s)",
            ["chk.fail"] = "defeito",
            ["chk.na"] = "n/t",
            ["btn.apply"] = "APLICAR",

            // antes / depois
            ["cmp.title"] = "Antes / depois",
            ["cmp.header"] = "ANTES / DEPOIS — mesmo serial",
            ["cmp.desc"] = "Escolhe um scan anterior para comparar com o atual — prova ao cliente que a peça nova ficou bem.",
            ["cmp.col.check"] = "verificação",
            ["cmp.col.before"] = "antes",
            ["cmp.col.now"] = "agora",

            // QR popup
            ["qr.text"] = "LÊ COM A CÂMARA DO TELEMÓVEL",

            // opções
            ["set.title"] = "Opções",
            ["set.header"] = "OPÇÕES",
            ["set.lang"] = "idioma",
            ["set.theme"] = "tema",
            ["set.cloud"] = "URL OficinaOS Cloud",
            ["btn.save"] = "GUARDAR",
            ["theme.terminal"] = "Terminal (fósforo)",
            ["theme.win95"] = "Windows 95",
            ["theme.fluent"] = "Moderno",
        },
        ["en"] = new()
        {
            ["status.watching"] = "watching USB…",
            ["status.detected"] = "{0} detected",
            ["results.header"] = "RESULTS",
            ["btn.scan"] = "SCAN",
            ["btn.bench"] = "SCAN+",
            ["btn.test"] = "SCREEN+TOUCH",
            ["btn.export"] = "EXPORT",
            ["btn.send"] = "→SHOP (PRO)",
            ["btn.ai"] = "AI (PRO)",
            ["btn.insurance"] = "INSURER",
            ["btn.update"] = "↓ UPDATE {0}",
            ["btn.log"] = "SEND LOG",
            ["btn.settings"] = "⚙",
            ["btn.checklist"] = "CHECKLIST",
            ["btn.compare"] = "BEFORE/AFTER",
            ["btn.label"] = "LABEL",
            ["btn.client"] = "CUSTOMER",

            ["log.screen"] = "> work area {0}×{1} · window {2}×{3} @{4},{5}",
            ["log.boot"] = "> boot sequence…",
            ["log.tagline"] = "> oficinaos-diag — free USB scan · Pro reports via cloud",
            ["log.adbmissing"] = "! adb not found in tools\\platform-tools — run tools\\fetch-tools.ps1",
            ["log.attached"] = "> device attached: {0} [{1}]",
            ["log.detached"] = "> device removed: {0}",
            ["log.update.avail"] = "> new version {0} available — press UPDATE",
            ["log.update.dl"] = "> downloading {0}…",
            ["log.update.fail"] = "! update failed: {0}",
            ["log.scan.run"] = "> scan running…",
            ["log.nodev"] = "! no device — connect a phone via USB (ADB debug / Trust)",
            ["log.nodev.usb"] = "! no device — connect a phone via USB",
            ["log.scan.done"] = "> scan complete: {0} checks",
            ["log.err"] = "! error: {0}",
            ["log.test.received"] = "> phone test results received (screen, touch, sensors)",
            ["log.test.url"] = "> screen test at {0}",
            ["log.test.android"] = "> test page opened on Android — follow the instructions on the phone",
            ["log.test.qr"] = "> QR shown — scan with the iPhone camera on the same Wi-Fi",
            ["log.test.fail"] = "! test failed: {0}",
            ["log.bench.saved"] = "> SCAN+ saved: {0} · {1} samples",
            ["log.export.saved"] = "> report saved: {0}",
            ["log.ins.saved"] = "> insurer report saved: {0}",
            ["log.send.start"] = "> sending to shop {0}…",
            ["log.send.fail"] = "! send failed: {0}",
            ["log.log.none"] = "! no log to send yet",
            ["log.log.truncated"] = "[… beginning truncated …]",
            ["log.log.sending"] = "> sending diagnostic log to cloud…",
            ["log.log.fail"] = "! log send failed: {0}",
            ["log.ai.gen"] = "> generating AI report…",
            ["log.ai.fail"] = "! AI failed: {0}",
            ["log.ai.saved"] = "> AI report saved: {0}",
            ["log.grade"] = "> grade {0} — {1}",
            ["log.checklist.saved"] = "> physical checklist saved to the report",
            ["log.cmp.none"] = "! no previous scans for this serial — before/after needs an older scan",
            ["log.cmp.noserial"] = "! report has no serial — before/after needs to identify the device",
            ["log.label.printed"] = "> label sent to the printer",
            ["log.label.cancel"] = "> label print cancelled",
            ["log.client.saved"] = "> customer report saved: {0}",

            ["test.touch.zones"] = "{0}/{1} zones",
            ["test.touch.detail"] = "touch grid filled on the phone",
            ["test.screen.val"] = "colours ok",
            ["test.screen.detail"] = "user confirmed colours — dead pixels would be visible",
            ["sensor.accel"] = "accelerometer",
            ["sensor.gyro"] = "gyroscope",
            ["sensor.orient"] = "orientation",
            ["sensor.light"] = "ambient light",
            ["sensor.multitouch"] = "multi-touch",
            ["sensor.responding"] = "responding",
            ["sensor.nodata"] = "no reading",
            ["sensor.noapi"] = "browser doesn't expose",
            ["sensor.nodata.detail"] = "browser returned no {0} data — re-test or check hardware",
            ["sensor.reading.detail"] = "reading via browser ({0})",
            ["sensor.vibrate.ok"] = "vibrated",
            ["sensor.vibrate.ok.detail"] = "user confirmed phone vibration",
            ["sensor.vibrate.fail"] = "didn't vibrate",
            ["sensor.vibrate.fail.detail"] = "user felt no vibration — check motor",

            ["dlg.export.filter"] = "HTML report|*.html|JSON|*.json",
            ["dlg.ins.filter"] = "Insurer report|*.html",
            ["dlg.ai.filter"] = "AI report|*.html",
            ["dlg.client.filter"] = "Customer report|*.html",
            ["dlg.ai.token"] = "Shop token (oficinaos-cloud):",
            ["dlg.ai.token.title"] = "AI report — PRO",
            ["dlg.ai.lang"] = "Report language (pt/en/fr/es):",
            ["dlg.token.remember"] = "remember token on this PC (encrypted)",
            ["dlg.ai.privacy"] = "The AI report sends the scan data — including crash logs with paths and usernames — to the cloud/LLM. Continue?",
            ["set.token"] = "Pro token (AI reports)",
            ["set.token.set"] = "saved — leave empty to keep, type to replace",
            ["set.token.unset"] = "empty — asked on each AI report",
            ["set.err.url"] = "Invalid URL — use a full address like https://cloud.oficinaos.app",
            ["set.warn.http"] = "http:// without encryption — the token and customer data travel in cleartext. Continue anyway?",

            ["bench.title"] = "OFICINA-OS // BENCH",
            ["bench.header"] = "BENCH — power session",
            ["bench.instr.idle"] = "cable to PC · keep the phone still · press START",
            ["bench.phase1"] = "phase 1/3 — baseline: keep the phone still (charging from cable)",
            ["bench.phase2.auto"] = "phase 2/3 — applying load: screen on + max brightness",
            ["bench.phase2.ios"] = "phase 2/3 — turn the iPhone screen ON and open the Camera",
            ["bench.phase3.auto"] = "phase 3/3 — removing load: watch the recovery",
            ["bench.phase3.ios"] = "phase 3/3 — turn the screen off and keep the phone still",
            ["bench.done"] = "session complete",
            ["bench.verdict.ir"] = " · internal resistance ~{0} mΩ",
            ["bench.verdict.sag"] = " · {0} mV sag under load",
            ["bench.aborted"] = "! session aborted: {0}",
            ["bench.elapsed"] = "{0}s · {1}s left",
            ["bench.legend.temp"] = "— temp",
            ["btn.start"] = "START",
            ["btn.saveclose"] = "SAVE+CLOSE",
            ["btn.close"] = "CLOSE",

            ["send.title"] = "Send to shop",
            ["send.header"] = "SEND DIAGNOSTIC TO SHOP",
            ["send.desc"] = "The shop gave you a 6-letter code (e.g. 54424B). Your details are attached to the request in the shop's app.",
            ["send.code"] = "shop code *",
            ["send.name"] = "your name *",
            ["send.phone"] = "your phone *",
            ["send.email"] = "email (optional)",
            ["send.purpose"] = "reason",
            ["send.purpose.repair"] = "Repair — the shop assesses the issue",
            ["send.purpose.sale"] = "Sell the phone — ask the shop for a quote",
            ["btn.send.ok"] = "SEND",
            ["btn.cancel"] = "cancel",
            ["send.err.code"] = "Shop code missing (6 letters).",
            ["send.err.contact"] = "We need your name and phone — the shop uses them to identify you.",

            ["ins.title"] = "Insurer report",
            ["ins.header"] = "INSURER REPORT",
            ["ins.desc"] = "Formal (A4) document with the device state — for the customer to hand to the insurer. Print or save as PDF in the browser.",
            ["ins.insured"] = "insured (customer name) *",
            ["ins.insurer"] = "insurer",
            ["ins.policy"] = "policy no. / claim ref",
            ["ins.damage"] = "reported damage *",
            ["ins.repair"] = "proposed repair",
            ["ins.repair.tip"] = "e.g.: Screen assembly + seal gasket replacement",
            ["ins.cost"] = "estimated cost (€)",
            ["ins.cost.tip"] = "e.g.: 89.90",
            ["ins.tech"] = "technician",
            ["ins.shopsec"] = "shop details (saved)",
            ["ins.shopname"] = "shop name *",
            ["ins.shopnif"] = "shop VAT / tax ID",
            ["ins.shopphone"] = "shop phone",
            ["ins.shopaddr"] = "shop address",
            ["btn.generate"] = "GENERATE",
            ["ins.err.insured"] = "Insured name missing.",
            ["ins.err.damage"] = "Describe the damage — it's the first thing the insurer asks for.",
            ["ins.err.shop"] = "Shop name is the report's stamp — required.",

            ["chk.title"] = "Physical checklist",
            ["chk.header"] = "PHYSICAL CHECKLIST — what USB can't see",
            ["chk.desc"] = "Mark each item in seconds: ok · defect · n/a (not tested). It goes into the report and weighs on the grade.",
            ["chk.notes"] = "technician notes (go in the report)",
            ["chk.photo"] = "ATTACH PHOTOS",
            ["chk.photos"] = "{0} photo(s) attached",
            ["chk.fail"] = "defect",
            ["chk.na"] = "n/a",
            ["btn.apply"] = "APPLY",

            ["cmp.title"] = "Before / after",
            ["cmp.header"] = "BEFORE / AFTER — same serial",
            ["cmp.desc"] = "Pick a previous scan to compare with the current one — prove to the customer the new part is good.",
            ["cmp.col.check"] = "check",
            ["cmp.col.before"] = "before",
            ["cmp.col.now"] = "now",

            ["qr.text"] = "SCAN WITH THE PHONE CAMERA",

            ["set.title"] = "Settings",
            ["set.header"] = "SETTINGS",
            ["set.lang"] = "language",
            ["set.theme"] = "theme",
            ["set.cloud"] = "OficinaOS Cloud URL",
            ["btn.save"] = "SAVE",
            ["theme.terminal"] = "Terminal (phosphor)",
            ["theme.win95"] = "Windows 95",
            ["theme.fluent"] = "Modern",
        },
    };
}
