using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
namespace AiCare.Api;

public sealed class FinancePdfTextException() : ArgumentException("The document contains a glyph, control character or text sequence unsupported by the embedded PDF fonts. Use a lossless CSV or XLSX export where available.");

/// <summary>Data-only exports: strings never become spreadsheet formulas; numbers retain their type.</summary>
public static class FinanceExportDocument
{
 public static bool SupportsPdf(IReadOnlyList<string> headers,IReadOnlyList<object?[]> rows)=>headers.Concat(rows.SelectMany(r=>r.Select(x=>Convert.ToString(x,CultureInfo.InvariantCulture)??""))) is var text && FinanceUnicodePdf.Supports(text);
 public static byte[] Csv(IReadOnlyList<string> headers,IReadOnlyList<object?[]> rows)
 {
  static string Cell(object? value){var s=Convert.ToString(value,CultureInfo.InvariantCulture)??"";if(value is string&&s.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@' or '\t' or '\r')s="'"+s;return "\""+s.Replace("\"","\"\"")+"\"";}
  var output=new StringBuilder();output.AppendLine(string.Join(',',headers.Select(x=>Cell(x))));foreach(var row in rows)output.AppendLine(string.Join(',',row.Select(Cell)));return Encoding.UTF8.GetBytes(output.ToString());
 }
 public static byte[] Xlsx(IReadOnlyList<string> headers,IReadOnlyList<object?[]> rows)
 {
  XNamespace sheet="http://schemas.openxmlformats.org/spreadsheetml/2006/main",rel="http://schemas.openxmlformats.org/officeDocument/2006/relationships",package="http://schemas.openxmlformats.org/package/2006/relationships";
  using var memory=new MemoryStream();using(var zip=new ZipArchive(memory,ZipArchiveMode.Create,true))
  {
   void Part(string path,XElement content){using var stream=zip.CreateEntry(path).Open();new XDocument(content).Save(stream);}
   XNamespace ct="http://schemas.openxmlformats.org/package/2006/content-types";
   Part("[Content_Types].xml",new XElement(ct+"Types",new XElement(ct+"Default",new XAttribute("Extension","rels"),new XAttribute("ContentType","application/vnd.openxmlformats-package.relationships+xml")),new XElement(ct+"Default",new XAttribute("Extension","xml"),new XAttribute("ContentType","application/xml")),new XElement(ct+"Override",new XAttribute("PartName","/xl/workbook.xml"),new XAttribute("ContentType","application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),new XElement(ct+"Override",new XAttribute("PartName","/xl/worksheets/sheet1.xml"),new XAttribute("ContentType","application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"))));
   XElement Relationship(string id,string type,string target)=>new(package+"Relationship",new XAttribute("Id",id),new XAttribute("Type",rel.NamespaceName+"/"+type),new XAttribute("Target",target));
   Part("_rels/.rels",new XElement(package+"Relationships",Relationship("rId1","officeDocument","xl/workbook.xml")));
   Part("xl/workbook.xml",new XElement(sheet+"workbook",new XElement(sheet+"sheets",new XElement(sheet+"sheet",new XAttribute("name","Finance"),new XAttribute("sheetId",1),new XAttribute(rel+"id","rId1")))));
   Part("xl/_rels/workbook.xml.rels",new XElement(package+"Relationships",Relationship("rId1","worksheet","worksheets/sheet1.xml")));
   XElement Cell(object? value)=>value is decimal or int or long or double?new(sheet+"c",new XElement(sheet+"v",Convert.ToString(value,CultureInfo.InvariantCulture))):new(sheet+"c",new XAttribute("t","inlineStr"),new XElement(sheet+"is",new XElement(sheet+"t",new XAttribute(XNamespace.Xml+"space","preserve"),Convert.ToString(value,CultureInfo.InvariantCulture)??"")));
   Part("xl/worksheets/sheet1.xml",new XElement(sheet+"worksheet",new XElement(sheet+"sheetData",new[]{headers.Cast<object?>().ToArray()}.Concat(rows).Select((r,i)=>new XElement(sheet+"row",new XAttribute("r",i+1),r.Select(Cell))))));
  }
  return memory.ToArray();
 }
 public static byte[] Pdf(string title,IReadOnlyList<string> headers,IReadOnlyList<object?[]> rows)=>FinanceUnicodePdf.Create(title,headers,rows);
}
