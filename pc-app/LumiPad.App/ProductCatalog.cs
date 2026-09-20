namespace LumiPad.App;

public enum DeviceDriverKind
{
    LumiZmk,
    QmkRawHid,
    Esp32Companion
}

public sealed record ProductDefinition(
    string Id,
    string Name,
    string Subtitle,
    string ProductCode,
    bool SupportsBattery,
    DeviceDriverKind Driver);

public static class ProductCatalog
{
    public static ProductDefinition DialDesk { get; } =
        new(
            "dial-desk",
            "DIAL DESK",
            "Wireless macro control desk",
            "DD-01",
            true,
            DeviceDriverKind.LumiZmk);

    public static IReadOnlyList<ProductDefinition> All { get; } =
    [
        DialDesk
    ];
}
