using System.Globalization;

namespace ArgumentMap.Contracts;

/// <summary>Keep the old double calculation/API, using MySQL's DECIMAL(20,18) scale for comparisons.</summary>
public static class PercentPrecision
{
    public const int Scale = 18;

    public static decimal ToStorage(double value) => decimal.Round(
        decimal.Parse(value.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture),
        Scale, MidpointRounding.AwayFromZero);

    public static double FromStorage(decimal value) => double.Parse(value.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    public static double Normalize(double value) => FromStorage(ToStorage(value));
}
