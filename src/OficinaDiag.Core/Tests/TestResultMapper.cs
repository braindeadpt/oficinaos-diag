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
        if (data.TryGetValue("touchHit", out var hit) && data.TryGetValue("touchTotal", out var tot))
        {
            var h = hit.GetInt32(); var t = tot.GetInt32();
            report.Set("test.touch", h >= t - 5 ? "pass" : "fail",
                L10n.F("test.touch.zones", h, t), L10n.T("test.touch.detail"));
        }
        report.Set("test.screen", "pass", L10n.T("test.screen.val"),
            L10n.T("test.screen.detail"));

        // Bordas: falha de toque nas margens é o sintoma clássico de
        // ecrã aftermarket / mal ligado — contado à parte do interior.
        if (data.TryGetValue("edgeHit", out var eh) && data.TryGetValue("edgeTotal", out var et))
        {
            var eH = eh.GetInt32(); var eT = et.GetInt32();
            if (eT > 0)
                report.Set("test.edges", eH >= eT ? "pass" : "warn",
                    L10n.F("test.edges.val", eH, eT),
                    eH >= eT ? L10n.T("test.edges.ok")
                             : L10n.T("test.edges.fail"));
        }

        // Multi-toque real (dedos simultâneos vistos na fase 3) — mais
        // informativo que o maxTouchPoints anunciado.
        if (data.TryGetValue("multiTouch", out var mt) && mt.GetInt32() > 0)
            report.Set("sensor.multitouch",
                mt.GetInt32() >= 5 ? "pass" : "warn",
                L10n.F("test.multitouch.val", mt.GetInt32()),
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
                if (!sensors.TryGetProperty(key, out var s)) continue;
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
    }
}
