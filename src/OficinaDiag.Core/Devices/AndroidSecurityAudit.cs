using System.Text.RegularExpressions;

namespace OficinaDiag.Devices;

/// <summary>
/// Auditoria de segurança ao nível das apps (pacote Pro, ideia 4): origem
/// dos installs, permissões sensíveis, REQUEST_INSTALL_PACKAGES, serviços
/// de acessibilidade, verificação de pacotes (Play Protect) e apps
/// desativadas. Parsing e decisões são puros — strings de adb entram,
/// <see cref="AndroidDecisions.Reading"/> sai — para serem testados sem
/// dispositivo, no mesmo padrão do <see cref="AndroidDecisions"/>.
///
/// Tudo aqui é só leitura: a decisão de desinstalar, rever permissões ou
/// repor definições é sempre do técnico e do cliente, nunca automática.
/// </summary>
internal static class AndroidSecurityAudit
{
    /// <summary>Uma app de utilizador com o instalador que o `pm` reportou.</summary>
    internal sealed record UserApp(string Package, string? Installer)
    {
        /// <summary>
        /// Só a Play Store dispensa revisão de origem. Lojas alternativas
        /// (Galaxy Store, GetApps…) e origem desconhecida (installer null —
        /// típico de `adb install`) contam como "fora da Play Store".
        /// </summary>
        internal bool FromPlayStore =>
            string.Equals(Installer, "com.android.vending", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Permissões perigosas que uma app de terceiros não devia deter sem
    /// revisão: SMS, contactos, registos de chamadas e identidade do
    /// telefone — o conjunto clássico de apps de vigilância/spyware.
    /// </summary>
    private static readonly HashSet<string> SensitivePermissionSet = new(StringComparer.Ordinal)
    {
        "android.permission.SEND_SMS",
        "android.permission.RECEIVE_SMS",
        "android.permission.READ_SMS",
        "android.permission.READ_CONTACTS",
        "android.permission.WRITE_CONTACTS",
        "android.permission.GET_ACCOUNTS",
        "android.permission.READ_CALL_LOG",
        "android.permission.WRITE_CALL_LOG",
        "android.permission.PROCESS_OUTGOING_CALLS",
        "android.permission.ANSWER_PHONE_CALLS",
        "android.permission.READ_PHONE_STATE",
        "android.permission.READ_PHONE_NUMBERS",
    };

    private static readonly Regex PackageLine = new(@"^package:([^\s]+)", RegexOptions.Compiled);
    private static readonly Regex InstallerInline = new(@"\binstaller=(\S+)", RegexOptions.Compiled);
    private static readonly Regex PackageHeader = new(@"^\s+Package \[([^\]]+)\]", RegexOptions.Compiled);
    private static readonly Regex GrantedPermission = new(
        @"^\s+(android\.permission\.[A-Za-z0-9_]+):\s*granted=true\b", RegexOptions.Compiled);
    private static readonly Regex SharedUserHeader = new(@"^\s+SharedUser \[", RegexOptions.Compiled);
    private static readonly Regex SharedMember = new(
        @"PackageSetting\{[^ ]+ ([^/]+)/\d+\}", RegexOptions.Compiled);
    private static readonly Regex AppOpsPackageLine = new(@"^Package (\S+):$", RegexOptions.Compiled);
    private static readonly Regex AppOpsOpParens = new(
        @"^REQUEST_INSTALL_PACKAGES \((\w+)\)", RegexOptions.Compiled);
    private static readonly Regex AppOpsOpMode = new(
        @"^REQUEST_INSTALL_PACKAGES: mode=(\w+)", RegexOptions.Compiled);
    private static readonly Regex UserInfoLine = new(
        @"UserInfo\{(\d+):", RegexOptions.Compiled);

    // ── Parsing (adb → dados) ────────────────────────────────────────────

    /// <summary>
    /// `pm list packages -3 -i` — apps de utilizador com o pacote instalador.
    /// <c>null</c> quando o adb não respondeu (≠ lista vazia: telemóvel sem
    /// apps de utilizador é um caso real e dá checks a zero).
    /// </summary>
    internal static List<UserApp>? ParseUserPackages(string? pmOutput)
    {
        if (pmOutput is null) return null;
        var apps = new List<UserApp>();
        string? name = null;
        string? installer = null;
        void Flush()
        {
            if (name is not null) apps.Add(new UserApp(name, NormalizeInstaller(installer)));
            name = null;
            installer = null;
        }
        foreach (var raw in pmOutput.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var m = PackageLine.Match(line);
            if (m.Success)
            {
                Flush();
                name = m.Groups[1].Value;
                var ins = InstallerInline.Match(line);
                installer = ins.Success ? ins.Groups[1].Value : null;
                continue;
            }
            // Algumas versões do pm imprimem o installer na linha seguinte.
            if (name is not null && installer is null)
            {
                var ins = InstallerInline.Match(line);
                if (ins.Success) installer = ins.Groups[1].Value;
            }
        }
        Flush();
        return apps;
    }

    /// <summary>O pm imprime `installer=null` quando não há origem conhecida.</summary>
    private static string? NormalizeInstaller(string? raw) =>
        raw is null || raw.Length == 0 || raw == "null" ? null : raw;

    /// <summary>
    /// `dumpsys package` completo — permissões concedidas por pacote.
    /// Um só dump: `dumpsys package &lt;pkg&gt;` por app eram ~100 arranques
    /// de JVM no telemóvel; o dump total transfere uma vez e filtra-se aqui.
    /// Só conta `granted=true` — `requested` e `granted=false` não dão risco.
    ///
    /// Apps com UID partilhado não têm permissões na secção "Packages:" —
    /// vivem no bloco "Shared users:" e aplicam-se a todos os membros, por
    /// isso os grants partilhados são replicados para cada membro. Sem a
    /// secção "Packages:" o dump está truncado/corrompido: devolve null e o
    /// collector faz skipped em vez de dar luz verde às cegas.
    /// </summary>
    internal static List<(string Package, string Permission)>? ParsePackageDumpGrants(string? dump)
    {
        if (dump is null) return null;
        var grants = new List<(string, string)>();
        var inPackages = false;
        var inShared = false;
        var sawPackages = false;
        string? current = null;
        List<string>? sharedMembers = null;
        List<string>? sharedGrants = null;

        void FlushShared()
        {
            if (sharedMembers is not null && sharedGrants is not null)
                foreach (var member in sharedMembers)
                    foreach (var perm in sharedGrants)
                        grants.Add((member, perm));
            sharedMembers = null;
            sharedGrants = null;
        }

        foreach (var raw in dump.Split('\n'))
        {
            // Linhas vazias (\n ou \r\n) não são cabeçalhos — não podem fechar a secção.
            if (string.IsNullOrWhiteSpace(raw)) continue;
            // Secções de topo começam na coluna 0; dentro delas tudo é indentado.
            if (raw[0] is not (' ' or '\t'))
            {
                var header = raw.TrimEnd();
                inPackages = string.Equals(header, "Packages:", StringComparison.Ordinal);
                if (inPackages) sawPackages = true;
                inShared = string.Equals(header, "Shared users:", StringComparison.Ordinal);
                if (!inShared) FlushShared();
                current = null;
                continue;
            }
            if (inPackages)
            {
                var pkg = PackageHeader.Match(raw);
                if (pkg.Success)
                {
                    current = pkg.Groups[1].Value;
                    continue;
                }
                if (current is null) continue;
                var g = GrantedPermission.Match(raw);
                if (g.Success) grants.Add((current, g.Groups[1].Value));
                continue;
            }
            if (!inShared) continue;
            if (SharedUserHeader.IsMatch(raw))
            {
                FlushShared();
                sharedMembers = new List<string>();
                sharedGrants = new List<string>();
                continue;
            }
            if (sharedMembers is null) continue;
            var member = SharedMember.Match(raw);
            if (member.Success)
            {
                sharedMembers.Add(member.Groups[1].Value);
                continue;
            }
            var sg = GrantedPermission.Match(raw);
            if (sg.Success) sharedGrants!.Add(sg.Groups[1].Value);
        }
        FlushShared();
        return sawPackages ? grants : null;
    }

    /// <summary>
    /// `dumpsys appops` — pacotes com REQUEST_INSTALL_PACKAGES em modo
    /// <c>allow</c>. A permissão é <c>signature|appop</c>: o "install
    /// permissions" do dumpsys package mostra sempre <c>granted=false</c>
    /// para apps de utilizador e a autorização real ("instalar apps
    /// desconhecidos") vive nos AppOps. Formato real (Android 14):
    /// <c>Uid u0aXXX:</c> → <c>Package com.app:</c> →
    /// <c>REQUEST_INSTALL_PACKAGES (allow):</c> (ou <c>: mode=allow</c>).
    /// </summary>
    internal static HashSet<string>? ParseInstallPackagesAppOps(string? appopsOutput)
    {
        if (appopsOutput is null) return null;
        // Sem o cabeçalho do serviço o output não é um dumpsys appops —
        // melhor skipped do que um "0 apps" inventado.
        if (!appopsOutput.Contains("AppOps Service state", StringComparison.Ordinal)) return null;
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        string? current = null;
        foreach (var raw in appopsOutput.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var pkg = AppOpsPackageLine.Match(line);
            if (pkg.Success)
            {
                current = pkg.Groups[1].Value;
                continue;
            }
            if (current is null) continue;
            var parens = AppOpsOpParens.Match(line);
            var mode = parens.Success ? parens.Groups[1].Value
                : AppOpsOpMode.Match(line) is { Success: true } m ? m.Groups[1].Value
                : null;
            // "allow" no Android moderno; "0" = MODE_ALLOWED em dumpsys
            // antigos (Android 8 imprime o modo numérico).
            if (mode is "allow" or "0") allowed.Add(current);
        }
        return allowed;
    }

    /// <summary>
    /// `dumpsys user` — a restrição <c>no_install_unknown_sources</c> nas
    /// "Effective restrictions" do utilizador atual bloqueia instalações de
    /// fontes desconhecidas a nível do UserManager, por cima dos AppOps:
    /// autorizações guardadas ficam inertes enquanto a política durar.
    /// null = não se conseguiu determinar (adb mudo ou utilizador atual
    /// ilegível) — nunca se assume bloqueio nem a ausência dele.
    /// </summary>
    internal static bool? ParseUnknownSourcesBlocked(string? dumpsysUser)
    {
        if (dumpsysUser is null) return null;
        string? currentUser = null;
        foreach (var raw in dumpsysUser.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Current user:", StringComparison.Ordinal))
            {
                var id = line["Current user:".Length..].Trim();
                if (int.TryParse(id, out _)) currentUser = id;
                break;
            }
        }
        if (currentUser is null) return null;

        var inTarget = false;
        var inEffective = false;
        var found = false;
        foreach (var raw in dumpsysUser.Split('\n'))
        {
            if (raw.Length > 0 && raw[0] is not (' ' or '\t'))
            {
                // Cabeçalhos de topo ("Users:", "Device properties:", …) fecham blocos.
                inTarget = false;
                inEffective = false;
                continue;
            }
            var user = UserInfoLine.Match(raw);
            if (user.Success)
            {
                inTarget = user.Groups[1].Value == currentUser;
                inEffective = false;
                continue;
            }
            if (!inTarget) continue;
            var trimmed = raw.Trim();
            if (trimmed == "Effective restrictions:")
            {
                inEffective = true;
                found = true;
                continue;
            }
            if (!inEffective) continue;
            // Os valores são mais indentados que o cabeçalho; a próxima chave
            // ("UserProperties:") está ao mesmo nível e fecha a lista.
            if (raw.Length - raw.TrimStart().Length <= 4) { inEffective = false; continue; }
            if (trimmed == "no_install_unknown_sources") return true;
        }
        return found ? false : null;
    }

    /// <summary>
    /// `settings get secure enabled_accessibility_services` — componentes
    /// `pacote/serviço` separados por `:`. "null"/vazio = nenhum ativo.
    /// </summary>
    internal static List<(string Package, string Service)>? ParseAccessibilityServices(string? output)
    {
        if (output is null) return null;
        var value = output.Trim();
        if (value.Length == 0 || value == "null") return new();
        var list = new List<(string, string)>();
        foreach (var entry in value.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var e = entry.Trim();
            var slash = e.IndexOf('/');
            if (slash <= 0 || slash == e.Length - 1) continue;
            list.Add((e[..slash], e[(slash + 1)..]));
        }
        return list;
    }

    /// <summary>
    /// Saída de `echo V1=$(settings get global package_verifier_enable); echo V2=…`.
    /// "1"/"0" → bool; chave ausente ou valor inesperado → null (não se inventa).
    /// </summary>
    internal static (bool? Enabled, bool? VerifyAdb) ParseVerifierSettings(string? output)
    {
        bool? enabled = null, verifyAdb = null;
        if (output is null) return (enabled, verifyAdb);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("V1=", StringComparison.Ordinal)) enabled = ParseFlag(line[3..]);
            else if (line.StartsWith("V2=", StringComparison.Ordinal)) verifyAdb = ParseFlag(line[3..]);
        }
        return (enabled, verifyAdb);
    }

    private static bool? ParseFlag(string v) => v.Trim() switch
    {
        "1" => true,
        "0" => false,
        _ => null,
    };

    /// <summary>`pm list packages -3 -d` — apps de utilizador desativadas mas ainda instaladas.</summary>
    internal static List<string>? ParseDisabledPackages(string? pmOutput)
    {
        if (pmOutput is null) return null;
        var list = new List<string>();
        foreach (var raw in pmOutput.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("package:", StringComparison.Ordinal)) continue;
            var rest = line["package:".Length..];
            var end = rest.IndexOfAny([' ', '\t']);
            var name = (end >= 0 ? rest[..end] : rest).Trim();
            if (name.Length > 0) list.Add(name);
        }
        return list;
    }

    // ── Decisões (dados → Reading) ───────────────────────────────────────

    /// <summary>Apps instaladas fora da Play Store — contexto de risco, não defeito.</summary>
    internal static AndroidDecisions.Reading Sideloaded(IReadOnlyList<UserApp> apps)
    {
        var external = apps.Where(a => !a.FromPlayStore).ToList();
        if (external.Count == 0)
            return new AndroidDecisions.Reading("pass", "0 apps fora da Play Store", null);
        var detail = string.Join(", ", external.Take(8)
            .Select(a => $"{a.Package} ({OriginLabel(a.Installer)})"));
        if (external.Count > 8) detail += $" … (+{external.Count - 8})";
        return new AndroidDecisions.Reading("info", $"{external.Count} app(s) fora da Play Store",
            detail + " — sideload não é defeito; é a superfície de risco a rever");
    }

    private static string OriginLabel(string? installer) => installer switch
    {
        null => "origem desconhecida",
        _ => installer,
    };

    /// <summary>
    /// Apps de utilizador com permissões sensíveis concedidas. O risco é
    /// uma app sideloaded a deter SMS/contactos; uma app da Play Store com
    /// as mesmas permissões é comum (WhatsApp, etc.) e fica informativa.
    /// </summary>
    internal static AndroidDecisions.Reading SensitivePermissions(
        IReadOnlyList<UserApp> apps, IReadOnlyList<(string Package, string Permission)> grants)
    {
        var appByPkg = new Dictionary<string, UserApp>(StringComparer.Ordinal);
        foreach (var a in apps) appByPkg.TryAdd(a.Package, a);

        var byApp = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var (pkg, perm) in grants)
        {
            if (!SensitivePermissionSet.Contains(perm) || !appByPkg.ContainsKey(pkg)) continue;
            if (!byApp.TryGetValue(pkg, out var set)) byApp[pkg] = set = new(StringComparer.Ordinal);
            set.Add(perm);
        }
        if (byApp.Count == 0)
            return new AndroidDecisions.Reading("pass", "0 apps com permissões sensíveis", null);

        var risky = new List<string>();
        var store = new List<string>();
        foreach (var (pkg, perms) in byApp.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var names = string.Join(", ", perms.Select(p => p["android.permission.".Length..]));
            var entry = $"{pkg}: {names} (via {OriginLabel(appByPkg[pkg].Installer)})";
            if (appByPkg[pkg].FromPlayStore) store.Add(entry); else risky.Add(entry);
        }

        var all = risky.Concat(store).ToList();
        var detail = string.Join(", ", all.Take(8)) + (all.Count > 8 ? $" … (+{all.Count - 8})" : "");
        if (risky.Count > 0)
            return new AndroidDecisions.Reading("warn",
                $"{all.Count} app(s) com permissões sensíveis ({risky.Count} fora da Play Store)",
                detail + " — SMS/contactos fora da loja oficial: rever com o cliente ou desinstalar se desconhecida");
        return new AndroidDecisions.Reading("info",
            $"{all.Count} app(s) com permissões sensíveis (todas da Play Store)",
            detail + " — permissões sensíveis são comuns em apps legítimos; sem sideload não é sinal de risco");
    }

    /// <summary>
    /// Apps de utilizador autorizadas a instalar APKs (AppOps
    /// REQUEST_INSTALL_PACKAGES em <c>allow</c> — o "instalar apps
    /// desconhecidos" das Definições). É o vetor de sideloading e
    /// persistência; desinstalar ou repor a permissão é decisão do técnico.
    /// <paramref name="unknownSourcesBlocked"/> = política do dispositivo
    /// (no_install_unknown_sources) bloqueia a instalação apesar das
    /// autorizações guardadas — nesse caso o risco é informativo.
    /// </summary>
    internal static AndroidDecisions.Reading InstallPackages(
        IReadOnlyList<UserApp> apps, ISet<string> appOpsAllowed, bool? unknownSourcesBlocked = null)
    {
        var holders = apps
            .Where(a => appOpsAllowed.Contains(a.Package))
            .Select(a => $"{a.Package} (via {OriginLabel(a.Installer)})")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        if (holders.Count == 0)
            return new AndroidDecisions.Reading("pass", "0 apps autorizadas a instalar APKs", null);

        var detail = string.Join(", ", holders.Take(8)) + (holders.Count > 8 ? $" … (+{holders.Count - 8})" : "");
        if (unknownSourcesBlocked == true)
            return new AndroidDecisions.Reading("info", $"{holders.Count} app(s) com autorização guardada",
                detail + " — mas a política do dispositivo bloqueia instalações de fontes desconhecidas; " +
                "a autorização só volta a contar se a política for levantada");
        return new AndroidDecisions.Reading("warn", $"{holders.Count} app(s) autorizadas a instalar APKs",
            detail + " — 'instalar apps desconhecidos' ativo; rever e desativar o que o cliente não reconhece");
    }

    /// <summary>
    /// Serviços de acessibilidade ativos (`enabled_accessibility_services`).
    /// Uma app de terceiros com acessibilidade lê o ecrã inteiro — o sinal
    /// clássico de stalkerware. Serviços de sistema (TalkBack…) ficam fora:
    /// só contam pacotes da lista de apps de utilizador.
    /// </summary>
    internal static AndroidDecisions.Reading Accessibility(
        IReadOnlyList<(string Package, string Service)>? services, ISet<string> userPackages)
    {
        if (services is null)
            return new AndroidDecisions.Reading("skipped", "definição ilegível",
                "enabled_accessibility_services não respondeu — acessibilidade não auditada");
        var thirdParty = services.Where(s => userPackages.Contains(s.Package)).ToList();
        if (thirdParty.Count == 0)
            return new AndroidDecisions.Reading("pass", "sem serviços de acessibilidade de terceiros", null);

        var detail = string.Join(", ", thirdParty.Take(8)
            .Select(s => $"{s.Package}/{s.Service}")) + (thirdParty.Count > 8 ? $" … (+{thirdParty.Count - 8})" : "");
        return new AndroidDecisions.Reading("warn", $"{thirdParty.Count} serviço(s) de acessibilidade ativo(s)",
            detail + " — apps com acessibilidade leem o ecrã; desativar o que o cliente não conhece");
    }

    /// <summary>
    /// Verificação de pacotes / Play Protect: `package_verifier_enable` e
    /// `verifier_verify_adb_installs`. Em Android recente a chave legada
    /// está ausente e as flags já não refletem o estado efetivo (o Play
    /// Protect não expõe estado via adb e uma política pode forçar a
    /// verificação) — nesse caso é skipped honesto, nunca inferido.
    /// </summary>
    internal static AndroidDecisions.Reading Verifier(bool? enabled, bool? verifyAdb)
    {
        if (enabled is null)
            return new AndroidDecisions.Reading("skipped", "verifier não exposto",
                "package_verifier_enable ausente neste Android e as flags legadas já não refletem o Play Protect — não verificável");
        if (!enabled.Value)
            return new AndroidDecisions.Reading("warn", "verificação de pacotes desligada",
                "Play Protect / verificação de apps desligada — apps novas não são verificadas antes de instalar");
        var note = verifyAdb == false
            ? "apps verificados antes de instalar; instalações via ADB não passam pelo verificador — conferir a origem dos APKs"
            : "apps verificados antes de instalar (Play Protect)";
        return new AndroidDecisions.Reading("pass", "verificação de pacotes ativa", note);
    }

    /// <summary>Apps desativadas mas ainda instaladas — inventário para a limpeza.</summary>
    internal static AndroidDecisions.Reading DisabledApps(IReadOnlyList<string> packages)
    {
        if (packages.Count == 0)
            return new AndroidDecisions.Reading("info", "0 apps desativadas", null);
        var detail = string.Join(", ", packages.Take(8)) + (packages.Count > 8 ? $" … (+{packages.Count - 8})" : "");
        return new AndroidDecisions.Reading("info", $"{packages.Count} app(s) desativada(s)",
            detail + " — desativadas continuam instaladas; candidatas a remoção na limpeza");
    }
}
