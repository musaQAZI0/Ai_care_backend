using AiCare.Api;
using Xunit;
namespace AiCare.Tests;
public sealed class VisitLocationValidationTests
{
    [Fact]
    public void DistanceHandlesCoincidentPinsAntimeridianAndLongDistances()
    {
        Assert.Equal(0, VisitDeliveryController.DistanceMeters(51.5m, -0.1m, 51.5m, -0.1m));
        Assert.InRange(VisitDeliveryController.DistanceMeters(0, 0, 0, 1), 111000, 112000);
        Assert.InRange(VisitDeliveryController.DistanceMeters(0, 179.9m, 0, -179.9m), 22000, 22500);
        Assert.InRange(VisitDeliveryController.DistanceMeters(90, 0, -90, 0), 20000000, 20100000);
    }
    [Fact]
    public void RejectsMissingAccuracyStaleFutureAndOutOfRangeEvidence()
    {
        var now = DateTimeOffset.UtcNow;
        var good = new RecordVisitLocationRequest("CheckIn", 51.5m, -0.1m, 10, now, "Device", "");
        Assert.True(VisitDeliveryController.ValidLocation(good, now));
        foreach(var bad in new[] { good with { AccuracyMeters = null }, good with { AccuracyMeters = -1 },
            good with { AccuracyMeters = 10001 }, good with { CapturedAt = now.AddMinutes(-6) },
            good with { CapturedAt = now.AddMinutes(2) }, good with { Latitude = 91 }, good with { Longitude = -181 } })
            Assert.False(VisitDeliveryController.ValidLocation(bad, now));
    }
}
