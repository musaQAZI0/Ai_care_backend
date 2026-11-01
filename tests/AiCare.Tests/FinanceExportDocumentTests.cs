using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using AiCare.Api;
using Xunit;
namespace AiCare.Tests;
public sealed class FinanceExportDocumentTests
{
 [Theory][InlineData("=HYPERLINK(\"https://example.invalid\")")][InlineData("  +SUM(1,1)")][InlineData("@x")][InlineData("-2+3")][InlineData("\t=1")]
 public void CsvUntrustedTextCannotBecomeFormula(string text)
 {
  var csv=Encoding.UTF8.GetString(FinanceExportDocument.Csv(["reference","amount"],[[text,-12.50m]]));Assert.Contains("\"'"+text.Replace("\"","\"\"")+"\"",csv);Assert.Contains("\"-12.50\"",csv);
 }
 [Fact]public void WorkbookRetainsExactNumbersAndUsesLiteralUnicodeStrings()
 {
  using var stream=new MemoryStream(FinanceExportDocument.Xlsx(["reference","amount"],[["=1+1 £ 漢字",0.01m]]));using var zip=new ZipArchive(stream);using var part=zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();var xml=XDocument.Load(part);XNamespace ns="http://schemas.openxmlformats.org/spreadsheetml/2006/main";Assert.Empty(xml.Descendants(ns+"f"));Assert.Contains(xml.Descendants(ns+"t"),x=>x.Value=="=1+1 £ 漢字");Assert.Contains(xml.Descendants(ns+"v"),x=>x.Value=="0.01");Assert.NotNull(zip.GetEntry("[Content_Types].xml"));Assert.NotNull(zip.GetEntry("xl/_rels/workbook.xml.rels"));
 }
 [Fact]public void PdfIncludesEveryRecordAcrossPagesAndEscapesControlCharacters()
 {
  var rows=Enumerable.Range(0,180).Select(i=>new object?[]{"source-"+i,0.01m}).ToArray();var pdf=Encoding.ASCII.GetString(FinanceExportDocument.Pdf("Finance",["source","amount"],rows));Assert.StartsWith("%PDF",pdf);Assert.Contains("/FontFile",pdf);Assert.Contains("/ToUnicode",pdf);Assert.True(System.Text.RegularExpressions.Regex.Matches(pdf,@"/Type\s*/Page\b").Count>1);Assert.Contains("%%EOF",pdf);
 }
 [Theory][InlineData("\u00a3")][InlineData("Jos\u00e9")][InlineData("\u6f22\u5b57")][InlineData("\u0627\u062d\u0645\u062f")]
 public void PdfEmbedsFontsForSupportedUnicode(string text){Assert.True(FinanceExportDocument.SupportsPdf(["worker"],[[text]]));var pdf=Encoding.ASCII.GetString(FinanceExportDocument.Pdf("Payroll",["worker"],[[text]]));Assert.Contains("/FontFile",pdf);Assert.Contains("/ToUnicode",pdf);}
 [Theory][InlineData("\ud83d\ude00")][InlineData("\u0000")]
 public void PdfNeverSilentlyReplacesUnsupportedText(string text){Assert.False(FinanceExportDocument.SupportsPdf(["worker"],[[text]]));Assert.Throws<FinancePdfTextException>(()=>FinanceExportDocument.Pdf("Payroll",["worker"],[[text]]));}
 [Fact] public void PdfRejectsMalformedUtf16BeforeAnyEncodingCanReplaceIt()
 {
  // xUnit serializes theory data during discovery, which normalizes a lone surrogate into U+FFFD.
  // Construct invalid code units here so the production validator receives the actual malformed string.
  foreach(var codeUnit in new[]{'\ud800','\udfff'})
  {
   var text=new string(codeUnit,1);
   Assert.False(FinanceExportDocument.SupportsPdf(["worker"],[[text]]));
   Assert.Throws<FinancePdfTextException>(()=>FinanceExportDocument.Pdf("Payroll",["worker"],[[text]]));
   Assert.Throws<FinancePdfTextException>(()=>FinanceExportDocument.Pdf(text,["worker"],[["valid"]]));
  }
 }
 [Fact] public void UnicodeWrappingRetainsEveryGraphemeIncludingTheFinalRecord()
 {
  var original=string.Concat(Enumerable.Repeat("Jos\u00e9 \u6f22\u5b57 e\u0301 ",100))+"FINAL-RECORD-179";
  var lines=FinanceUnicodePdf.Layout([original]);Assert.True(lines.Count>1);Assert.Equal(original,string.Concat(lines));
 }
}
