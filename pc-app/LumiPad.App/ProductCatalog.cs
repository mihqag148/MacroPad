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
    DeviceDriverKind Driver,
    int? UsbVendorId = null,
    int? UsbProductId = null,
    ushort RawUsagePage = 0xFF60,
    ushort RawUsageId = 0x0061,
    byte RawReportId = 0);

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


    public static ProductDefinition PixelPro { get; } =
        CreateQmkProduct(
            "pixel-pro",
            "PIXEL PRO",
            "USB macro control pad",
            "PP-01",
            0x303A,
            0x4009,
            supportsBattery: false,
            rawReportId: 0);

    public static ProductDefinition CreateQmkProduct(
        string id,
        string name,
        string subtitle,
        string productCode,
        int vendorId,
        int productId,
        bool supportsBattery = false,
        byte rawReportId = 0) =>
        new(
            id,
            name,
            subtitle,
            productCode,
            supportsBattery,
            DeviceDriverKind.QmkRawHid,
            vendorId,
            productId,
            RawReportId: rawReportId);

    public static IReadOnlyList<ProductDefinition> All { get; } =
    [
        DialDesk,
        PixelPro
    ];
}
