using System;
using System.Collections.Generic;

namespace QSideloader.Models;

public static class OculusProductsInfo
{
    private static readonly List<OculusProductProps> Products = new()
    {
        new OculusProductProps(OculusProductType.Quest1, "Quest 1", "vr_monterey", new[] {60, 72}),
        new OculusProductProps(OculusProductType.Quest2, "Quest 2", "hollywood", new[] {72, 90, 120}),
        new OculusProductProps(OculusProductType.Quest3, "Quest 3", "eureka", Array.Empty<int>()),
        new OculusProductProps(OculusProductType.QuestPro, "Quest Pro", "seacliff", new[] {72, 90})
    };

    public static OculusProductProps GetProductInfo(string productName)
    {
        return Products.Find(x => x.ProductName == productName) ??
               throw new KeyNotFoundException("Unknown device productName");
    }

    public static bool IsKnownProduct(string productName)
    {
        return Products.Exists(x => x.ProductName == productName);
    }
}

public class OculusProductProps
{
    public OculusProductType Type { get; }
    public string Name { get; }
    public string ProductName { get; }
    public IEnumerable<int> SupportedRefreshRates { get; }

    public OculusProductProps(OculusProductType type, string name, string productName,
        IEnumerable<int> supportedRefreshRates)
    {
        Type = type;
        Name = name;
        ProductName = productName;
        SupportedRefreshRates = supportedRefreshRates;
    }
}

public enum OculusProductType
{
    Quest1,
    Quest2,
    Quest3,
    QuestPro
}