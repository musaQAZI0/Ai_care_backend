using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace AiCare.Tests;
// Only synthetic fixture configuration. This does not migrate or infer production VAT treatment.
internal static class ApprovedVatTestData
{
 internal static async Task Ensure(CareDbContext db,Guid org,Guid branch,string service,decimal rate=0)
 {
  if(await db.Database.SqlQueryRaw<int>("select count(*)::int as \"Value\" from finance_vat_treatments where organization_id={0} and branch_id={1} and lower(service_type)=lower({2}) and approved_by is not null",org,branch,service).SingleAsync()>0)return;
  var adviser=Guid.NewGuid();var creator=Guid.NewGuid();var grantor=Guid.NewGuid();var authority=Guid.NewGuid();var treatment=Guid.NewGuid();db.AppUsers.Add(new AppUser(adviser,$"vat.{adviser:N}",$"vat.{adviser:N}@test.invalid",PasswordHasher.HashPassword("Admin123!"),UserRole.BackOffice,true,org,branch));await db.SaveChangesAsync();
  await db.Database.ExecuteSqlRawAsync("""
   insert into finance_vat_authorities(id,organization_id,branch_id,adviser_user_id,qualification_reference,valid_from,valid_to,created_by,approved_by,approved_at,request_snapshot) values({0},{1},{2},{3},'SYNTHETIC-QUALIFICATION','2000-01-01','2099-12-31',{4},{5},now(),'{{}}');
   insert into finance_vat_treatments(id,organization_id,branch_id,service_type,valid_from,valid_to,treatment,rate,tax_point,rounding_mode,reference,created_by,approved_by,approved_at,authority_id,request_snapshot) values({6},{1},{2},{7},'2000-01-01','2099-12-31',{8},{9},'ServiceDate','AwayFromZero','EXPLICIT-SYNTHETIC-VAT',{4},{3},now(),{0},'{{}}');
   """,authority,org,branch,adviser,creator,grantor,treatment,service,rate>0?"Taxable":"ZeroRated",rate);
 }
 internal sealed record Scope(Guid Org,Guid Branch,string Service,decimal Rate);
}
