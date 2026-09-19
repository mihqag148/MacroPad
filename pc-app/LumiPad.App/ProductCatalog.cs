namespace LumiPad.App;

public sealed record ProductDefinition(
    string Id,
    string Name,
    string Subtitle,
    string ProductCode,
    bool SupportsBattery);

public static class ProductCatalog
{
    public static IReadOnlyList<ProductDefinition> All { get; } =
    [
        new ProductDefinition(
            "dial-desk",
            "DIAL DESK",
            "Wireless macro control desk",
            "DD-01",
            true)
    ];
}
