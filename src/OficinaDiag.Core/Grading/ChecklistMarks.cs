namespace OficinaDiag.Grading;

/// <summary>
/// Marcas do checklist físico: 1 = ok · 2 = defeito · 3 = não testado · 0 = sem
/// marca. Vive fora do diálogo para a regra poder ser testada sem WPF — e para
/// nunca mais haver um indexer direto a rebentar em itens sem marca, que é o
/// caso normal de um relatório acabado de recolher.
/// </summary>
public static class ChecklistMarks
{
    /// <summary>
    /// Marca um item: premir o mesmo botão outra vez desmarca. Um item que
    /// ainda não tem marca vale 0 — ler com indexer aqui lançava
    /// `KeyNotFoundException` e a marca nunca pegava.
    /// </summary>
    public static int Toggle(IDictionary<string, int> marcas, string chave, int premida)
    {
        var atual = marcas.TryGetValue(chave, out var v) ? v : 0;
        var nova = atual == premida ? 0 : premida;
        marcas[chave] = nova;
        return nova;
    }

    /// <summary>Estado que a marca vira no relatório.</summary>
    public static string Status(int marca) => marca switch
    {
        1 => "pass",
        2 => "fail",
        _ => "skipped",
    };
}
