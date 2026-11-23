using System.Globalization;
using OficinaDiag.Devices;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// Fase D do Diag Pro: inventário iOS via installation_proxy
/// (ideviceinstaller -l -o list_all,xml) — parsing do plist, decisões dos
/// checks ios.apps.* e o fluxo de desinstalação com re-lista de verificação.
/// </summary>
public sealed class IosAppsTests
{
    // Forma real do que o installation_proxy devolve por app: tipo de
    // aplicação, identificador, nomes/versões e uso estático/dinâmico.
    private const string Plist = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <array>
            <dict>
                <key>ApplicationType</key><string>User</string>
                <key>CFBundleIdentifier</key><string>com.exemplo.app</string>
                <key>CFBundleDisplayName</key><string>Exemplo</string>
                <key>CFBundleShortVersionString</key><string>1.2.3</string>
                <key>StaticDiskUsage</key><integer>104857600</integer>
                <key>DynamicDiskUsage</key><integer>52428800</integer>
                <key>Entitlements</key>
                <dict>
                    <key>key.app</key><true/>
                </dict>
            </dict>
            <dict>
                <key>ApplicationType</key><string>System</string>
                <key>CFBundleIdentifier</key><string>com.apple.mobilesafari</string>
                <key>CFBundleName</key><string>MobileSafari</string>
                <key>CFBundleVersion</key><string>17.0</string>
                <key>StaticDiskUsage</key><integer>67108864</integer>
            </dict>
            <dict>
                <key>ApplicationType</key><string>User</string>
                <key>CFBundleIdentifier</key><string>com.exemplo.semuso</string>
                <key>CFBundleDisplayName</key><string>SemUso</string>
                <key>CFBundleShortVersionString</key><string>2.0</string>
            </dict>
        </array>
        </plist>
        """;

    private const string PlistVazio = """
        <?xml version="1.0" encoding="UTF-8"?>
        <plist version="1.0"><array></array></plist>
        """;

    private static void WithCulture(string name, Action body)
    {
        var old = CultureInfo.CurrentCulture;
        var oldUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(name);
            CultureInfo.CurrentUICulture = new CultureInfo(name);
            body();
        }
        finally
        {
            CultureInfo.CurrentCulture = old;
            CultureInfo.CurrentUICulture = oldUi;
        }
    }

    // ── parsing ──────────────────────────────────────────────────────────

    [Fact]
    public void ParseList_DevolveAppsComCampos()
    {
        var apps = IosAppParsing.ParseList(Plist);

        Assert.NotNull(apps);
        Assert.Equal(3, apps!.Count);

        var app = apps[0];
        Assert.Equal("com.exemplo.app", app.BundleId);
        Assert.Equal("Exemplo", app.DisplayName);
        Assert.Equal("1.2.3", app.Version);
        Assert.Equal(IosAppKind.User, app.Kind);
        Assert.True(app.CanUninstall);
        Assert.Equal(104857600, app.StaticBytes);
        Assert.Equal(52428800, app.DynamicBytes);
        Assert.Equal(157286400, app.TotalBytes);
        Assert.True(app.HasUsage);

        // System: nome via CFBundleName (sem DisplayName), versão via
        // CFBundleVersion (sem ShortVersion), DynamicDiskUsage ausente → null.
        var safari = apps[1];
        Assert.Equal(IosAppKind.System, safari.Kind);
        Assert.False(safari.CanUninstall);
        Assert.Equal("MobileSafari", safari.DisplayName);
        Assert.Equal("17.0", safari.Version);
        Assert.Equal(67108864, safari.StaticBytes);
        Assert.Null(safari.DynamicBytes);
        Assert.True(safari.HasUsage);

        // Sem chaves de uso → HasUsage falso, nunca 0 fingido.
        var semUso = apps[2];
        Assert.False(semUso.HasUsage);
        Assert.Equal(0, semUso.TotalBytes);
    }

    [Fact]
    public void ParseList_SemPlistOuComDictEmVezDeArray_DevolveNull()
    {
        Assert.Null(IosAppParsing.ParseList(null));
        Assert.Null(IosAppParsing.ParseList(""));
        Assert.Null(IosAppParsing.ParseList("Usage: ideviceinstaller [OPTIONS] COMMAND"));
        Assert.Null(IosAppParsing.ParseList(
            "<?xml version=\"1.0\"?><plist version=\"1.0\"><dict><key>x</key><string>y</string></dict></plist>"));
        Assert.Null(IosAppParsing.ParseList("isto não é xml"));
    }

    [Fact]
    public void ParseList_ArrayVazio_DevolveListaVazia()
    {
        var apps = IosAppParsing.ParseList(PlistVazio);
        Assert.NotNull(apps);
        Assert.Empty(apps!);
    }

    [Fact]
    public void ParseList_EntradasSemBundleIdValido_SaoSaltadas()
    {
        const string xml = """
            <?xml version="1.0"?><plist version="1.0"><array>
            <dict><key>ApplicationType</key><string>User</string>
                  <key>CFBundleIdentifier</key><string>sem-ponto</string></dict>
            <dict><key>ApplicationType</key><string>User</string>
                  <key>CFBundleIdentifier</key><string>com.exemplo.app</string></dict>
            <dict><key>ApplicationType</key><string>User</string></dict>
            </array></plist>
            """;
        var apps = IosAppParsing.ParseList(xml);
        Assert.Single(apps!);
        Assert.Equal("com.exemplo.app", apps![0].BundleId);
        // Sem ApplicationType → conservador: sistema, nunca desinstalável.
        var semTipo = IosAppParsing.ParseList("""
            <?xml version="1.0"?><plist version="1.0"><array>
            <dict><key>CFBundleIdentifier</key><string>com.exemplo.app</string></dict>
            </array></plist>
            """)![0];
        Assert.Equal(IosAppKind.System, semTipo.Kind);
        Assert.False(semTipo.CanUninstall);
    }

    [Theory]
    [InlineData("com.exemplo.app", true)]
    [InlineData("com.apple.mobilesafari", true)]
    [InlineData("a.b-c.d_e", true)]
    [InlineData("sem-ponto", false)]
    [InlineData("com.espaço.app", false)]
    [InlineData("com.app\" -U x", false)]
    [InlineData("com.app\n", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidBundleId_ReverseDNSEstrito(string? id, bool esperado) =>
        Assert.Equal(esperado, IosAppParsing.IsValidBundleId(id));

    // ── decisões / checks ────────────────────────────────────────────────

    [Fact]
    public void Checks_InventarioValido_TresChecks()
    {
        var inv = new IosAppInventory { Apps = IosAppParsing.ParseList(Plist)! };
        var checks = IosAppDecisions.Checks(inv)
            .ToDictionary(kv => kv.Key, kv => kv.Item2);

        Assert.Equal(3, checks.Count);
        Assert.Equal("info", checks["ios.apps.count"].Status);
        Assert.Equal("3", checks["ios.apps.count"].Value);
        Assert.Equal("2 de utilizador · 1 de sistema", checks["ios.apps.count"].Note);

        // 100 MB + 50 MB + 64 MB = 214 MB — soma Static+Dynamic de todas
        // as apps com uso reportado (a app sem uso não entra na conta).
        var space = checks["ios.apps.space"];
        Assert.Equal("info", space.Status);
        Assert.Equal("214 MB", space.Value);

        var top = checks["ios.apps.top"];
        Assert.Equal("info", top.Status);
        Assert.Contains("Exemplo", top.Value);
        Assert.Contains("150 MB", top.Value); // 100+50 MB da maior app
        Assert.Contains("maiores consumidores", top.Note);
    }

    [Fact]
    public void Checks_InventarioSemUso_EspacoETopSkipped()
    {
        var inv = new IosAppInventory
        {
            Apps =
            [
                new IosAppEntry
                {
                    BundleId = "com.exemplo.app", DisplayName = "Exemplo",
                    Kind = IosAppKind.User,
                },
            ],
        };
        var checks = IosAppDecisions.Checks(inv)
            .ToDictionary(kv => kv.Key, kv => kv.Item2);

        Assert.Equal("info", checks["ios.apps.count"].Status);
        Assert.Equal("1 de utilizador · 0 de sistema", checks["ios.apps.count"].Note);
        Assert.Equal("skipped", checks["ios.apps.space"].Status);
        Assert.Equal("skipped", checks["ios.apps.top"].Status);
    }

    [Theory]
    [InlineData("tool")]
    [InlineData("parse")]
    [InlineData("empty")]
    public void Checks_InventarioFalhado_TudoSkippedComNota(string error)
    {
        var inv = new IosAppInventory { Apps = [], Error = error };
        var checks = IosAppDecisions.Checks(inv).ToList();

        Assert.Equal(3, checks.Count);
        Assert.All(checks, kv =>
        {
            Assert.Equal("skipped", kv.Item2.Status);
            Assert.False(string.IsNullOrWhiteSpace(kv.Item2.Note));
        });
    }

    [Fact]
    public void FormatSize_GBMBKB()
    {
        WithCulture("pt-PT", () =>
        {
            Assert.Equal("1,5 GB", IosAppDecisions.FormatSize(1_610_612_736)); // vírgula pt-PT
            Assert.Equal("214 MB", IosAppDecisions.FormatSize(224_395_264));
            Assert.Equal("512 KB", IosAppDecisions.FormatSize(524_288));
            Assert.Equal("0 KB", IosAppDecisions.FormatSize(0));
            Assert.Equal("0 KB", IosAppDecisions.FormatSize(-10));
        });
    }

    // ── manager (seam injetável, sem iPhone) ─────────────────────────────

    [Fact]
    public async Task List_Sucesso_DevolveInventario()
    {
        var manager = new IosAppManager((_, args, _, _) =>
            args.Contains("list_all") ? Plist : null);

        var inv = await manager.ListAsync("00008101-0002390A3600801E");

        Assert.Null(inv.Error);
        Assert.Equal(3, inv.Apps.Count);
        Assert.Equal("com.exemplo.app", inv.Apps[0].BundleId);
    }

    [Fact]
    public async Task List_FalhaDoTool_ErrorTool()
    {
        var manager = new IosAppManager((_, _, _, _) => null);
        var inv = await manager.ListAsync("00008101-0002390A3600801E");
        Assert.Equal("tool", inv.Error);
    }

    [Fact]
    public async Task List_SaidaInvalida_ErrorParse()
    {
        var manager = new IosAppManager((_, _, _, _) => "ERROR: instproxy_browse returned -8");
        var inv = await manager.ListAsync("00008101-0002390A3600801E");
        Assert.Equal("parse", inv.Error);
    }

    [Fact]
    public async Task List_ArrayVazio_ErrorEmpty()
    {
        var manager = new IosAppManager((_, _, _, _) => PlistVazio);
        var inv = await manager.ListAsync("00008101-0002390A3600801E");
        Assert.Equal("empty", inv.Error);
    }

    [Fact]
    public async Task List_UdidInvalido_NaoCorreTool()
    {
        var calls = 0;
        var manager = new IosAppManager((_, _, _, _) =>
        {
            calls++;
            return Plist;
        });
        var inv = await manager.ListAsync("udid com espaços");
        Assert.Equal("tool", inv.Error);
        Assert.Equal(0, calls);
    }

    private const string PlistSemExemplo = """
        <?xml version="1.0" encoding="UTF-8"?>
        <plist version="1.0">
        <array>
            <dict>
                <key>ApplicationType</key><string>System</string>
                <key>CFBundleIdentifier</key><string>com.apple.mobilesafari</string>
            </dict>
        </array>
        </plist>
        """;

    /// <summary>Runner que simula: list devolve Plist, -U marca, relista sem a app.</summary>
    private static IosAppManager FakeDevice(List<string> calls)
    {
        var uninstalled = false;
        return new IosAppManager((_, args, _, _) =>
        {
            calls.Add(args);
            if (args.Contains("-U ", StringComparison.Ordinal))
            {
                uninstalled = args.Contains("com.exemplo.app");
                return "Uninstalling...";
            }
            return uninstalled ? PlistSemExemplo : Plist;
        });
    }

    [Fact]
    public async Task Uninstall_Utilizador_SucessoVerificado()
    {
        var calls = new List<string>();
        var r = await FakeDevice(calls).UninstallAsync("00008101-0002390A3600801E", "com.exemplo.app");

        Assert.True(r.Ok);
        Assert.Null(r.Error);
        // -U correu exactamente uma vez e a verificação relistou.
        Assert.Equal(1, calls.Count(a => a.StartsWith("-U ", StringComparison.Ordinal)));
        Assert.True(calls.Count(a => a.Contains("list_all")) >= 2);
    }

    [Fact]
    public async Task Uninstall_Sistema_RecusadoSemCorrer()
    {
        var calls = new List<string>();
        var r = await FakeDevice(calls).UninstallAsync("00008101-0002390A3600801E", "com.apple.mobilesafari");

        Assert.False(r.Ok);
        Assert.Equal("protected", r.Error);
        Assert.DoesNotContain(calls, a => a.StartsWith("-U ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Uninstall_AppAusente_Notfound()
    {
        var calls = new List<string>();
        var r = await FakeDevice(calls).UninstallAsync("00008101-0002390A3600801E", "com.nao.existe.app");

        Assert.False(r.Ok);
        Assert.Equal("notfound", r.Error);
        Assert.DoesNotContain(calls, a => a.StartsWith("-U ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Uninstall_BundleIdInvalido_InvalidSemCorrer()
    {
        var calls = new List<string>();
        var r = await FakeDevice(calls).UninstallAsync("00008101-0002390A3600801E", "com.app -U injectado");

        Assert.False(r.Ok);
        Assert.Equal("invalid", r.Error);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Uninstall_AppContinuaInstalada_ErrorVerify()
    {
        // O tool "corre" mas a relista ainda traz a app — o status do
        // ideviceinstaller não é fiável, a lista é.
        var calls = new List<string>();
        var manager = new IosAppManager((_, args, _, _) =>
        {
            calls.Add(args);
            return args.StartsWith("-U ", StringComparison.Ordinal) ? "done" : Plist;
        });
        var r = await manager.UninstallAsync("00008101-0002390A3600801E", "com.exemplo.app");

        Assert.False(r.Ok);
        Assert.Equal("verify", r.Error);
    }

    [Fact]
    public async Task Uninstall_ToolNaoResponde_ErrorTool()
    {
        var calls = new List<string>();
        var manager = new IosAppManager((_, args, _, _) =>
        {
            calls.Add(args);
            return args.StartsWith("-U ", StringComparison.Ordinal) ? null : Plist;
        });
        var r = await manager.UninstallAsync("00008101-0002390A3600801E", "com.exemplo.app");

        Assert.False(r.Ok);
        Assert.Equal("tool", r.Error);
    }
}
