namespace OficinaDiag.Reports;

/// <summary>
/// Dados que o técnico introduz para o relatório de seguradora —
/// o DeviceReport traz o estado do equipamento, isto traz o contexto
/// do sinistro e o carimbo da loja.
/// </summary>
public sealed class InsuranceForm
{
    public required string InsuredName { get; init; }
    public string? Insurer { get; init; }
    public string? PolicyNo { get; init; }
    public required string DamageDescription { get; init; }
    public string? RepairEstimate { get; init; }
    public string? EstimatedCost { get; init; }
    public string? Technician { get; init; }

    public required string ShopName { get; init; }
    public string? ShopNif { get; init; }
    public string? ShopPhone { get; init; }
    public string? ShopAddress { get; init; }
}
