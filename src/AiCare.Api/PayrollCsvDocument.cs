using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace AiCare.Api;

public sealed record PayrollCsvColumn(string Header,string Field,string? Constant=null);
public sealed record PayrollCsvDefinition(PayrollCsvColumn[] Columns,bool IncludeHeader,bool Utf8Bom,string DateFormat,int QuantityDecimals,int RateDecimals,string RoundingMode);
public sealed record PayrollCsvLine(Guid Id,string WorkerReference,string PayCode,string SourceType,DateOnly ServiceDate,string Currency,decimal Quantity,decimal UnitRate,decimal Amount);
public sealed record PayrollCsvArtifact(byte[] Content,string Sha256,string SourceSha256,int RowCount,decimal Total,SortedDictionary<string,decimal> WorkerTotals);

public static class PayrollCsvDocument
{
    public static readonly JsonSerializerOptions JsonOptions=new(JsonSerializerDefaults.Web);
    private static readonly string[] Fields=["WorkerReference","PayCode","SourceType","ServiceDate","Currency","Quantity","UnitRate","Amount","LineId","Constant"];
    public static string? Validate(PayrollCsvDefinition? d)
    {
        if(d is null||d.Columns is null||d.Columns.Length is <2 or >32)return "Choose 2 to 32 ordered CSV columns.";
        if(d.DateFormat is not("yyyy-MM-dd" or "dd/MM/yyyy")||d.QuantityDecimals is <0 or >8||d.RateDecimals is <0 or >8||d.RoundingMode is not("ToEven" or "AwayFromZero"))return "Choose an explicit supported date format, numeric precision and rounding policy.";
        if(d.Columns.Any(c=>c is null||!SafeText(c.Header,120)||!Fields.Contains(c.Field)||c.Field=="Constant"&&!SafeText(c.Constant??"",200,true)))return "CSV headers/constants must be bounded plain text and every column needs a supported field.";
        if(d.Columns.Select(c=>c.Header).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=d.Columns.Length)return "CSV headers must be unique.";
        if(!d.Columns.Any(c=>c.Field=="WorkerReference")||!d.Columns.Any(c=>c.Field=="PayCode")||
           !(d.Columns.Any(c=>c.Field=="Amount")||d.Columns.Any(c=>c.Field=="Quantity")&&d.Columns.Any(c=>c.Field=="UnitRate")))return "Include worker reference, pay code and either amount or both quantity and unit rate.";
        return null;
    }
    private static bool SafeText(string? value,int max,bool empty=false)=>value is not null&&value.Length<=max&&(empty||!string.IsNullOrWhiteSpace(value))&&
        !value.Any(char.IsControl)&&value.TrimStart().FirstOrDefault() is not('=' or '+' or '-' or '@');

    public static PayrollCsvArtifact Render(PayrollCsvDefinition d,IEnumerable<PayrollCsvLine> input,string currency,DateOnly from,DateOnly to)
    {
        var validation=Validate(d);if(validation is not null)throw new ArgumentException(validation);
        var lines=input.OrderBy(x=>x.WorkerReference,StringComparer.Ordinal).ThenBy(x=>x.ServiceDate).ThenBy(x=>x.SourceType,StringComparer.Ordinal).ThenBy(x=>x.Id).ToArray();
        if(lines.Length is 0 or >100000||lines.Select(x=>x.Id).Distinct().Count()!=lines.Length)throw new ArgumentException("Export requires a non-empty, bounded set of unique approved lines.");
        var rounding=d.RoundingMode=="ToEven"?MidpointRounding.ToEven:MidpointRounding.AwayFromZero;
        static string Quoted(string text)=>"\""+text.Replace("\"","\"\"")+"\"";
        var output=new StringBuilder();if(d.IncludeHeader)output.Append(string.Join(',',d.Columns.Select(c=>Quoted(c.Header)))).Append("\r\n");
        var workers=new SortedDictionary<string,decimal>(StringComparer.Ordinal);
        foreach(var line in lines)
        {
            if(line.Id==Guid.Empty||!SafeText(line.WorkerReference,120)||!SafeText(line.PayCode,120)||
               !SafeText(line.SourceType,120)||currency.Length!=3||!currency.All(char.IsAsciiLetterUpper)||line.Currency!=currency||line.ServiceDate<from||line.ServiceDate>to||line.Amount!=decimal.Round(line.Amount,2))
                throw new ArgumentException("Worker references, pay codes, dates, currency or monetary precision do not reconcile.");
            var quantity=decimal.Round(line.Quantity,d.QuantityDecimals,rounding);var rate=decimal.Round(line.UnitRate,d.RateDecimals,rounding);
            // Never change approved money to make a destination's rounding policy fit.
            if(decimal.Round(quantity*rate,2,rounding)!=line.Amount)throw new ArgumentException("Rounded quantity and rate do not reconcile to the approved amount. Use an appropriate approved template.");
            string Value(PayrollCsvColumn c)=>c.Field switch {
                "WorkerReference"=>line.WorkerReference,"PayCode"=>line.PayCode,"SourceType"=>line.SourceType,
                "ServiceDate"=>line.ServiceDate.ToString(d.DateFormat,CultureInfo.InvariantCulture),"Currency"=>line.Currency,
                "Quantity"=>quantity.ToString("F"+d.QuantityDecimals,CultureInfo.InvariantCulture),"UnitRate"=>rate.ToString("F"+d.RateDecimals,CultureInfo.InvariantCulture),
                "Amount"=>line.Amount.ToString("F2",CultureInfo.InvariantCulture),"LineId"=>line.Id.ToString(),"Constant"=>c.Constant??"",_=>throw new ArgumentException("Unsupported column.")};
            output.Append(string.Join(',',d.Columns.Select(c=>Quoted(Value(c))))).Append("\r\n");
            workers[line.WorkerReference]=workers.GetValueOrDefault(line.WorkerReference)+line.Amount;
        }
        var text=Encoding.UTF8.GetBytes(output.ToString());var content=d.Utf8Bom?Encoding.UTF8.GetPreamble().Concat(text).ToArray():text;
        return new(content,Hash(content),Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(lines,JsonOptions))),lines.Length,lines.Sum(x=>x.Amount),workers);
    }
    public static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static byte[] Key(IConfiguration configuration,string keyId)
    {
        if(string.IsNullOrWhiteSpace(keyId)||keyId.Length>40||!keyId.All(c=>char.IsAsciiLetterOrDigit(c)||c=='_'))throw new ArgumentException("Payroll export encryption key ID is invalid.");
        byte[] bytes;try{bytes=Convert.FromBase64String(configuration["PayrollExports:EncryptionKeys:"+keyId]??"");}catch(FormatException){throw new ArgumentException("Payroll export encryption key must be configured as 32 base64-encoded bytes.");}
        if(bytes.Length!=32)throw new ArgumentException("Payroll export encryption key must be configured as 32 base64-encoded bytes.");return bytes;
    }
    public static bool Configured(IConfiguration c){try{_ = Key(c,c["PayrollExports:ActiveKeyId"]??"");return true;}catch(ArgumentException){return false;}}
    public static (string KeyId,byte[] Payload) Protect(byte[] content,IConfiguration c,string context)
    {
        var id=c["PayrollExports:ActiveKeyId"]??"";var key=Key(c,id);var nonce=RandomNumberGenerator.GetBytes(12);var tag=new byte[16];var cipher=new byte[content.Length];
        using var aes=new AesGcm(key,16);aes.Encrypt(nonce,content,cipher,tag,Encoding.UTF8.GetBytes(context));
        return(id,nonce.Concat(tag).Concat(cipher).ToArray());
    }
    public static byte[] Unprotect(byte[] payload,string keyId,IConfiguration c,string context)
    {
        if(payload.Length<28)throw new CryptographicException("Invalid payroll artifact.");var result=new byte[payload.Length-28];
        using var aes=new AesGcm(Key(c,keyId),16);aes.Decrypt(payload.AsSpan(0,12),payload.AsSpan(28),payload.AsSpan(12,16),result,Encoding.UTF8.GetBytes(context));return result;
    }
}
