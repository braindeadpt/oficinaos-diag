using OficinaDiag.Devices;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// Auditoria de segurança Android (pacote Pro, ideia 4): parsers das saídas
/// de adb e as decisões que viram checks <c>security.audit.*</c>. Tudo puro —
/// os fixtures imitam o formato real do <c>pm</c>/<c>dumpsys</c>/<c>settings</c>.
/// </summary>
public class AndroidSecurityAuditTests
{
    // ── pm list packages -3 -i ───────────────────────────────────────────

    [Fact]
    public void UserPackages_LinhaUnicaComInstaller()
    {
        var apps = AndroidSecurityAudit.ParseUserPackages(
            "package:com.whatsapp  installer=com.android.vending\n" +
            "package:org.telegram.messenger  installer=null\n");

        Assert.NotNull(apps);
        Assert.Equal(2, apps!.Count);
        Assert.Equal("com.whatsapp", apps[0].Package);
        Assert.Equal("com.android.vending", apps[0].Installer);
        Assert.True(apps[0].FromPlayStore);
        Assert.Equal("org.telegram.messenger", apps[1].Package);
        Assert.Null(apps[1].Installer);
        Assert.False(apps[1].FromPlayStore);
    }

    [Fact]
    public void UserPackages_InstallerNaLinhaSeguinte()
    {
        // Versões antigas do pm quebram a linha depois do nome do pacote.
        var apps = AndroidSecurityAudit.ParseUserPackages(
            "package:com.example.app\n" +
            "    installer=com.android.vending\n" +
            "package:com.example.other\n");

        Assert.NotNull(apps);
        Assert.Equal("com.android.vending", apps![0].Installer);
        Assert.Null(apps[1].Installer);
    }

    [Fact]
    public void UserPackages_SemAppsDeUtilizadorDaListaVaziaNaoNula()
    {
        var apps = AndroidSecurityAudit.ParseUserPackages("package:com.android.settings\n");
        // Nota: -3 não lista system apps; o parser não filtra — quem filtra é o pm.
        Assert.NotNull(apps);
        Assert.Single(apps!);
    }

    [Fact]
    public void UserPackages_AdbMudoEDistingueDeVazio()
    {
        Assert.Null(AndroidSecurityAudit.ParseUserPackages(null));
        Assert.Empty(AndroidSecurityAudit.ParseUserPackages("")!);
    }

    // ── dumpsys package (permissões concedidas) ─────────────────────────

    private const string PackageDump = """
        Activity Resolver Table:
          Scheme: "market" ...
        Packages:
          Package [com.whatsapp] (a1b2c3):
            userId=10123
            pkg=Package{deadbeef com.whatsapp}
            codePath=/data/app/~~x==/com.whatsapp-abc==
            requested permissions:
              android.permission.INTERNET
              android.permission.READ_SMS
              android.permission.REQUEST_INSTALL_PACKAGES
            install permissions:
              android.permission.INTERNET: granted=true
              android.permission.REQUEST_INSTALL_PACKAGES: granted=false
            runtime permissions:
              android.permission.READ_SMS: granted=true, flags=[USER_SENSITIVE_WHEN_GRANTED]
              android.permission.CAMERA: granted=false, flags=[USER_SENSITIVE_WHEN_GRANTED]
          Package [com.example.spy] (d4e5f6):
            userId=10124
            requested permissions:
              android.permission.READ_CONTACTS
            runtime permissions:
              android.permission.READ_CONTACTS: granted=true, flags=[]
              android.permission.READ_PHONE_STATE: granted=true, flags=[]
        Package Changes:
          Package [com.whatsapp] (a1b2c3):
            granted permissions were here but must be ignored
        """;

    [Fact]
    public void DumpGrants_SoGrantedTrueDentroDePackages()
    {
        var grants = AndroidSecurityAudit.ParsePackageDumpGrants(PackageDump);

        Assert.NotNull(grants);
        // INTERNET (install) + READ_SMS (runtime) do whatsapp; CONTACTS + PHONE_STATE do spy.
        // REQUEST_INSTALL_PACKAGES granted=false e CAMERA granted=false ficam fora;
        // o "Package Changes" depois de Packages: não conta.
        Assert.Equal(4, grants!.Count);
        Assert.Contains(grants, g => g.Package == "com.whatsapp" && g.Permission == "android.permission.INTERNET");
        Assert.Contains(grants, g => g.Package == "com.whatsapp" && g.Permission == "android.permission.READ_SMS");
        Assert.Contains(grants, g => g.Package == "com.example.spy" && g.Permission == "android.permission.READ_CONTACTS");
        Assert.Contains(grants, g => g.Package == "com.example.spy" && g.Permission == "android.permission.READ_PHONE_STATE");
        Assert.DoesNotContain(grants, g => g.Permission == "android.permission.REQUEST_INSTALL_PACKAGES");
        Assert.DoesNotContain(grants, g => g.Permission == "android.permission.CAMERA");
    }

    [Fact]
    public void DumpGrants_CrlfELinhasVaziasNaoFechamASeccao()
    {
        // adb no Windows pode trazer \r\n; uma linha vazia a meio de
        // "Packages:" não pode ser lida como cabeçalho de topo.
        var dump = "Activity Resolver Table:\r\nPackages:\r\n" +
            "  Package [com.a] (aa):\r\n" +
            "\r\n" +
            "    install permissions:\r\n" +
            "      android.permission.INTERNET: granted=true\r\n" +
            "      android.permission.READ_SMS: granted=false\r\n" +
            "\r\n" +
            "Package Changes:\r\n" +
            "  Package [com.b] (bb):\r\n" +
            "      android.permission.READ_CONTACTS: granted=true\r\n";
        var grants = AndroidSecurityAudit.ParsePackageDumpGrants(dump);
        Assert.NotNull(grants);
        Assert.Single(grants!);
        Assert.Equal(("com.a", "android.permission.INTERNET"), grants[0]);
    }

    [Fact]
    public void DumpGrants_DumpNuloEDumpSemSecao()
    {
        Assert.Null(AndroidSecurityAudit.ParsePackageDumpGrants(null));
        // Sem "Packages:" o dump está truncado/corrompido — null = skipped,
        // nunca uma lista vazia que daria "pass" às cegas.
        Assert.Null(AndroidSecurityAudit.ParsePackageDumpGrants("lixo sem secção Packages"));
    }

    [Fact]
    public void DumpGrants_UidPartilhadoReplicaPermissoesNosMembros()
    {
        // Apps com sharedUserId não têm bloco de permissões em "Packages:" —
        // as permissões do UID vivem em "Shared users:" e valem para todos.
        var dump = """
            Packages:
              Package [com.example.normal] (aa):
                install permissions:
                  android.permission.INTERNET: granted=true
            Shared users:
              SharedUser [com.example.shared] (bb):
                appId=10100
                Packages
                  PackageSetting{c0ffee com.example.membera/10100}
                  PackageSetting{c0ffee com.example.memberb/10100}
                install permissions:
                  android.permission.INTERNET: granted=true
                  android.permission.WRITE_SETTINGS: granted=true
                User 0:
                  runtime permissions:
                    android.permission.READ_SMS: granted=true, flags=[USER_SENSITIVE_WHEN_GRANTED]
                    android.permission.READ_CONTACTS: granted=false, flags=[]
            Package Changes:
              SharedUser [com.example.late] (cc):
                PackageSetting{c0ffee com.example.ignored/10101}
                install permissions:
                  android.permission.READ_SMS: granted=true
            """;
        var grants = AndroidSecurityAudit.ParsePackageDumpGrants(dump);

        Assert.NotNull(grants);
        // Cada membro herda os grants do UID partilhado; granted=false fica fora
        // e o bloco depois de "Package Changes:" (fora de "Shared users:") não conta.
        Assert.Contains(grants!, g => g.Package == "com.example.membera" && g.Permission == "android.permission.READ_SMS");
        Assert.Contains(grants!, g => g.Package == "com.example.memberb" && g.Permission == "android.permission.WRITE_SETTINGS");
        Assert.Contains(grants!, g => g.Package == "com.example.memberb" && g.Permission == "android.permission.INTERNET");
        Assert.DoesNotContain(grants!, g => g.Package == "com.example.membera" && g.Permission == "android.permission.READ_CONTACTS");
        Assert.DoesNotContain(grants!, g => g.Package == "com.example.ignored");
    }

    // ── dumpsys user (no_install_unknown_sources) ──────────────────────

    private const string UserDump = """
        Current user: 0

        Users:
          UserInfo{0:null:4c13} serialNo=0 isPrimary=true
            Type: android.os.usertype.full.SYSTEM
            Restrictions:
              none
            Device policy restrictions:
              null
            Effective restrictions:
              none
            UserProperties:
                mPropertiesPresent=0
          UserInfo{10:null:5d14} serialNo=10 isPrimary=false
            Type: android.os.usertype.profile.MANAGED
            Restrictions:
              no_install_unknown_sources
            Device policy restrictions:
              null
            Effective restrictions:
              no_install_unknown_sources
            UserProperties:
                mPropertiesPresent=0

        Device properties:
          Device policy global restrictions:
            null
          Guest restrictions:
            no_install_unknown_sources
        """;

    [Fact]
    public void UnknownSources_CurrentUserSemRestricaoEDesseBloqueio()
    {
        // O utilizador atual (0) não tem a restrição; o perfil gerido (10) tem.
        // As "Guest restrictions" nomeiam-na sempre — não podem contar.
        Assert.Equal(false, AndroidSecurityAudit.ParseUnknownSourcesBlocked(UserDump));
    }

    [Fact]
    public void UnknownSources_CurrentUserComRestricaoBloqueia()
    {
        var dump = UserDump.Replace("Current user: 0", "Current user: 10");
        Assert.Equal(true, AndroidSecurityAudit.ParseUnknownSourcesBlocked(dump));
    }

    [Fact]
    public void UnknownSources_NuloOuSemUtilizadorAtualDevolveNulo()
    {
        Assert.Null(AndroidSecurityAudit.ParseUnknownSourcesBlocked(null));
        Assert.Null(AndroidSecurityAudit.ParseUnknownSourcesBlocked("lixo sem current user"));
    }

    // ── settings: acessibilidade ────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    public void Accessibility_SemServicosDaListaVazia(string? output)
    {
        var services = AndroidSecurityAudit.ParseAccessibilityServices(output);
        Assert.NotNull(services);
        Assert.Empty(services!);
    }

    [Fact]
    public void Accessibility_ParsingComponentes()
    {
        var services = AndroidSecurityAudit.ParseAccessibilityServices(
            "com.google.android.marvin.talkback/com.google.android.marvin.talkback.TalkBackService:" +
            "com.example.app/.MyAccessibilityService");

        Assert.NotNull(services);
        Assert.Equal(2, services!.Count);
        Assert.Equal("com.example.app", services[1].Package);
        Assert.Equal(".MyAccessibilityService", services[1].Service);
    }

    [Fact]
    public void Accessibility_AdbMudoENull()
    {
        Assert.Null(AndroidSecurityAudit.ParseAccessibilityServices(null));
    }

    // ── settings: verificador de pacotes ────────────────────────────────

    [Theory]
    [InlineData("V1=1\nV2=1", true, true)]
    [InlineData("V1=0\nV2=0", false, false)]
    [InlineData("V1=1\nV2=", true, null)]
    [InlineData("V1=\nV2=0", null, false)]
    [InlineData("", null, null)]
    public void VerifierSettings_Flags(string output, bool? enabled, bool? verifyAdb)
    {
        var (e, v) = AndroidSecurityAudit.ParseVerifierSettings(output);
        Assert.Equal(enabled, e);
        Assert.Equal(verifyAdb, v);
    }

    // ── pm list packages -d ─────────────────────────────────────────────

    [Fact]
    public void DisabledPackages_NomesSimples()
    {
        var list = AndroidSecurityAudit.ParseDisabledPackages(
            "package:com.android.dreams.basic\npackage:com.example.old\n");
        Assert.NotNull(list);
        Assert.Equal(["com.android.dreams.basic", "com.example.old"], list);
    }

    [Fact]
    public void DisabledPackages_NuloEDescarregaLixo()
    {
        Assert.Null(AndroidSecurityAudit.ParseDisabledPackages(null));
        Assert.Empty(AndroidSecurityAudit.ParseDisabledPackages("not a package line\n")!);
    }

    // ── Decisões ────────────────────────────────────────────────────────

    private static AndroidSecurityAudit.UserApp App(string pkg, string? installer) =>
        new(pkg, installer);

    [Fact]
    public void Sideloaded_TudoPlayStoreEPass()
    {
        var r = AndroidSecurityAudit.Sideloaded([App("com.a", "com.android.vending")]);
        Assert.Equal("pass", r.Status);
        Assert.Equal("0 apps fora da Play Store", r.Value);
    }

    [Fact]
    public void Sideloaded_SidemLoadEDesconhecidoFicamInfoComOrigem()
    {
        var r = AndroidSecurityAudit.Sideloaded(
        [
            App("com.a", "com.android.vending"),
            App("com.b", null),
            App("com.c", "com.xiaomi.market"),
        ]);
        Assert.Equal("info", r.Status);
        Assert.Contains("2 app(s)", r.Value);
        Assert.Contains("com.b (origem desconhecida)", r.Note);
        Assert.Contains("com.c (com.xiaomi.market)", r.Note);
    }

    private static readonly (string Package, string Permission)[] SpyGrants =
    [
        ("com.spy", "android.permission.READ_SMS"),
        ("com.spy", "android.permission.READ_CONTACTS"),
        ("com.whatsapp", "android.permission.READ_PHONE_STATE"),
        ("com.system.app", "android.permission.READ_SMS"), // não é app de utilizador — ignorada
    ];

    private static readonly AndroidSecurityAudit.UserApp[] AuditApps =
    [
        App("com.spy", null),
        App("com.whatsapp", "com.android.vending"),
    ];

    [Fact]
    public void SensitivePermissions_SideloadComSMSContactsEWarn()
    {
        var r = AndroidSecurityAudit.SensitivePermissions(AuditApps, SpyGrants);
        Assert.Equal("warn", r.Status);
        Assert.Contains("fora da Play Store", r.Value);
        Assert.Contains("com.spy: READ_CONTACTS, READ_SMS", r.Note);
        Assert.DoesNotContain("com.system.app", r.Note);
    }

    [Fact]
    public void SensitivePermissions_SoPlayStoreEInfo()
    {
        var r = AndroidSecurityAudit.SensitivePermissions(AuditApps,
            [.. SpyGrants.Where(g => g.Package != "com.spy")]);
        Assert.Equal("info", r.Status);
        Assert.Contains("Play Store", r.Value);
    }

    [Fact]
    public void SensitivePermissions_SemGrantsEPass()
    {
        var r = AndroidSecurityAudit.SensitivePermissions(AuditApps, []);
        Assert.Equal("pass", r.Status);
    }

    [Fact]
    public void SensitivePermissions_GerRequestedFalseNaoConta()
    {
        // granted=false nunca entra no parser, mas a decisão também ignora
        // permissões que não estão no conjunto sensível.
        var r = AndroidSecurityAudit.SensitivePermissions(AuditApps,
            [("com.spy", "android.permission.INTERNET")]);
        Assert.Equal("pass", r.Status);
    }

    // ── dumpsys appops (REQUEST_INSTALL_PACKAGES) ──────────────────────

    private const string AppOpsDump = """
        Current AppOps Service state:
          Op mode watchers:
            Op COARSE_LOCATION:
              #0: ModeCallback{1e735b0 watchinguid=-1 flags=0x1 op=FINE_LOCATION from uid=u0a128 pid=5060}
          Uid u0a100:
            state=cch
            Package com.android.system: 
            Package com.whatsapp:
              REQUEST_INSTALL_PACKAGES (ignore):
              READ_CONTACTS: mode=ignore
          Uid u0a101:
            state=cch
            Package com.spy:
              REQUEST_INSTALL_PACKAGES (allow):
              REQUEST_INSTALL_PACKAGES (allow): foreground
            Package com.other:
              REQUEST_INSTALL_PACKAGES: mode=allow
        AppOps policy location tags:
          #0: 10128=com.google.android.gms[awareness_provider]
        """;

    [Fact]
    public void AppOps_SoModoAllowConta()
    {
        var allowed = AndroidSecurityAudit.ParseInstallPackagesAppOps(AppOpsDump);

        Assert.NotNull(allowed);
        // ignore não conta; allow conta nos dois formatos ((allow) e mode=allow);
        // duplicados no mesmo pacote não importam (HashSet).
        Assert.Equal(["com.other", "com.spy"], allowed!.OrderBy(s => s, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("com.whatsapp", allowed);
        Assert.DoesNotContain("com.android.system", allowed);
    }

    [Fact]
    public void AppOps_NuloDevolveNuloESemCabecalhoEDumpInvalido()
    {
        Assert.Null(AndroidSecurityAudit.ParseInstallPackagesAppOps(null));
        // Output ilegível não é um inventário vazio: null = skipped no coletor.
        Assert.Null(AndroidSecurityAudit.ParseInstallPackagesAppOps("lixo sem appops"));
    }

    [Fact]
    public void AppOps_DumpValidoSemAutorizacoesDaListaVazia()
    {
        var allowed = AndroidSecurityAudit.ParseInstallPackagesAppOps("""
            Current AppOps Service state:
              Uid u0a100:
                Package com.whatsapp:
                  READ_CONTACTS: mode=ignore
            """);
        Assert.NotNull(allowed);
        Assert.Empty(allowed!);
    }

    [Theory]
    // Android 8 imprime o modo numérico: 0 = MODE_ALLOWED.
    [InlineData("REQUEST_INSTALL_PACKAGES (0):", true)]
    [InlineData("REQUEST_INSTALL_PACKAGES: mode=0", true)]
    [InlineData("REQUEST_INSTALL_PACKAGES (1):", false)]
    [InlineData("REQUEST_INSTALL_PACKAGES: mode=ignore", false)]
    [InlineData("REQUEST_INSTALL_PACKAGES: mode=default", false)]
    public void AppOps_ModoNumericoDoAndroid8(string opLine, bool expected)
    {
        var dump = "Current AppOps Service state:\n  Uid u0a100:\n    Package com.a:\n      " + opLine + "\n";
        var allowed = AndroidSecurityAudit.ParseInstallPackagesAppOps(dump);
        Assert.NotNull(allowed);
        Assert.Equal(expected, allowed!.Contains("com.a"));
    }

    [Fact]
    public void InstallPackages_AutorizadasPelaAppopSaoWarn()
    {
        var r = AndroidSecurityAudit.InstallPackages(AuditApps,
            new HashSet<string>(StringComparer.Ordinal) { "com.spy" });
        Assert.Equal("warn", r.Status);
        Assert.Contains("1 app(s) autorizadas a instalar APKs", r.Value);
        Assert.Contains("com.spy (via origem desconhecida)", r.Note);
        Assert.Contains("desconhecidos", r.Note);
    }

    [Fact]
    public void InstallPackages_AppopDeSistemaNaoConta()
    {
        // Só contam apps de utilizador (pm -3): appops do sistema não são risco novo.
        var r = AndroidSecurityAudit.InstallPackages(AuditApps,
            new HashSet<string>(StringComparer.Ordinal) { "com.android.system", "com.whatsapp.system" });
        Assert.Equal("pass", r.Status);
    }

    [Fact]
    public void InstallPackages_SemAutorizacaoEPass()
    {
        var r = AndroidSecurityAudit.InstallPackages(AuditApps, new HashSet<string>());
        Assert.Equal("pass", r.Status);
        Assert.Equal("0 apps autorizadas a instalar APKs", r.Value);
    }

    [Fact]
    public void InstallPackages_PoliticaBloqueiaDerrubaWarnParaInfo()
    {
        var r = AndroidSecurityAudit.InstallPackages(AuditApps,
            new HashSet<string>(StringComparer.Ordinal) { "com.spy" }, unknownSourcesBlocked: true);
        Assert.Equal("info", r.Status);
        Assert.Contains("autorização guardada", r.Value);
        Assert.Contains("política", r.Note);
    }

    [Fact]
    public void InstallPackages_PoliticaDesconhecidaNaoInventaBloqueio()
    {
        var r = AndroidSecurityAudit.InstallPackages(AuditApps,
            new HashSet<string>(StringComparer.Ordinal) { "com.spy" }, unknownSourcesBlocked: null);
        Assert.Equal("warn", r.Status);
    }

    [Fact]
    public void Accessibility_TerceiroAtivoEWarn_SistemaNaoConta()
    {
        var userPkgs = new HashSet<string>(StringComparer.Ordinal) { "com.spy" };
        var services = new List<(string, string)>
        {
            ("com.google.android.marvin.talkback", "com.google.android.marvin.talkback.TalkBackService"),
            ("com.spy", "com.spy/.SpyService"),
        };
        var r = AndroidSecurityAudit.Accessibility(services, userPkgs);
        Assert.Equal("warn", r.Status);
        Assert.Contains("com.spy/.SpyService", r.Note);
        Assert.DoesNotContain("talkback", r.Note);
    }

    [Fact]
    public void Accessibility_SoSistemaEPass()
    {
        var userPkgs = new HashSet<string>(StringComparer.Ordinal) { "com.spy" };
        var services = new List<(string, string)>
        {
            ("com.google.android.marvin.talkback", "com.google.android.marvin.talkback.TalkBackService"),
        };
        var r = AndroidSecurityAudit.Accessibility(services, userPkgs);
        Assert.Equal("pass", r.Status);
    }

    [Fact]
    public void Accessibility_DefinicaoIlegivelESkipped()
    {
        var r = AndroidSecurityAudit.Accessibility(null, new HashSet<string>());
        Assert.Equal("skipped", r.Status);
    }

    [Theory]
    [InlineData(null, null, "skipped")]
    [InlineData(false, null, "warn")]
    [InlineData(true, false, "pass")]
    [InlineData(true, true, "pass")]
    // Android recente não expõe package_verifier_enable e as flags legadas
    // já não refletem o Play Protect: skipped honesto, nunca inferido.
    [InlineData(null, false, "skipped")]
    [InlineData(null, true, "skipped")]
    public void Verifier_Estados(bool? enabled, bool? verifyAdb, string status)
    {
        var r = AndroidSecurityAudit.Verifier(enabled, verifyAdb);
        Assert.Equal(status, r.Status);
    }

    [Fact]
    public void Verifier_AdbSemVerificacaoExplicaNaNota()
    {
        var r = AndroidSecurityAudit.Verifier(true, false);
        Assert.Contains("ADB", r.Note);
    }

    [Fact]
    public void DisabledApps_InventarioInformativo()
    {
        var r = AndroidSecurityAudit.DisabledApps(["com.a.old", "com.b.old"]);
        Assert.Equal("info", r.Status);
        Assert.Contains("2 app(s)", r.Value);
        Assert.Contains("com.a.old", r.Note);
    }
}
