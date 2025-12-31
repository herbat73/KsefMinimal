namespace KsefMinimal.Models;

public class ProductLineInfo
{
    public string ProductName { get; set; }
    public double NetPrice { get; set; }
    public double VatRate { get; set; } = 23;
    public double Qty { get; set; } = 1;
    public string Meassure { get; set; } = "szt";
}