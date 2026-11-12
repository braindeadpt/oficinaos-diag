using System.Windows;

namespace OficinaDiag;

/// <summary>
/// Swaps the active theme ResourceDictionary at runtime — all DynamicResource
/// lookups (brushes, fonts, button style) re-resolve live in every window.
/// </summary>
public static class ThemeManager
{
    public const string Terminal = "terminal";
    public const string Win95 = "win95";
    public const string Fluent = "fluent";

    public static readonly string[] All = { Terminal, Win95, Fluent };

    public static string Current { get; private set; } = Terminal;

    /// <summary>Tema mudou — janelas re-aplicam textos (as decorações [ ]/── dependem do tema).</summary>
    public static event Action? Changed;

    public static bool IsTerminal => Current == Terminal;

    public static void Apply(string name)
    {
        if (!All.Contains(name)) name = Terminal;
        var uri = $"pack://application:,,,/Themes/{name}.xaml";
        var previous = Current;
        var merged = Application.Current.Resources.MergedDictionaries;
        for (var i = merged.Count - 1; i >= 0; i--)
            if (merged[i].Source?.OriginalString.Contains("/Themes/") == true)
                merged.RemoveAt(i);
        merged.Add(new ResourceDictionary { Source = new Uri(uri) });
        Current = name;
        // O Core não conhece WPF: publica-se aqui se o chrome [ ]/── é o certo.
        // Atualiza-se mesmo quando o tema não mudou — a app pode arrancar com
        // o tema já aplicado e o valor por omissão do L10n não servir.
        L10n.UseTerminalChrome = IsTerminal;
        if (previous == name) return;
        Changed?.Invoke();
    }
}
