using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OficinaDiag.Devices;

public enum IosAppKind { User, System }

/// <summary>
/// Uma app do inventário do installation_proxy — o que
/// <c>ideviceinstaller -l -o list_all,xml</c> devolve por app.
/// </summary>
public sealed record IosAppEntry
{
    public required string BundleId { get; init; }
    public required string DisplayName { get; init; }
    /// <summary>CFBundleShortVersionString, fallback CFBundleVersion — null se ambos faltarem.</summary>
    public string? Version { get; init; }
    public IosAppKind Kind { get; init; }
    /// <summary>StaticDiskUsage em bytes — null quando o iOS não reporta (não é 0).</summary>
    public long? StaticBytes { get; init; }
    /// <summary>DynamicDiskUsage em bytes — null quando o iOS não reporta.</summary>
    public long? DynamicBytes { get; init; }
    public long TotalBytes => (StaticBytes ?? 0) + (DynamicBytes ?? 0);
    /// <summary>O iOS reportou uso para esta app — sem isso o tamanho é desconhecido, não zero.</summary>
    public bool HasUsage => StaticBytes is not null || DynamicBytes is not null;
    /// <summary>installation_proxy só desinstala apps de utilizador — sistema é sempre protegido.</summary>
    public bool CanUninstall => Kind == IosAppKind.User;
}

public sealed record IosAppInventory
{
    public required IReadOnlyList<IosAppEntry> Apps { get; init; }
    /// <summary>"tool" | "parse" | "empty" — null = inventário válido.</summary>
    public string? Error { get; init; }
}

/// <summary>Resultado de uma ação do gestor — Ok + output ou Error legível.</summary>
public sealed record IosAppActionResult(bool Ok, string? Output, string? Error);

/// <summary>
/// Parsing do plist XML que o <c>-o xml</c> do ideviceinstaller imprime:
/// um &lt;array&gt; de &lt;dict&gt;, um por app. O plist é binário-safe mas
/// aqui chega sempre em XML — XDocument chega, como no resto do coletor.
/// </summary>
internal static class IosAppParsing
{
    // \A/\z em vez de ^/$: em .NET o $ aceita um \n final — um bundle id
    // com newline passava na guarda de injecção de linha de comando.
    private static readonly Regex BundleIdPattern = new(
        @"\A[A-Za-z0-9][A-Za-z0-9_-]*(\.[A-Za-z0-9][A-Za-z0-9_-]*)+\z",
        RegexOptions.Compiled);

    /// <summary>
    /// Reverse-DNS estrito (≥1 ponto, alfanumérico/hífen/underscore — o
    /// underscore aparece em bundle ids reais). Serve dois
    /// propósitos: filtra entradas malformadas do plist e é a guarda que
    /// impede um bundle id com aspas/espaços de ser interpolado na linha de
    /// comando do ideviceinstaller no uninstall.
    /// </summary>
    internal static bool IsValidBundleId(string? id) =>
        id is { Length: > 0 and <= 200 } && BundleIdPattern.IsMatch(id);

    /// <summary>
    /// Apps do array do plist. null = a saída não é um plist com array no
    /// topo (erro do tool, usage text, plist binário) — nunca uma lista vazia
    /// fingida. Um array vazio é uma lista vazia a sério (o chamador decide).
    /// </summary>
    internal static List<IosAppEntry>? ParseList(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml) || !xml.Contains("<plist", StringComparison.Ordinal))
            return null;
        try
        {
            var doc = XDocument.Parse(xml);
            // Só o array do topo interessa — os dicts das apps têm arrays
            // próprios (ícones, famílias) que não podem ser apanhados.
            var array = doc.Root?.Elements("array").FirstOrDefault();
            if (array is null) return null;
            var apps = new List<IosAppEntry>();
            foreach (var dict in array.Elements("dict"))
            {
                if (EntryFrom(dict) is { } entry) apps.Add(entry);
            }
            return apps;
        }
        catch { return null; }
    }

    private static IosAppEntry? EntryFrom(XElement dict)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in IosCollector.Pairs(dict))
        {
            if (value.Name.LocalName is "string" or "integer" or "real")
                map[key] = value.Value;
            else if (value.Name.LocalName is "true" or "false")
                map[key] = value.Name.LocalName;
        }

        var bundleId = map.GetValueOrDefault("CFBundleIdentifier");
        if (!IsValidBundleId(bundleId)) return null;

        // ApplicationType: "User" é o único valor que autoriza a
        // desinstalação; "System"/"Hidden"/ausente caem em System — a
        // classificação conservadora nunca oferece uma app protegida.
        var appType = map.GetValueOrDefault("ApplicationType");
        return new IosAppEntry
        {
            BundleId = bundleId!,
            DisplayName = map.GetValueOrDefault("CFBundleDisplayName")
                ?? map.GetValueOrDefault("CFBundleName")
                ?? bundleId!,
            Version = map.GetValueOrDefault("CFBundleShortVersionString")
                ?? map.GetValueOrDefault("CFBundleVersion"),
            Kind = appType == "User" ? IosAppKind.User : IosAppKind.System,
            StaticBytes = Bytes(map.GetValueOrDefault("StaticDiskUsage")),
            DynamicBytes = Bytes(map.GetValueOrDefault("DynamicDiskUsage")),
        };
    }

    private static long? Bytes(string? raw) =>
        long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) && b >= 0
            ? b : null;
}

/// <summary>
/// Decisões do inventário iOS — checks ios.apps.* do relatório e guarda da
/// desinstalação. Separado do manager para ser testável sem iPhone.
/// </summary>
internal static class IosAppDecisions
{
    internal static readonly string[] CheckKeys =
        ["ios.apps.count", "ios.apps.space", "ios.apps.top"];

    /// <summary>Tamanho legível — mesma convenção do storage check ("1,2 GB").</summary>
    internal static string FormatSize(long bytes)
    {
        if (bytes < 0) bytes = 0;
        if (bytes >= 1_073_741_824)
            return $"{bytes / 1_073_741_824.0:0.#} GB";
        if (bytes >= 1_048_576)
            return $"{bytes / 1_048_576.0:0} MB";
        return $"{bytes / 1024.0:0} KB";
    }

    /// <summary>
    /// Checks ios.apps.* a partir do inventário. Inventário falhado → os
    /// três ficam skipped com a razão honesta (nunca "0 apps" por omissão).
    /// </summary>
    internal static IEnumerable<(string Key, IosDecisions.Reading)> Checks(IosAppInventory inv)
    {
        if (inv.Error is not null)
        {
            var note = inv.Error switch
            {
                "parse" => "resposta do installation_proxy ilegível — inventário por fazer",
                "empty" => "lista vazia — confirma que o iPhone está desbloqueado",
                _ => "installation_proxy não respondeu — iOS antigo ou serviço indisponível",
            };
            foreach (var key in CheckKeys)
                yield return (key, new IosDecisions.Reading("skipped", "indisponível", note));
            yield break;
        }

        var user = inv.Apps.Count(a => a.Kind == IosAppKind.User);
        var system = inv.Apps.Count - user;
        yield return ("ios.apps.count", new IosDecisions.Reading("info",
            inv.Apps.Count.ToString(CultureInfo.InvariantCulture),
            $"{user} de utilizador · {system} de sistema"));

        var withUsage = inv.Apps.Where(a => a.HasUsage).ToList();
        if (withUsage.Count == 0)
        {
            // iOS que não reporta Static/DynamicDiskUsage não é disco a
            // zero — é medição em falta, e skipped é a resposta honesta.
            yield return ("ios.apps.space", new IosDecisions.Reading("skipped", "sem dados",
                "este iOS não reporta uso por app — espaço por app por medir"));
            yield return ("ios.apps.top", new IosDecisions.Reading("skipped", "sem dados",
                "sem uso por app não há ranking de consumidores"));
            yield break;
        }

        var total = withUsage.Sum(a => a.TotalBytes);
        yield return ("ios.apps.space", new IosDecisions.Reading("info", FormatSize(total),
            "soma Static+Dynamic do iOS — estimativa; não inclui o sistema base"));

        var top = withUsage.OrderByDescending(a => a.TotalBytes).Take(5).ToList();
        yield return ("ios.apps.top", new IosDecisions.Reading("info",
            $"{top[0].DisplayName} — {FormatSize(top[0].TotalBytes)}",
            "maiores consumidores: " + string.Join(" · ",
                top.Select(a => $"{a.DisplayName} {FormatSize(a.TotalBytes)}"))));
    }
}

/// <summary>
/// Inventário e desinstalação de apps iOS via ideviceinstaller.exe — o
/// serviço com.apple.mobile.installation_proxy por trás é o mesmo que o
/// iTunes usa, por isso funciona sem jailbreak. A execução fica numa seam
/// injetável para os testes provarem o fluxo sem iPhone.
/// </summary>
public sealed class IosAppManager
{
    private readonly Func<string, string, int, CancellationToken, string?>? _run;

    public IosAppManager() { }

    internal IosAppManager(Func<string, string, int, CancellationToken, string?>? run) => _run = run;

    /// <summary>Timeout do browse: ~500 apps numa iPhone demora segundos, não minutos.</summary>
    private const int ListTimeoutMs = 30_000;
    private const int UninstallTimeoutMs = 60_000;

    /// <summary>
    /// Orçamento do inventário dentro de um scan — acima do ListTimeoutMs
    /// para o timeout do próprio tool produzir o erro primeiro; o WhenAny
    /// é só a rede de segurança para um bloqueio fora do ToolProcess.
    /// </summary>
    internal const int ScanBudgetMs = ListTimeoutMs + 5_000;

    /// <summary>Inventário síncrono — o coletor já corre fora da UI thread.</summary>
    internal IosAppInventory List(string udid, CancellationToken ct = default)
    {
        var xml = Exec(udid, "-l -o list_all,xml", ListTimeoutMs, ct);
        if (xml is null) return new IosAppInventory { Apps = [], Error = "tool" };
        var apps = IosAppParsing.ParseList(xml);
        if (apps is null) return new IosAppInventory { Apps = [], Error = "parse" };
        if (apps.Count == 0) return new IosAppInventory { Apps = [], Error = "empty" };
        return new IosAppInventory { Apps = apps };
    }

    /// <summary>Versão para a UI — o browse bloqueia, por isso vai para o thread-pool.</summary>
    public Task<IosAppInventory> ListAsync(string udid, CancellationToken ct = default) =>
        Task.Run(() => List(udid, ct));

    /// <summary>
    /// Inventário com orçamento próprio — para o scan iOS, cujo prazo global
    /// de 30 s já expirou quando o inventário começa. Se a listagem exceder
    /// o orçamento (ou falhar por qualquer via), devolve Error="tool": os
    /// checks ficam skipped e o estado do scan nunca é substituído. A task
    /// abandonada continua mas morre sozinha — o ToolProcess mata a árvore
    /// ao fim do ListTimeoutMs.
    /// </summary>
    internal async Task<IosAppInventory> ListBoundedAsync(
        string udid, int budgetMs, CancellationToken ct = default)
    {
        try
        {
            var list = Task.Run(() => List(udid, ct), ct);
            var winner = await Task.WhenAny(list, Task.Delay(budgetMs, ct));
            ct.ThrowIfCancellationRequested();
            // IsCompleted cobre a corrida em que os dois terminam juntos.
            return winner == list || list.IsCompleted
                ? await list
                : new IosAppInventory { Apps = [], Error = "tool" };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new IosAppInventory { Apps = [], Error = "tool" };
        }
    }

    /// <summary>
    /// Desinstala uma app de utilizador. O ideviceinstaller sai com código 0
    /// mesmo quando o status do serviço reporta erro — a prova real é a app
    /// ter desaparecido do inventário seguinte, por isso há re-lista.
    /// </summary>
    internal IosAppActionResult Uninstall(string udid, string bundleId, CancellationToken ct = default)
    {
        if (!IosAppParsing.IsValidBundleId(bundleId))
            return new IosAppActionResult(false, null, "invalid");

        // A lista fresca é a fonte — a UI pode estar velha e um bundle id de
        // sistema nunca passa, mesmo que o chamador tente à força.
        var before = List(udid, ct);
        if (before.Error is not null)
            return new IosAppActionResult(false, null, before.Error);
        var app = before.Apps.FirstOrDefault(a => a.BundleId == bundleId);
        if (app is null) return new IosAppActionResult(false, null, "notfound");
        if (app.Kind != IosAppKind.User)
            return new IosAppActionResult(false, null, "protected");

        var output = Exec(udid, $"-U {bundleId}", UninstallTimeoutMs, ct);
        if (output is null) return new IosAppActionResult(false, null, "tool");

        // A relista é a prova — mas só se ela própria correr bem. Uma
        // relista falhada (tool/parse/empty) é erro de fetch, não
        // «a app continua instalada»: propaga-se o motivo real.
        var after = List(udid, ct);
        if (after.Error is not null)
            return new IosAppActionResult(false, output, after.Error);
        if (after.Apps.All(a => a.BundleId != bundleId))
            return new IosAppActionResult(true, output, null);
        return new IosAppActionResult(false, output, "verify");
    }

    public Task<IosAppActionResult> UninstallAsync(
        string udid, string bundleId, CancellationToken ct = default) =>
        Task.Run(() => Uninstall(udid, bundleId, ct));

    /// <summary>
    /// Corre o ideviceinstaller com o UDID já validado — args nunca levam
    /// aspas nem espaços (os valores vêm de IsValidUdid/IsValidBundleId).
    /// </summary>
    private string? Exec(string udid, string args, int timeoutMs, CancellationToken ct)
    {
        if (!IosCollector.IsValidUdid(udid)) return null;
        if (_run is not null) return _run(udid, args, timeoutMs, ct);
        var exe = IosCollector.ToolPath("ideviceinstaller.exe");
        // O plist sai em UTF-8 — sem encoding explícito o .NET lê com a
        // página de códigos do Windows e nomes acentuados corrompem.
        return exe is null ? null
            : ToolProcess.Run(exe, $"-u {udid} {args}", timeoutMs, ct, Encoding.UTF8);
    }
}
