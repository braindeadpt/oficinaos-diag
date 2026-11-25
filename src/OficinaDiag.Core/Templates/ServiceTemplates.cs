using OficinaDiag.Devices;
using OficinaDiag.Grading;

namespace OficinaDiag.Templates;

/// <summary>
/// Item do checklist de um template de serviço. Ao contrário do checklist
/// físico (<see cref="ChecklistItem"/>) um item pode ser uma MEDIÇÃO: o
/// técnico escreve o valor lido ("87%", "OEM") e a marca ok/defeito/n-t dá o
/// veredicto — o valor viaja em <see cref="CheckResult.Value"/>.
/// </summary>
/// <param name="Key">Sufixo estável dentro do template — a chave do check fica tpl.&lt;id&gt;.&lt;key&gt;.</param>
/// <param name="Measurement">true = linha com campo de texto para o valor medido.</param>
public sealed record ServiceTemplateItem(
    string Key, string LabelPt, string LabelEn, bool Measurement = false);

/// <summary>
/// Template de serviço — o que o técnico escolhe no início do atendimento.
/// Define o checklist específico do serviço, as medições que o scan tem de
/// trazer (<see cref="RequiredChecks"/>) e o propósito do relatório IA
/// (<see cref="AiPurpose"/>).
/// </summary>
/// <param name="AiPurpose">
/// "sale" | "repair" — os únicos propósitos que a Cloud aceita para além de
/// "maintenance" (ver oficinaos-cloud src/routes/reports.ts). null = relatório
/// de diagnóstico/reparação por defeito; a diferenciação por serviço viaja nos
/// checks tpl.* que entram em results — a Cloud não tem purposes por serviço.
/// </param>
/// <param name="OpensMaintenanceDialog">
/// true = o serviço JÁ existe como fluxo próprio (limpeza medida via adb) —
/// o template não duplica checklist: aplicar abre a janela LIMPEZA.
/// </param>
public sealed record ServiceTemplate(
    string Id,
    string LabelPt,
    string LabelEn,
    string DescriptionPt,
    string DescriptionEn,
    string? AiPurpose,
    IReadOnlyList<ServiceTemplateItem> Items,
    IReadOnlyList<string> RequiredChecks,
    bool OpensMaintenanceDialog = false);

/// <summary>
/// Catálogo de templates de serviço (Fase E.6 do roadmap Diag Pro). Os checks
/// gravados usam o prefixo tpl.&lt;serviço&gt;.* para não colidir com os
/// checks genéricos de scan/checklist — e são preservados no re-scan como
/// qualquer fonte manual (<see cref="DeviceReport.PreserveManualFrom"/>).
/// </summary>
public static class ServiceTemplates
{
    /// <summary>Chave do check que regista que serviço foi aplicado.</summary>
    public const string AppliedKey = "service.template";

    /// <summary>Prefixo dos checks de item de template — ManualPrefixes do DeviceReport.</summary>
    public const string Prefix = "tpl.";

    /// <summary>Id do template de otimização — o mesmo que abre a janela LIMPEZA.</summary>
    public const string MaintenanceId = "maintenance";

    public static readonly ServiceTemplate[] All =
    [
        new("battery",
            "Troca de bateria",
            "Battery replacement",
            "Medições de bateria antes/depois e verificação pós-reparação. O scan traz a leitura inicial (capacidade/estado).",
            "Before/after battery readings and post-repair checks. The scan provides the baseline (capacity/health).",
            AiPurpose: null,
            RequiredChecks: ["battery.capacity", "battery.health"],
            Items:
            [
                new("capacity_before", "Capacidade da bateria ANTES da troca", "Battery capacity BEFORE the swap", Measurement: true),
                new("capacity_after", "Capacidade da bateria DEPOIS da troca", "Battery capacity AFTER the swap", Measurement: true),
                new("cycles_old", "Ciclos da bateria antiga (se lido)", "Old battery cycle count (if read)", Measurement: true),
                new("part_origin", "Origem da peça (OEM / aftermarket / sem marca)", "Part origin (OEM / aftermarket / unbranded)", Measurement: true),
                new("seal", "Selos/adesivos repostos (vidro traseiro)", "Seals/adhesive replaced (back glass)"),
                new("liquid", "Sinais de líquido encontrados à abertura", "Liquid signs found while opening"),
                new("function", "Carga + arranque + Bluetooth OK após a troca", "Charging + boot + Bluetooth OK after the swap"),
            ]),
        new("screen",
            "Troca de ecrã",
            "Screen replacement",
            "Verificação pós-montagem do ecrã — toque, imagem, biometria e selos. Corre o ECRÃ+TOQUE depois da reparação.",
            "Post-assembly screen checks — touch, image, biometrics and seals. Run SCREEN+TOUCH after the repair.",
            AiPurpose: null,
            RequiredChecks: ["test.touch", "test.screen"],
            Items:
            [
                new("part_origin", "Origem do ecrã (original do aparelho / OEM / aftermarket)", "Screen origin (original part / OEM / aftermarket)", Measurement: true),
                new("adhesive", "Cola/junta do ecrã reaplicada", "Screen adhesive/gasket reapplied"),
                new("function", "Toque + imagem + brilho OK após montagem", "Touch + image + brightness OK after assembly"),
                new("flex", "Flex do ecrã (auricular/speaker) testado", "Screen flex (earpiece/speaker) tested"),
                new("biometrics", "Biometria under-display testada (se aplicável)", "Under-display biometrics tested (if applicable)"),
                new("water", "Selos de impermeabilidade intactos", "Water seals intact"),
            ]),
        new("used",
            "Compra de usado",
            "Used-device purchase",
            "Avaliação de compra — checklist de danos. O grau A–D sai do scan + checklist físico; este serviço marca o propósito de venda.",
            "Purchase evaluation — damage checklist. The A–D grade comes from the scan + physical checklist; this service sets the sale purpose.",
            AiPurpose: "sale",
            RequiredChecks: ["grade.overall", "battery.capacity"],
            Items:
            [
                new("frame", "Moldura: mossas, trincas, parafusos", "Frame: dents, cracks, screws"),
                new("screen", "Ecrã: riscos, manchas, dead pixels, brilho uniforme", "Screen: scratches, marks, dead pixels, even brightness"),
                new("touch", "Toque uniforme em todo o ecrã", "Even touch across the whole screen"),
                new("ports", "Portas: carga, auscultadores, leitor SIM", "Ports: charging, headphone jack, SIM tray"),
                new("buttons", "Botões power/volume + vibração", "Power/volume buttons + vibration"),
                new("accounts", "Contas removidas — sem FRP pendente", "Accounts removed — no pending FRP"),
                new("water", "Sinais de líquido / humidade", "Liquid / moisture signs"),
            ]),
        new(MaintenanceId,
            "Otimização / manutenção",
            "Optimization / maintenance",
            "Limpeza e otimização medida antes/depois — já existe como janela própria (LIMPEZA, adb). Escolher este serviço abre-a.",
            "Measured cleanup before/after — already exists as its own window (CLEANUP, adb). Picking this service opens it.",
            AiPurpose: "maintenance",
            RequiredChecks: [],
            Items: [],
            OpensMaintenanceDialog: true),
        new("whatsapp",
            "Migração WhatsApp",
            "WhatsApp migration",
            "Checklist da migração WhatsApp (ver docs/internal/whatsapp-migration.md): backup, transferência e verificação no destino.",
            "WhatsApp migration checklist (see docs/internal/whatsapp-migration.md): backup, transfer and destination verification.",
            AiPurpose: null,
            RequiredChecks: [],
            Items:
            [
                new("method", "Método usado (Move to iOS / cabo / manual)", "Method used (Move to iOS / cable / manual)", Measurement: true),
                new("backup", "Backup do WhatsApp confirmado na origem", "WhatsApp backup confirmed on the source"),
                new("transfer", "Transferência executada sem erros", "Transfer completed without errors"),
                new("restore", "Conversas restauradas no aparelho de destino", "Chats restored on the destination device"),
                new("media", "Multimédia (fotos/vídeos) visível no destino", "Media (photos/videos) visible on the destination"),
                new("verify", "Conversas de teste enviadas/recebidas OK", "Test chats sent/received OK"),
            ]),
    ];

    /// <summary>Template por id — null quando desconhecido.</summary>
    public static ServiceTemplate? Find(string? id) =>
        id is null ? null : All.FirstOrDefault(t => t.Id == id);

    /// <summary>Chave do check de um item: tpl.&lt;templateId&gt;.&lt;itemKey&gt;.</summary>
    public static string CheckKey(string templateId, string itemKey) => $"{Prefix}{templateId}.{itemKey}";

    /// <summary>
    /// Remove os checks de serviço do relatório (tpl.* + service.template).
    /// Um atendimento = um serviço: aplicar um template novo limpa os do
    /// anterior para um relatório nunca misturar duas reparações.
    /// </summary>
    public static void Clear(DeviceReport report)
    {
        foreach (var key in report.Results.Keys
            .Where(k => k.StartsWith(Prefix, StringComparison.Ordinal) || k == AppliedKey)
            .ToList())
            report.Remove(key);
    }

    /// <summary>
    /// Grava o checklist do template no relatório: cada item marcado vira um
    /// check tpl.&lt;id&gt;.* (pass/fail/skipped como no checklist físico),
    /// service.template regista o serviço escolhido e Purpose segue o
    /// AiPurpose do template ("sale" para compra de usado; null nos restantes
    /// — o serviço passa a definir o propósito, como o SaleBox do checklist).
    /// Devolve quantos itens escreveu.
    /// </summary>
    public static int Apply(
        DeviceReport report, ServiceTemplate template,
        IReadOnlyDictionary<string, int> marcas,
        IReadOnlyDictionary<string, string> medidas,
        Func<ServiceTemplateItem, string> rotulo)
    {
        Clear(report);
        var aplicadas = 0;
        foreach (var item in template.Items)
        {
            var marca = marcas.TryGetValue(item.Key, out var v) ? v : 0;
            if (marca == 0) continue;
            // Item de medição: o texto do técnico é o valor; sem texto o
            // check fica só com o estado (medição confirmada sem número).
            string? value = null;
            if (item.Measurement)
            {
                if (medidas.TryGetValue(item.Key, out var m) && !string.IsNullOrWhiteSpace(m))
                    value = m.Trim();
            }
            else
            {
                value = marca == 1 ? "ok" : marca == 2 ? "defeito" : "não testado";
            }
            report.Set(CheckKey(template.Id, item.Key), ChecklistMarks.Status(marca),
                value, rotulo(item));
            aplicadas++;
        }
        report.Set(AppliedKey, "info", template.Id, $"serviço: {template.LabelPt}");
        // O template é dono do propósito uma vez aplicado — um "sale" de um
        // template anterior não pode ficar preso a uma troca de bateria.
        report.Purpose = template.AiPurpose is "sale" or "repair" ? template.AiPurpose : null;
        return aplicadas;
    }

    /// <summary>
    /// A limpeza medida passa a ser o serviço do atendimento: se havia um
    /// template aplicado (tpl.* + service.template), os checks dele saem —
    /// um relatório não mistura "compra de usado" com "otimização". O
    /// Purpose que o template impôs (ex.: "sale") sai também; um Purpose
    /// manual do checklist físico (SaleBox, sem template aplicado)
    /// mantém-se.
    /// No fim grava service.template=maintenance: o seletor SERVIÇO reabre
    /// na otimização e distingue-a da limpeza complementar (o botão LIMPEZA
    /// direto não passa por aqui e não deixa marcador). Devolve true quando
    /// substituiu um serviço anterior — quem chamou pode avisar no log.
    /// </summary>
    public static bool ApplyMaintenance(DeviceReport report)
    {
        var serviceAnterior = report.Results.TryGetValue(AppliedKey, out var c)
            ? c.Value : null;
        var substituiu = report.Results.Keys.Any(k => k.StartsWith(Prefix, StringComparison.Ordinal))
            || (serviceAnterior is not null && serviceAnterior != MaintenanceId);
        Clear(report);
        if (serviceAnterior is not null && serviceAnterior != MaintenanceId)
            report.Purpose = null;
        var tpl = Find(MaintenanceId);
        report.Set(AppliedKey, "info", MaintenanceId, $"serviço: {tpl?.LabelPt ?? MaintenanceId}");
        return substituiu;
    }

    /// <summary>
    /// Rótulo PT de um check tpl.* para as vistas de relatório — null quando
    /// a chave não é de template (ou o item já não existe no catálogo).
    /// </summary>
    public static string? LabelPtFor(string checkKey)
    {
        var parts = checkKey.Split('.');
        if (parts.Length != 3 || parts[0] != "tpl") return null;
        var item = Find(parts[1])?.Items.FirstOrDefault(i => i.Key == parts[2]);
        return item?.LabelPt;
    }

    /// <summary>
    /// Origem da medição exigida — decide a dica "em falta" do diálogo:
    /// test.* vem do ECRÃ+TOQUE (teste no dispositivo), grade.* deriva-se
    /// do scan+checklist já feitos, o resto vem do SCAN. Sem isto o aviso
    /// mandava correr o scan para checks que o scan nunca produz.
    /// </summary>
    public static string RequiredSource(string key) =>
        key.StartsWith("test.", StringComparison.Ordinal) ? "test"
        : key.StartsWith("grade.", StringComparison.Ordinal) ? "auto"
        : "scan";

    /// <summary>
    /// true = a plataforma consegue produzir esta medição. battery.health
    /// só existe no coletor Android — no iOS um "em falta" seria
    /// impossível de resolver e só fazia ruído no diálogo.
    /// </summary>
    public static bool RequiredApplies(string key, string? platform) =>
        platform != "ios" || key is not "battery.health";

    /// <summary>
    /// Propósito de IA do serviço aplicado — lido do check service.template.
    /// A Cloud só conhece repair/sale/maintenance: para os serviços sem
    /// purpose próprio (bateria, ecrã, WhatsApp) a diferenciação viaja nos
    /// checks tpl.* dentro de results.
    /// </summary>
    public static string? AiPurposeFor(DeviceReport report) =>
        report.Results.TryGetValue(AppliedKey, out var c) && c.Value is { } id
            ? Find(id)?.AiPurpose
            : null;
}
