using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AiCare.Api;
using Microsoft.Extensions.Configuration;
using Xunit;
namespace AiCare.Tests;
public sealed class PayrollCsvDocumentTests
{
    public static PayrollCsvDefinition Definition => new([
        new("Worker", "WorkerReference"), new("Code", "PayCode"), new("Date", "ServiceDate"),
        new("Hours", "Quantity"), new("Rate", "UnitRate"), new("Total", "Amount")],true,false,"dd/MM/yyyy",4,4,"ToEven");
    static readonly DateOnly Day=new(2052,1,11);
    static PayrollCsvLine Line(string worker="WORKER-1")=>new(Guid.NewGuid(),worker,"LEAVE","Leave",Day,"GBP",2m,10m,20m);
    static PayrollCsvArtifact Render(PayrollCsvDefinition d,params PayrollCsvLine[] lines)=>PayrollCsvDocument.Render(d,lines,"GBP",Day,Day);

    [Fact] public void RetryBytesAndHashesAreStableAcrossSourceOrderAndCulture()
    {
        var lines=new[]{Line("W-2"),Line("W-1")}; var first=Render(Definition,lines);
        var before=CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture=new CultureInfo("fr-FR");var retry=Render(Definition,lines.Reverse().ToArray());Assert.Equal(first.Content,retry.Content);Assert.Equal(first.Sha256,retry.Sha256);Assert.Equal(first.SourceSha256,retry.SourceSha256); }
        finally { CultureInfo.CurrentCulture=before; }
        Assert.Equal(40m,first.Total);Assert.Equal(2,first.WorkerTotals.Count);
        Assert.Contains("\"11/01/2052\",\"2.0000\",\"10.0000\",\"20.00\"\r\n",Encoding.UTF8.GetString(first.Content));
    }
    [Fact] public void EscapesQuotesAndCommasAndHonoursBomAndHeaderChoice()
    {
        var output=Render(Definition with{Utf8Bom=true,IncludeHeader=false},Line("WORKER,\"1\""));
        Assert.Equal(new byte[]{239,187,191},output.Content.Take(3));
        Assert.StartsWith("\"WORKER,\"\"1\"\"\"",Encoding.UTF8.GetString(output.Content.AsSpan(3)));
    }
    [Theory][InlineData("=1+1")][InlineData(" +123")][InlineData("@cmd")][InlineData("-1")][InlineData("W\n1")][InlineData("")]
    public void RejectsUnsafeOrMissingWorkerReferencesWithoutAlteringIdentifiers(string worker)=>Assert.Throws<ArgumentException>(()=>Render(Definition,Line(worker)));
    [Fact] public void RejectsDuplicateSourcesCurrencyDateCodesAndPrecisionMismatch()
    {
        var line=Line();Assert.Throws<ArgumentException>(()=>Render(Definition,line,line));
        Assert.Throws<ArgumentException>(()=>Render(Definition,line with{Currency="EUR"}));
        Assert.Throws<ArgumentException>(()=>Render(Definition,line with{ServiceDate=Day.AddDays(1)}));
        Assert.Throws<ArgumentException>(()=>Render(Definition,line with{PayCode=""}));
        Assert.Throws<ArgumentException>(()=>Render(Definition,line with{Amount=20.001m}));
        Assert.Throws<ArgumentException>(()=>Render(Definition with{QuantityDecimals=0},line with{Quantity=1.25m,Amount=12.5m}));
        Assert.NotNull(PayrollCsvDocument.Validate(Definition with{RoundingMode=""}));
        Assert.NotNull(PayrollCsvDocument.Validate(Definition with{Columns=[new("Worker","WorkerReference"),new("Worker","PayCode")]}));
    }
    [Fact] public void ExplicitRoundingNeverChangesApprovedMoney()
    {
        var line=Line() with{Quantity=1.005m,UnitRate=1m,Amount=1m};
        Assert.Equal(1m,Render(Definition with{QuantityDecimals=2},line).Total);
        Assert.Throws<ArgumentException>(()=>Render(Definition with{QuantityDecimals=2,RoundingMode="AwayFromZero"},line));
    }
    [Fact] public void EncryptionBindsArtifactToTenantAndExportAndDetectsTampering()
    {
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{
            ["PayrollExports:ActiveKeyId"]="test",["PayrollExports:EncryptionKeys:test"]=Convert.ToBase64String(Enumerable.Repeat((byte)7,32).ToArray())}).Build();
        Assert.True(PayrollCsvDocument.Configured(config));var plain=Render(Definition,Line()).Content;
        var encrypted=PayrollCsvDocument.Protect(plain,config,"org:export");
        Assert.NotEqual(plain,encrypted.Payload);Assert.Equal(plain,PayrollCsvDocument.Unprotect(encrypted.Payload,encrypted.KeyId,config,"org:export"));
        Assert.ThrowsAny<CryptographicException>(()=>PayrollCsvDocument.Unprotect(encrypted.Payload,encrypted.KeyId,config,"other:export"));
        encrypted.Payload[^1]^=1;Assert.ThrowsAny<CryptographicException>(()=>PayrollCsvDocument.Unprotect(encrypted.Payload,encrypted.KeyId,config,"org:export"));
        config["PayrollExports:EncryptionKeys:test"]=null;Assert.False(PayrollCsvDocument.Configured(config));
        Assert.Throws<ArgumentException>(()=>PayrollCsvDocument.Unprotect(encrypted.Payload,encrypted.KeyId,config,"org:export"));
    }
}
