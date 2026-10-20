using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class PercentPrecisionTests
{
    [TestMethod]
    [DataRow(0d,"0"),DataRow(1d,"1"),DataRow(0.5d,"0.5")]
    [DataRow(0.12345678901234568d,"0.12345678901234568")]
    [DataRow(0.0000000000000000005d,"0.000000000000000001")]
    [DataRow(0.0000000000000000004d,"0")]
    public void StorageScale_RetainsRoundTripDigitsAndRoundsLikeMysql(double value,string expected)
    {
        Assert.AreEqual(decimal.Parse(expected,System.Globalization.CultureInfo.InvariantCulture),PercentPrecision.ToStorage(value));
        Assert.AreEqual(PercentPrecision.Normalize(value),PercentPrecision.Normalize(PercentPrecision.Normalize(value)));
    }

    [TestMethod]
    public void CoordinateFractions_NormalizationIsStableAcrossStorageCycles()
    {
        foreach(var length in new[]{1,3,7,30,97,99991,499999,500000})
        foreach(var numerator in new[]{0m,0.5m,1m,length/2m,length-0.5m,(decimal)length})
        {
            var value=(double)(numerator/length);var stored=PercentPrecision.ToStorage(value);
            var normalized=PercentPrecision.Normalize(value);
            for(var i=0;i<10;i++)
            {
                Assert.AreEqual(stored,PercentPrecision.ToStorage(normalized));
                normalized=PercentPrecision.FromStorage(PercentPrecision.ToStorage(normalized));
            }
            Assert.AreEqual((double)numerator,normalized*length,1e-9,"Storage cycles must preserve the intended coordinate.");
        }
    }
}
