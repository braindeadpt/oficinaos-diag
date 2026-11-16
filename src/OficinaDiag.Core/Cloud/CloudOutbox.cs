using System.Globalization;
using System.Text.Json;

namespace OficinaDiag.Cloud;

/// <summary>
/// Fila durável dos envios à loja. Sem rede no momento, o relatório não se
/// perde: fica em disco com o corpo <em>já construído</em> e a chave de
/// idempotência que ia no pedido, e sai quando a ligação voltar.
///
/// Guardar o corpo e não o <c>DeviceReport</c> é o que permite fechar a app
/// sem perder o que o técnico preencheu no diálogo (nome, telefone, email).
/// </summary>
public sealed class CloudOutbox
{
    /// <summary>Um envio à espera. O corpo é o JSON do /intake, tal como sairia.</summary>
    public sealed record Envio(
        string ShopCode, string Corpo, string Chave, DateTimeOffset CriadoEm, int Tentativas);

    public delegate Task<(bool ok, string mensagem)> Enviador(
        string shopCode, string corpo, string chave, CancellationToken ct);

    /// <summary>Depois disto o envio já não interessa — e são dados de cliente.</summary>
    public const int IdadeMaximaDias = 30;

    /// <summary>Teto da fila: uma loja sem rede uma semana não enche o disco.</summary>
    public const int Maximo = 50;

    private readonly string _dir;
    private readonly Enviador _enviar;
    private readonly Func<DateTimeOffset> _agora;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CloudOutbox(string dir, Enviador enviar, Func<DateTimeOffset>? agora = null)
    {
        _dir = dir;
        _enviar = enviar;
        _agora = agora ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Pasta por omissão: ao lado do config, na AppData do utilizador.</summary>
    public static string PastaPorDefeito => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OficinaDiag", "outbox");

    public string Pasta => _dir;

    public int Pendentes => Listar().Count;

    /// <summary>
    /// Grava um envio falhado. Escrita atómica (tmp + move): um corte de
    /// corrente a meio não deixa um JSON pela metade na fila.
    /// </summary>
    public async Task GuardarAsync(string shopCode, string corpo, string chave, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_dir);
            var agora = _agora();
            var nome = agora.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N")[..8] + ".json";
            var envio = new Envio(shopCode, corpo, chave, agora, 0);
            var destino = Path.Combine(_dir, nome);
            var tmp = destino + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(envio), ct);
            File.Move(tmp, destino, true);
            Podar();
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Tenta enviar o que está em fila, do mais antigo para o mais recente, e
    /// devolve quantos saíram. Pára no primeiro que falhar: se a rede está em
    /// baixo, insistir nos outros 49 só gasta tempo do técnico.
    /// </summary>
    public async Task<int> DrenarAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var enviados = 0;
            foreach (var ficheiro in Listar())
            {
                Envio? envio;
                try { envio = JsonSerializer.Deserialize<Envio>(await File.ReadAllTextAsync(ficheiro, ct)); }
                catch (Exception) when (!ct.IsCancellationRequested) { envio = null; }

                // Um ficheiro estragado não pode travar os outros: sai da fila
                // para uma pasta de erro (fica para diagnóstico, não se apaga).
                if (envio is null) { Quarentena(ficheiro); continue; }

                if (_agora() - envio.CriadoEm > TimeSpan.FromDays(IdadeMaximaDias))
                {
                    Apagar(ficheiro);
                    continue;
                }

                bool ok;
                try { (ok, _) = await _enviar(envio.ShopCode, envio.Corpo, envio.Chave, ct); }
                catch (Exception) when (!ct.IsCancellationRequested) { ok = false; }

                if (ok) { Apagar(ficheiro); enviados++; continue; }

                try
                {
                    await File.WriteAllTextAsync(ficheiro,
                        JsonSerializer.Serialize(envio with { Tentativas = envio.Tentativas + 1 }), ct);
                }
                catch { /* sem escrita, fica como estava — não é motivo para perder o resto */ }
                break;
            }
            return enviados;
        }
        finally { _gate.Release(); }
    }

    private List<string> Listar() => Directory.Exists(_dir)
        ? Directory.GetFiles(_dir, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToList()
        : [];

    /// <summary>Corta os mais antigos quando a fila passa do teto.</summary>
    private void Podar()
    {
        var ficheiros = Listar();
        for (var i = 0; i < ficheiros.Count - Maximo; i++) Apagar(ficheiros[i]);
    }

    private void Quarentena(string ficheiro)
    {
        try
        {
            var erro = Path.Combine(_dir, "erro");
            Directory.CreateDirectory(erro);
            File.Move(ficheiro, Path.Combine(erro, Path.GetFileName(ficheiro)), true);
        }
        catch { Apagar(ficheiro); }
    }

    private static void Apagar(string ficheiro)
    {
        try { File.Delete(ficheiro); } catch { /* já não está lá */ }
    }
}
