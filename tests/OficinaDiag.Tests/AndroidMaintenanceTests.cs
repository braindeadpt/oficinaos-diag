using System.Collections.Concurrent;
using OficinaDiag.Maintenance;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// O runner de limpeza contra um shell falso — prova a sequência de comandos
/// que vai para o telefone e que uma ação falhada não aborta as restantes.
/// </summary>
public class AndroidMaintenanceTests
{
    /// <summary>Shell falso: regista os comandos e responde conforme a tabela.</summary>
    private sealed class FakeShell
    {
        public ConcurrentQueue<string> Commands { get; } = new();
        private readonly Func<string, string?> _responder;

        public FakeShell(Func<string, string?> responder) => _responder = responder;

        public Task<string?> Run(string cmd)
        {
            Commands.Enqueue(cmd);
            return Task.FromResult(_responder(cmd));
        }
    }

    private static string? SnapshotResponder(string cmd) => cmd switch
    {
        "df /data" => "Filesystem 1K-blocks Used Available Use% Mounted on\n/dev/block/dm-7 115296000 82345678 30472322 74% /data\n",
        "cat /proc/meminfo" => "MemTotal: 7700000 kB\nMemAvailable: 3123456 kB\n",
        "cat /proc/uptime" => "12345.67 89012.34",
        "dumpsys battery" => "level: 87\ntemperature: 312",
        "ps -A 2>/dev/null | wc -l" => "123",
        // as 4 settings numa só resposta (o runner junta-as com ';')
        _ when cmd.Contains("settings get global") => "1.0\n0.5\nnull\n1",
        // pm/am/settings de ação não devolvem nada no sucesso — o runner só
        // precisa de distinguir falha (Error/Exception) de ausência de saída.
        _ => "Success",
    };

    [Fact]
    public async Task SnapshotAsync_PreencheAsMetricasDasLeituras()
    {
        var fake = new FakeShell(SnapshotResponder);
        var maint = new AndroidMaintenance(fake.Run);
        var s = await maint.SnapshotAsync();

        Assert.Equal(30472322L * 1024, s.StorageFreeBytes);
        Assert.Equal(82345678L * 1024, s.StorageUsedBytes);
        Assert.Equal(3123456L * 1024, s.MemAvailableBytes);
        Assert.Equal(87, s.BatteryLevelPct);
        Assert.Equal(31.2, s.BatteryTempC);
        Assert.Equal(12345, s.UptimeSeconds);
        Assert.Equal(123, s.RunningProcesses);
        Assert.Equal(1, s.LowPowerMode);
        Assert.Equal(1.0, s.Animations!.Window);
        Assert.Null(s.Animations.Animator); // "null" do sistema não é escala medida
    }

    [Fact]
    public async Task RunCleanupAsync_CorreAntesAcoesEDepoisNessaOrdem()
    {
        var fake = new FakeShell(SnapshotResponder);
        var maint = new AndroidMaintenance(fake.Run);
        var plan = new MaintenancePlan
        {
            TrimCaches = true,
            ClearCachePackages = ["com.whatsapp"],
            ZeroAnimations = true,
            KillBackgroundApps = true,
            BatterySaver = true,
        };
        var report = await maint.RunCleanupAsync(plan);

        var cmds = fake.Commands.ToList();
        // O primeiro e o último bloco de medições são os snapshots.
        Assert.Equal("df /data", cmds.First());
        Assert.Equal("pm trim-caches 512G", cmds[6]);
        Assert.Contains("pm clear --cache-only com.whatsapp", cmds);
        Assert.Contains("settings put global window_animation_scale 0", cmds);
        Assert.Contains("settings put global transition_animation_scale 0", cmds);
        Assert.Contains("settings put global animator_duration_scale 0", cmds);
        Assert.Contains("am kill-all", cmds);
        Assert.Contains("settings put global low_power 1", cmds);
        // o "depois" volta a medir — o df aparece duas vezes
        Assert.Equal(2, cmds.Count(c => c == "df /data"));

        Assert.NotNull(report.Before);
        Assert.NotNull(report.After);
        Assert.Contains(report.Actions, a => a.Key == "trim-caches" && a.Status == "ok");
        Assert.Contains(report.Actions, a => a.Key == "animations-off");
        // guarda os valores lidos no "antes" — é para eles que o repor volta
        Assert.Equal(1.0, report.AnimationsBefore!.Window);
    }

    [Fact]
    public async Task RunCleanupAsync_UmaAccaoFalhadaNaoAbortaAsSeguintes()
    {
        var fake = new FakeShell(cmd => cmd switch
        {
            "pm trim-caches 512G" => "Error: nope",
            "pm clear --cache-only com.a" => "Error: Failed to clear package",
            "pm clear --cache-only com.b" => "Success",
            _ => SnapshotResponder(cmd),
        });
        var maint = new AndroidMaintenance(fake.Run);
        var report = await maint.RunCleanupAsync(new MaintenancePlan
        {
            TrimCaches = true,
            ClearCachePackages = ["com.a", "com.b"],
            KillBackgroundApps = true,
        });

        // trim falhou mas o kill-all correu na mesma
        Assert.Contains(fake.Commands, c => c == "am kill-all");
        var trim = report.Actions.Single(a => a.Key == "trim-caches");
        Assert.Equal("fail", trim.Status);
        // uma app falhou, a outra passou → parcial
        var clear = report.Actions.Single(a => a.Key == "clear-cache");
        Assert.Equal("partial", clear.Status);
        Assert.Contains("1/2", clear.Detail);
    }

    [Fact]
    public async Task ClearCacheApps_NomeHostilNaoChegaAoShell()
    {
        var fake = new FakeShell(SnapshotResponder);
        var maint = new AndroidMaintenance(fake.Run);
        var report = await maint.RunCleanupAsync(new MaintenancePlan
        {
            TrimCaches = false,
            ClearCachePackages = ["com.ok", "com.evil; rm -rf /", "com.evil$(id)"],
        });
        var cmds = fake.Commands.ToList();
        Assert.Contains("pm clear --cache-only com.ok", cmds);
        Assert.DoesNotContain(cmds, c => c.Contains("rm -rf"));
        Assert.DoesNotContain(cmds, c => c.Contains("$(id)"));
        var clear = report.Actions.Single(a => a.Key == "clear-cache");
        Assert.Contains("2 nome(s) inválido(s) ignorado(s)", clear.Detail);
    }

    [Fact]
    public async Task RestoreAnimations_ValoresNuncaEscritosUsamDelete()
    {
        var fake = new FakeShell(SnapshotResponder);
        var maint = new AndroidMaintenance(fake.Run);
        // window=1.0 (escrita), transition=null (nunca escrita → delete), animator=0.5
        var report = await maint.RunCleanupAsync(new MaintenancePlan
        {
            TrimCaches = false,
            RestoreAnimations = new AnimationScales(1.0, null, 0.5),
        });
        var cmds = fake.Commands.ToList();
        Assert.Contains("settings put global window_animation_scale 1", cmds);
        Assert.Contains("settings delete global transition_animation_scale", cmds);
        Assert.Contains("settings put global animator_duration_scale 0.5", cmds);
        Assert.Contains(report.Actions, a => a.Key == "animations-restore" && a.Status == "ok");
    }

    [Fact]
    public async Task ListUserPackagesAsync_DevolveSoAppsDeUtilizador()
    {
        var fake = new FakeShell(cmd => cmd == "pm list packages -3"
            ? "package:com.whatsapp\npackage:com.instagram.android\n"
            : SnapshotResponder(cmd));
        var maint = new AndroidMaintenance(fake.Run);
        var apps = await maint.ListUserPackagesAsync();
        Assert.Equal(["com.whatsapp", "com.instagram.android"], apps);
    }

    [Fact]
    public async Task RunCleanupAsync_ClearCacheSemConfirmacaoContaComoFalha()
    {
        // stdout vazio: a falha ficou só no stderr, que o adb deita fora —
        // o genérico IsSuccess contaria "" como sucesso.
        var fake = new FakeShell(cmd => cmd.StartsWith("pm clear", StringComparison.Ordinal)
            ? ""
            : SnapshotResponder(cmd));
        var maint = new AndroidMaintenance(fake.Run);
        var report = await maint.RunCleanupAsync(new MaintenancePlan
        {
            TrimCaches = false,
            ClearCachePackages = ["com.a"],
        });
        var clear = report.Actions.Single(a => a.Key == "clear-cache");
        Assert.Equal("fail", clear.Status);
    }

    [Fact]
    public async Task RunCleanupAsync_SegundaLimpezaMantemAsOrigensMaisAntigas()
    {
        var settings = "1.0\n0.5\nnull\n1";
        var fake = new FakeShell(cmd => cmd.Contains("settings get global")
            ? settings
            : SnapshotResponder(cmd));
        var maint = new AndroidMaintenance(fake.Run);
        var first = await maint.RunCleanupAsync(new MaintenancePlan { ZeroAnimations = true });
        Assert.Equal(1.0, first.AnimationsBefore!.Window);

        // O "antes" da 2.ª já lê os zeros que a 1.ª deixou — não pode virar a origem.
        settings = "0\n0\n0\n1";
        var second = await maint.RunCleanupAsync(
            new MaintenancePlan { ZeroAnimations = true }, first);
        Assert.Equal(1.0, second.AnimationsBefore!.Window);
        Assert.Equal(0.5, second.AnimationsBefore.Transition);
    }

    [Fact]
    public async Task RunCleanupAsync_SemAnimacoesPreservaAsOrigensGuardadas()
    {
        var settings = "1.0\n0.5\nnull\n1";
        var fake = new FakeShell(cmd => cmd.Contains("settings get global")
            ? settings
            : SnapshotResponder(cmd));
        var maint = new AndroidMaintenance(fake.Run);
        var first = await maint.RunCleanupAsync(new MaintenancePlan { ZeroAnimations = true });

        // Uma limpeza de cache não toca nas animações: as origens sobrevivem,
        // senão o "repor" ficava sem caminho depois de qualquer outra limpeza.
        settings = "0\n0\n0\n1";
        var second = await maint.RunCleanupAsync(
            new MaintenancePlan { TrimCaches = true }, first);
        Assert.Equal(1.0, second.AnimationsBefore!.Window);
    }

    [Fact]
    public async Task SnapshotAsync_SemLeituraDasSettings_DevolveAnimacoesDesconhecidas()
    {
        var fake = new FakeShell(cmd => cmd.Contains("settings get global")
            ? null
            : SnapshotResponder(cmd));
        var maint = new AndroidMaintenance(fake.Run);
        var s = await maint.SnapshotAsync();
        Assert.Null(s.Animations);
        Assert.Null(s.LowPowerMode);
    }

    [Fact]
    public void MaintenancePlan_HasAnyAction_ReflecteAsEscolhas()
    {
        Assert.False(new MaintenancePlan { TrimCaches = false }.HasAnyAction);
        Assert.True(new MaintenancePlan { TrimCaches = true }.HasAnyAction);
        Assert.True(new MaintenancePlan { TrimCaches = false, BatterySaver = false }.HasAnyAction);
        Assert.False(new MaintenancePlan { TrimCaches = false, BatterySaver = null }.HasAnyAction);
    }
}
