using OficinaDiag.Devices;
using Xunit;

namespace OficinaDiag.Tests;

public sealed class AndroidAppsTests
{
    [Fact]
    public void ParsePackageLines_PathComPaddingEPacoteComInstaller()
    {
        const string output =
            "package:/data/app/~~abc==/com.exemplo-xyz==/base.apk=com.exemplo.app installer=com.android.packageinstaller\r\n" +
            "package:com.sem.path installer=null\r\n" +
            "lixo\r\n";

        var found = AndroidAppParsing.ParsePackageLines(output);

        Assert.Equal(2, found.Count);
        Assert.Equal("com.exemplo.app", found[0].Package);
        Assert.Equal("com.android.packageinstaller", found[0].Installer);
        Assert.Equal("com.sem.path", found[1].Package);
        Assert.Null(found[1].Installer);
    }

    [Fact]
    public void ParseHomePackage_ComponenteEPackageSemBarra()
    {
        Assert.Equal("com.google.android.apps.nexuslauncher",
            AndroidAppParsing.ParseHomePackage(
                "priority=0 preferredOrder=0 match=0x108000\n" +
                "com.google.android.apps.nexuslauncher/.NexusLauncherActivity\n"));
        Assert.Equal("com.android.launcher3",
            AndroidAppParsing.ParseHomePackage("com.android.launcher3\n"));
        Assert.Null(AndroidAppParsing.ParseHomePackage("No activity found\n"));
    }

    [Fact]
    public void Actions_UserEnabled_DesinstalaEForcaParagem()
    {
        var e = new AndroidAppEntry
        {
            PackageName = "com.exemplo.app",
            Kind = AndroidAppKind.User,
            State = AndroidAppState.Enabled,
        };

        var kinds = AndroidAppRules.ActionsFor(e, null).Select(a => a.Kind).ToList();

        Assert.Equal([AndroidAppActionKind.Uninstall, AndroidAppActionKind.ForceStop], kinds);
        Assert.Null(AndroidAppRules.ActionsFor(e, null)[0].Undo);
    }

    [Fact]
    public void Actions_SystemEnabled_DesativaComUndoEnable()
    {
        var e = new AndroidAppEntry
        {
            PackageName = "com.oem.bloat",
            Kind = AndroidAppKind.System,
            State = AndroidAppState.Enabled,
        };

        var disable = Assert.Single(AndroidAppRules.ActionsFor(e, null),
            a => a.Kind == AndroidAppActionKind.Disable);

        Assert.Equal("pm disable-user --user 0 com.oem.bloat", disable.Command);
        Assert.Equal(AndroidAppActionKind.Enable, disable.Undo!.Kind);
        Assert.Equal("pm enable --user 0 com.oem.bloat", disable.Undo.Command);
    }

    [Fact]
    public void Actions_PacoteProtegidoOuLauncher_NaoTemAcoes()
    {
        AndroidAppEntry Entry(string p) => new()
        {
            PackageName = p,
            Kind = AndroidAppKind.System,
            State = AndroidAppState.Enabled,
        };

        Assert.Empty(AndroidAppRules.ActionsFor(Entry("com.android.systemui"), null));
        Assert.Empty(AndroidAppRules.ActionsFor(Entry("com.google.android.gms"), null));
        Assert.Empty(AndroidAppRules.ActionsFor(Entry("com.launcher.atual"), "com.launcher.atual"));
    }

    [Fact]
    public void Actions_SystemUninstalled_ReporComUndoUninstall()
    {
        var e = new AndroidAppEntry
        {
            PackageName = "com.oem.removido",
            Kind = AndroidAppKind.System,
            State = AndroidAppState.Uninstalled,
        };

        var restore = Assert.Single(AndroidAppRules.ActionsFor(e, null));

        Assert.Equal(AndroidAppActionKind.Restore, restore.Kind);
        Assert.Equal("cmd package install-existing --user 0 com.oem.removido", restore.Command);
        Assert.Equal(AndroidAppActionKind.Uninstall, restore.Undo!.Kind);
    }

    [Fact]
    public async Task Manager_ListaUserSystemDisabledEUninstalled()
    {
        var calls = new List<string>();
        var manager = new AndroidAppManager("adb", async (serial, command, _, _) =>
        {
            calls.Add(command);
            return command switch
            {
                "pm list packages -3 -i -f --user 0" =>
                    "package:/data/app/x=br.com.app installer=com.android.packageinstaller\n",
                "pm list packages -s -i -f --user 0" =>
                    "package:/system/app/y=com.android.systemui\n" +
                    "package:/system/app/z=com.oem.bloat\n",
                "pm list packages -d -i -f --user 0" =>
                    "package:com.oem.off\n",
                "pm list packages -s -u -i -f --user 0" =>
                    "package:/system/app/z=com.oem.bloat\n" +
                    "package:com.oem.removido\n",
                "cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.HOME" =>
                    "priority=0\ncom.oem.launcher/.Home\n",
                _ => "",
            };
        });

        var inv = await manager.ListAsync("SERIAL1");

        Assert.Null(inv.Error);
        Assert.Equal("com.oem.launcher", inv.HomePackage);
        var user = Assert.Single(inv.Apps, a => a.PackageName == "br.com.app");
        Assert.Equal(AndroidAppKind.User, user.Kind);
        Assert.Contains(user.Actions, a => a.Kind == AndroidAppActionKind.Uninstall);
        var bloat = Assert.Single(inv.Apps, a => a.PackageName == "com.oem.bloat");
        Assert.Equal(AndroidAppState.Enabled, bloat.State);
        Assert.Contains(bloat.Actions, a => a.Kind == AndroidAppActionKind.Disable);
        var off = Assert.Single(inv.Apps, a => a.PackageName == "com.oem.off");
        Assert.Equal(AndroidAppState.Disabled, off.State);
        var removed = Assert.Single(inv.Apps, a => a.PackageName == "com.oem.removido");
        Assert.Equal(AndroidAppState.Uninstalled, removed.State);
        Assert.Empty(Assert.Single(inv.Apps, a => a.PackageName == "com.android.systemui").Actions);
    }

    [Fact]
    public async Task RunAsync_SucessoDevolveUndo()
    {
        var manager = new AndroidAppManager("adb", (_, command, _, _) =>
            Task.FromResult<string?>(command == "pm disable-user --user 0 com.oem.bloat" ? "Success" : null));
        var action = new AndroidAppAction(AndroidAppActionKind.Disable, "com.oem.bloat",
            "pm disable-user --user 0 com.oem.bloat",
            new AndroidAppAction(AndroidAppActionKind.Enable, "com.oem.bloat",
                "pm enable --user 0 com.oem.bloat"));

        var r = await manager.RunAsync("SERIAL1", action);

        Assert.True(r.Ok);
        Assert.Equal(AndroidAppActionKind.Enable, r.Undo!.Kind);
    }

    [Fact]
    public async Task RunAsync_PacoteProtegidoNaoExecutaAdb()
    {
        var calls = 0;
        var manager = new AndroidAppManager("adb", (_, _, _, _) =>
        {
            calls++;
            return Task.FromResult<string?>("Success");
        });
        var action = AndroidAppRules.CommandFor(AndroidAppActionKind.ForceStop, "com.android.systemui")
            is { } cmd
            ? new AndroidAppAction(AndroidAppActionKind.ForceStop, "com.android.systemui", cmd)
            : throw new InvalidOperationException();

        var r = await manager.RunAsync("SERIAL1", action);

        Assert.False(r.Ok);
        Assert.Equal("protected", r.Error);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RunAsync_FailureDoPmNaoEDesfeito()
    {
        var manager = new AndroidAppManager("adb", (_, _, _, _) =>
            Task.FromResult<string?>("Failure [DELETE_FAILED_INTERNAL_ERROR]"));
        var action = new AndroidAppAction(AndroidAppActionKind.Uninstall, "br.com.app",
            "pm uninstall -k --user 0 br.com.app");

        var r = await manager.RunAsync("SERIAL1", action);

        Assert.False(r.Ok);
        Assert.Contains("DELETE_FAILED", r.Error);
    }
}
