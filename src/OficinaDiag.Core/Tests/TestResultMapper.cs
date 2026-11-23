using System.Text.Json;
using OficinaDiag.Devices;

namespace OficinaDiag.Tests;

/// <summary>
/// Traduz o JSON que a página on-device devolve (POST result) nas linhas do
/// relatório. É regra, não UI: vive no Core para poder ser testada, sobretudo o
/// contrato dos sensores — <c>no-data</c> vira <c>warn</c>, <c>unavailable</c>
/// vira <c>skipped</c> e uma chave <b>ausente</b> não cria linha nenhuma (é o
/// que acontece no iOS quando o técnico não concede a permissão de movimento).
/// </summary>
public static class TestResultMapper
{
    public static void Apply(DeviceReport report, IReadOnlyDictionary<string, JsonElement> data)
    {
        // O payload vem de um POST na LAN — TryGet* em vez de Get*: um tipo
        // errado não pode rebentar o mapeamento a meio do relatório.
        if (data.TryGetValue("touchHit", out var hit) && data.TryGetValue("touchTotal", out var tot)
            && hit.ValueKind == JsonValueKind.Number && tot.ValueKind == JsonValueKind.Number
            && hit.TryGetInt32(out var h) && tot.TryGetInt32(out var t))
        {
            report.Set("test.touch", h >= t - 5 ? "pass" : "fail",
                L10n.F("test.touch.zones", h, t), L10n.T("test.touch.detail"));
        }
        // colorsViewed = quantas cores o técnico confirmou na fase de ecrã.
        // 0 = a fase foi saltada — marcar «pass» era um falso positivo.
        // Chave ausente (payload antigo/malformado) mantém o «pass» de antes.
        var colors = data.TryGetValue("colorsViewed", out var cv)
            && cv.ValueKind == JsonValueKind.Number ? cv.GetInt32() : -1;
        report.Set("test.screen",
            colors == 0 ? "skipped" : "pass",
            colors == 0 ? L10n.T("test.screen.skip") : L10n.T("test.screen.val"),
            L10n.T("test.screen.detail"));

        // Bordas: falha de toque nas margens é o sintoma clássico de
        // ecrã aftermarket / mal ligado — contado à parte do interior.
        if (data.TryGetValue("edgeHit", out var eh) && data.TryGetValue("edgeTotal", out var et)
            && eh.ValueKind == JsonValueKind.Number && et.ValueKind == JsonValueKind.Number
            && eh.TryGetInt32(out var eH) && et.TryGetInt32(out var eT))
        {
            if (eT > 0)
                report.Set("test.edges", eH >= eT ? "pass" : "warn",
                    L10n.F("test.edges.val", eH, eT),
                    eH >= eT ? L10n.T("test.edges.ok")
                             : L10n.T("test.edges.fail"));
        }

        // Multi-toque real (dedos simultâneos vistos na fase 3) — mais
        // informativo que o maxTouchPoints anunciado.
        if (data.TryGetValue("multiTouch", out var mt) && mt.ValueKind == JsonValueKind.Number
            && mt.TryGetInt32(out var mtVal) && mtVal > 0)
            report.Set("sensor.multitouch",
                mtVal >= 5 ? "pass" : "warn",
                L10n.F("test.multitouch.val", mtVal),
                L10n.T("test.multitouch.detail"));

        // Altifalante — confirmação humana do tom WebAudio.
        if (data.TryGetValue("speaker", out var spk) && spk.ValueKind == JsonValueKind.String)
        {
            var s = spk.GetString();
            report.Set("sensor.speaker",
                s == "ok" ? "pass" : s == "fail" ? "fail" : "skipped",
                s == "ok" ? L10n.T("test.speaker.ok")
                    : s == "fail" ? L10n.T("test.speaker.fail")
                    : L10n.T("test.speaker.skip"),
                L10n.T("test.speaker.detail"));
        }

        // Sensores — a página mede por browser APIs; "no-data" significa que o
        // browser não respondeu, não que o hardware falhou. Chave ausente (iOS
        // sem permissão de movimento) fica sem linha: não foi medido.
        if (data.TryGetValue("sensors", out var sensors)
            && sensors.ValueKind == JsonValueKind.Object)
        {
            var names = new (string Key, string Label)[]
            {
                ("accel", L10n.T("sensor.accel")), ("gyro", L10n.T("sensor.gyro")),
                ("orient", L10n.T("sensor.orient")), ("light", L10n.T("sensor.light")),
            };
            foreach (var (key, label) in names)
            {
                if (!sensors.TryGetProperty(key, out var s)
                    || s.ValueKind != JsonValueKind.String) continue;
                var state = s.GetString();
                if (state is null) continue;
                report.Set($"sensor.{key}",
                    state == "ok" ? "pass" : state == "no-data" ? "warn" : "skipped",
                    state == "ok" ? L10n.T("sensor.responding")
                        : state == "no-data" ? L10n.T("sensor.nodata")
                        : L10n.T("sensor.noapi"),
                    state == "no-data"
                        ? L10n.F("sensor.nodata.detail", label)
                        : L10n.F("sensor.reading.detail", label));
            }

            // Auricular e microfone: "fail" é defeito, "skip" não (microfone
            // bloqueado em HTTP LAN não é hardware).
            void MapAudio(string key, string okKey, string failKey, string detailKey)
            {
                if (!sensors.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String)
                    return;
                var st = v.GetString();
                report.Set($"sensor.{key}",
                    st == "ok" ? "pass" : st == "fail" ? "fail" : "skipped",
                    st == "ok" ? L10n.T(okKey)
                        : st == "fail" ? L10n.T(failKey) : L10n.T("test.mic.skip"),
                    L10n.T(detailKey));
            }
            MapAudio("earpiece", "test.earpiece.ok", "test.earpiece.fail", "test.earpiece.detail");
            MapAudio("mic", "test.mic.ok", "test.mic.fail", "test.mic.detail");
        }
        if (data.TryGetValue("vibrate", out var vib) && vib.ValueKind == JsonValueKind.String)
        {
            var v = vib.GetString();
            if (v == "ok")
                report.Set("sensor.vibrate", "pass", L10n.T("sensor.vibrate.ok"),
                    L10n.T("sensor.vibrate.ok.detail"));
            else if (v == "no-data")
                report.Set("sensor.vibrate", "fail", L10n.T("sensor.vibrate.fail"),
                    L10n.T("sensor.vibrate.fail.detail"));
        }

        // ── testes avançados (Fase B do plano Diag Pro) ─────────────────
        // 'skip' cobre «não testado», «API não exposta» e «permissão
        // recusada» — no iOS Safari em HTTP LAN quase tudo cai aqui e
        // limitação de plataforma nunca é falha de hardware. Chave ausente
        // (payload de página antiga) não cria linha, como nos sensores.

        // Câmaras — confirmação visual humana; 'fail' só quando o técnico
        // viu a imagem e disse que não presta.
        foreach (var (pk, ck) in new (string Payload, string Check)[]
        {
            ("cameraFront", "test.camera_front"),
            ("cameraBack", "test.camera_back"),
        })
        {
            if (!data.TryGetValue(pk, out var cam)) continue;
            var cs = cam.ValueKind == JsonValueKind.String ? cam.GetString() : null;
            report.Set(ck,
                cs == "ok" ? "pass" : cs == "fail" ? "fail" : "skipped",
                cs == "ok" ? L10n.T("test.camera.ok")
                    : cs == "fail" ? L10n.T("test.camera.fail")
                    : L10n.T("test.camera.skip"),
                L10n.T("test.camera.detail"));
        }

        // GPU — stress WebGL de ~3 s. 'warn' = fps a rasar do mínimo
        // (RAF estrangulado por poupança de bateria, não defeito). 'noctx' =
        // API exposta mas contexto recusado — bloqueio de plataforma
        // indistinguível de avaria, por isso skipped com nota própria. O fps
        // pode faltar num payload malformado — o valor cai num texto simples.
        if (data.TryGetValue("gpu", out var gpu))
        {
            var gs = gpu.ValueKind == JsonValueKind.String ? gpu.GetString() : null;
            var fps = Num(data, "gpuFps");
            report.Set("test.gpu",
                gs == "ok" ? "pass" : gs == "warn" ? "warn"
                    : gs == "fail" ? "fail" : "skipped",
                gs == "ok"
                    ? fps is { } gf ? L10n.F("test.gpu.ok", gf.ToString("0")) : L10n.T("test.gpu.okplain")
                    : gs == "warn"
                        ? fps is { } wf ? L10n.F("test.gpu.warnval", wf.ToString("0")) : L10n.T("test.gpu.warnplain")
                        : gs == "fail" ? L10n.T("test.gpu.fail")
                        : gs == "noctx" ? L10n.T("test.gpu.noctx")
                        : L10n.T("test.gpu.skip"),
                L10n.T("test.gpu.detail"));
        }

        // GPS — um fix; precisão vai no valor. Os erros padrão (permissão,
        // indisponível, timeout) já chegam como 'skip' da página.
        if (data.TryGetValue("gps", out var gps))
        {
            var gs = gps.ValueKind == JsonValueKind.String ? gps.GetString() : null;
            var acc = Num(data, "gpsAccuracyM");
            report.Set("test.gps",
                gs == "ok" ? "pass" : gs == "fail" ? "fail" : "skipped",
                gs == "ok"
                    ? acc is { } a ? L10n.F("test.gps.ok", a.ToString("0")) : L10n.T("test.gps.okplain")
                    : gs == "fail" ? L10n.T("test.gps.fail")
                    : L10n.T("test.gps.skip"),
                L10n.T("test.gps.detail"));
        }

        // Brilho — leitura de screen.brightness; quase nunca exposto em
        // telemóveis, por isso skipped é o resultado normal.
        if (data.TryGetValue("brightness", out var br))
        {
            var bs = br.ValueKind == JsonValueKind.String ? br.GetString() : null;
            var pct = Num(data, "brightnessValue");
            report.Set("test.brightness",
                bs == "ok" ? "pass" : "skipped",
                bs == "ok"
                    ? pct is { } p ? L10n.F("test.brightness.val", p.ToString("0")) : L10n.T("test.brightness.plain")
                    : L10n.T("test.brightness.skip"),
                L10n.T("test.brightness.detail"));
        }

        // Taxa de amostragem de toque — medida por getCoalescedEvents
        // durante as fases de toque; 'warn' = abaixo de 60 Hz.
        if (data.TryGetValue("touchSample", out var ts))
        {
            var tst = ts.ValueKind == JsonValueKind.String ? ts.GetString() : null;
            var hz = Num(data, "touchHz");
            report.Set("test.touchrate",
                tst == "ok" ? "pass" : tst == "warn" ? "warn" : "skipped",
                tst == "ok" || tst == "warn"
                    ? hz is { } hzVal ? L10n.F("test.touchrate.val", hzVal.ToString("0")) : L10n.T("test.touchrate.plain")
                    : L10n.T("test.touchrate.skip"),
                L10n.T(tst == "warn" ? "test.touchrate.warn.detail" : "test.touchrate.detail"));
        }
    }

    /// <summary>Número opcional do payload — tipo errado não rebenta o mapeamento.</summary>
    private static double? Num(IReadOnlyDictionary<string, JsonElement> data, string key) =>
        data.TryGetValue(key, out var v)
        && v.ValueKind == JsonValueKind.Number
        && v.TryGetDouble(out var d) ? d : null;
}
