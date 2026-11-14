using System.Diagnostics;

namespace OficinaDiag.Devices;

/// <summary>
/// Executa ferramentas externas com timeout e cancelamento, matando a árvore
/// de processos: um <c>idevicediagnostics</c> órfão segura o relay e o scan
/// seguinte falha. Separado do coletor para poder ser provado com um
/// executável falso, sem iPhone nem Windows.
/// </summary>
internal static class ToolProcess
{
    /// <summary>
    /// Stdout do processo, ou <c>null</c> se não arrancou, estourou o timeout
    /// ou foi cancelado. Em qualquer um dos três a árvore é morta.
    /// </summary>
    internal static string? Run(string exe, string arguments, int timeoutMs,
        CancellationToken ct = default)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return null;

            // Sem `ct` nas leituras de propósito: com ele, cancelar fazia a
            // leitura lançar e o `catch` devolvia null por uma via lateral —
            // a guarda do cancelamento ficava sem dono e o comportamento
            // dependia de quem chegasse primeiro. A leitura termina quando o
            // pipe fecha (processo morto pelo kill) e a decisão «cancelado →
            // null» fica num sítio só, depois do `WaitForExit`.
            var stdout = p.StandardOutput.ReadToEndAsync();
            // Drenar o stderr: o buffer cheio bloqueia o processo antes do exit.
            _ = p.StandardError.ReadToEndAsync();

            // Cancelamento mata o filho logo, em vez de deixar órfãos.
            using var kill = ct.Register(() =>
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            });

            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            // Cancelado a meio: o kill do registo faz o processo sair antes do
            // timeout, e um `WaitForExit` bem-sucedido não distingue «acabou»
            // de «mataram-no». Sem esta guarda devolvia-se o stdout parcial
            // (muitas vezes "") como se a ferramenta tivesse corrido bem —
            // era isto que fazia o teste de cancelamento oscilar e que,
            // em produção, entregava uma lista vazia em vez de «cancelado».
            if (ct.IsCancellationRequested) return null;

            return stdout.Result;
        }
        catch { return null; }
    }
}
