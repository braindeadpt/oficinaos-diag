using Microsoft.Data.Sqlite;

namespace OficinaDiag.WhatsApp;

/// <summary>
/// Agenda iOS (<c>AddressBook.sqlitedb</c>, domínio HomeDomain do backup) →
/// mapa dígitos-do-número → nome, para resolver JIDs sem nome na BD
/// WhatsApp. O WhatsApp iOS não guarda nomes de contactos — mostra-os a
/// partir da agenda do sistema, que é esta.
/// </summary>
internal static class WaContacts
{
    /// <summary>
    /// Devolve mapa de dígitos (como aparecem no JID, ex. «351912345678»)
    /// → «Primeiro Último» ou organização. Chaves duplicadas: fica a
    /// primeira — o mesmo número em dois contactos não tem resposta certa.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> Read(string path)
    {
        using var db = WaStore.OpenReadOnly(path);
        db.Open();

        // ABMultiValue.property=3 são telefones; value vem formatado.
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT mv.value, TRIM(
                     COALESCE(p.First, '') || ' ' || COALESCE(p.Last, '')),
                   p.Organization
            FROM ABMultiValue mv
            JOIN ABPerson p ON p.ROWID = mv.record_id
            WHERE mv.property = 3 AND mv.value IS NOT NULL
            """;
        var map = new Dictionary<string, string>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var name = r.IsDBNull(1) || r.GetString(1) == ""
                ? (r.IsDBNull(2) ? null : r.GetString(2))
                : r.GetString(1);
            if (name is null) continue;
            var digits = Digits(r.GetString(0));
            if (digits is not null)
                map.TryAdd(digits, name);
        }
        return map;
    }

    /// <summary>
    /// Normaliza um número para o formato JID: só dígitos, «00»→código
    /// internacional sem «+». Números curtos/não-telefónicos (extensões,
    /// «*123») devolvem null — não correspondem a JID nenhum.
    /// </summary>
    private static string? Digits(string raw)
    {
        var d = new string(raw.Where(char.IsDigit).ToArray());
        if (d.StartsWith("00")) d = d[2..];
        return d.Length < 9 ? null : d;
    }

    /// <summary>
    /// Resolve o dígito-local de um JID («3519…@s.whatsapp.net») contra o
    /// mapa de contactos — exato primeiro, depois últimos 9 dígitos
    /// (contacto gravado sem indicativo de país).
    /// </summary>
    internal static string? Lookup(
        IReadOnlyDictionary<string, string> contacts, string jid)
    {
        var local = jid.Split('@')[0];
        if (contacts.TryGetValue(local, out var name)) return name;
        if (local.Length > 9 &&
            contacts.TryGetValue(local[^9..], out name))
            return name;
        return null;
    }
}
