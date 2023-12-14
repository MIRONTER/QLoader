using System;
using System.Collections.Generic;

namespace QSideloader.Models;

public static class OculusProductsInfo
{
    private static readonly List<OculusProduct> Products =
    [
        new OculusProduct(OculusProductType.Quest1, "Quest 1", "vr_monterey", new[] {60, 72}),
        new OculusProduct(OculusProductType.Quest2, "Quest 2", "hollywood", new[] {72, 90, 120}),
        new OculusProduct(OculusProductType.Quest3, "Quest 3", "eureka", Array.Empty<int>()),
        new OculusProduct(OculusProductType.QuestPro, "Quest Pro", "seacliff", new[] {72, 90})
    ];

    public static OculusProduct GetProductInfo(string productName)
    {
        return Products.Find(x => x.ProductName == productName) ??
               throw new KeyNotFoundException("Unknown device productName");
    }

    public static bool IsKnownProduct(string productName)
    {
        return Products.Exists(x => x.ProductName == productName);
    }
}

public class OculusProduct(
    OculusProductType type,
    string name,
    string productName,
    IEnumerable<int> supportedRefreshRates)
{
    public OculusProductType Type { get; } = type;
    public string Name { get; } = name;
    public string ProductName { get; } = productName;
    public IEnumerable<int> SupportedRefreshRates { get; } = supportedRefreshRates;
}

public enum OculusProductType
{
    Quest1,
    Quest2,
    Quest3,
    QuestPro
}