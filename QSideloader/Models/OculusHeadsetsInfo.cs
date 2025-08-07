using System;
using System.Collections.Generic;

namespace QSideloader.Models;

public static class OculusHeadsetsInfo
{
    private static readonly List<OculusHeadset> Products =
    [
        new OculusHeadset(OculusHeadsetEnum.Quest1, "Quest 1", "vr_monterey", new[] {60, 72}),
        new OculusHeadset(OculusHeadsetEnum.Quest2, "Quest 2", "hollywood", new[] {72, 90, 120}),
        new OculusHeadset(OculusHeadsetEnum.Quest3, "Quest 3", "eureka", Array.Empty<int>()),
        new OculusHeadset(OculusHeadsetEnum.Quest3S, "Quest 3S", "panther", Array.Empty<int>()),
        new OculusHeadset(OculusHeadsetEnum.QuestPro, "Quest Pro", "seacliff", new[] {72, 90}),
        new OculusHeadset(OculusHeadsetEnum.Quest3SXbox, "Quest 3S Xbox Edition", "xse_panther", Array.Empty<int>())
    ];

    public static OculusHeadset GetProductInfo(string productName)
    {
        return Products.Find(x => x.ProductName == productName) ??
               new OculusHeadset(OculusHeadsetEnum.Unknown, $"Unknown ({productName})", productName, Array.Empty<int>());
    }

    public static bool IsKnownProduct(string productName)
    {
        return Products.Exists(x => x.ProductName == productName);
    }
}

public class OculusHeadset(
    OculusHeadsetEnum type,
    string name,
    string productName,
    IEnumerable<int> supportedRefreshRates)
{
    public OculusHeadsetEnum Type { get; } = type;
    public string Name { get; } = name;
    public string ProductName { get; } = productName;
    public IEnumerable<int> SupportedRefreshRates { get; } = supportedRefreshRates;
}

public enum OculusHeadsetEnum
{
    Quest1,
    Quest2,
    Quest3,
    Quest3S,
    QuestPro,
    Quest3SXbox,
    Unknown
}