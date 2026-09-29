using System.Net.Http.Json;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiCare.Tests;

internal static class GovernedInvoiceTestData
{
    internal sealed record Event(Guid VisitId, Guid ServiceUserId, DateOnly ServiceDate, decimal Quantity, decimal UnitRate);
    internal sealed record InvoiceRow(Guid Id, Guid ServiceUserId, string InvoiceNumber, decimal Amount);

    internal static async Task<List<InvoiceRow>> GenerateAsync(PostgresRegressionFactory factory, HttpClient client, DateOnly start, DateOnly end, params Event[] events)
    {
        var funder=Guid.NewGuid();var card=Guid.NewGuid();var version=Guid.NewGuid();var rule=Guid.NewGuid();
        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            await db.Database.ExecuteSqlRawAsync("insert into finance_funders(id,organization_id,branch_id,name,normalized_name,funder_type,payment_terms_days,default_invoice_frequency,currency,created_by,updated_by) values({0},{1},{2},{3},{4},'Private',30,'Monthly','GBP','test','test');insert into finance_rate_cards(id,organization_id,branch_id,name,service_type,currency,created_by,updated_by) values({5},{1},{2},{6},'Care','GBP','test','test');insert into finance_rate_versions(id,rate_card_id,organization_id,branch_id,version,effective_from,status,created_by) values({7},{5},{1},{2},1,{8},'Active','test');insert into finance_rate_rules(id,rate_version_id,organization_id,rule_type,day_type,unit,unit_rate,created_by) values({9},{7},{1},'Standard','Any','Hour',{10},'test')",funder,TenantDefaults.OrganizationId,TenantDefaults.BranchId,$"Regression Funder {funder:N}",$"REGRESSION FUNDER {funder:N}",card,$"Regression Rate {card:N}",version,start,rule,events[0].UnitRate);
            foreach(var item in events)
            {
                var arrangement=Guid.NewGuid();var billable=Guid.NewGuid();var amount=Math.Round(item.Quantity*item.UnitRate,2);
                await db.Database.ExecuteSqlRawAsync("insert into funding_arrangements(id,service_user_id,organization_id,branch_id,funding_source,funder_name,authorized_hours_per_week,hourly_rate,valid_from,status,funder_id,rate_card_id,created_by) values({0},{1},{2},{3},'Private','Regression Funder',168,{4},{5},'Active',{6},{7},'test');insert into finance_billable_events(id,organization_id,branch_id,service_user_id,visit_id,funder_id,funding_arrangement_id,rate_version_id,rate_rule_id,service_date,quantity,unit,unit_rate,gross_amount,final_amount,currency,calculation_explanation,status,created_by,approved_at,approved_by) values({8},{2},{3},{1},{9},{6},{0},{10},{11},{5},{12},'Hour',{4},{13},{13},'GBP','Regression governed snapshot','Approved','test',now(),'test')",arrangement,item.ServiceUserId,TenantDefaults.OrganizationId,TenantDefaults.BranchId,item.UnitRate,item.ServiceDate,funder,card,billable,item.VisitId,version,rule,item.Quantity,amount);
            }
        }
        var response=await client.PostAsJsonAsync("/api/phase1/finance/invoice-runs",new{periodStart=start,periodEnd=end,funderId=funder,idempotencyKey=$"regression-{Guid.NewGuid():N}"});
        response.EnsureSuccessStatusCode();
        using var verify=factory.Services.CreateScope();var verifyDb=verify.ServiceProvider.GetRequiredService<CareDbContext>();
        var people=events.Select(x=>x.ServiceUserId).ToArray();
        return await (from invoice in verifyDb.Invoices.AsNoTracking() join detail in verifyDb.Database.SqlQueryRaw<InvoiceDetailRow>("select invoice_id as \"InvoiceId\",invoice_number as \"InvoiceNumber\" from finance_invoice_details") on invoice.Id equals detail.InvoiceId where people.Contains(invoice.ServiceUserId) select new InvoiceRow(invoice.Id,invoice.ServiceUserId,detail.InvoiceNumber,invoice.Amount)).ToListAsync();
    }

    private sealed record InvoiceDetailRow(Guid InvoiceId,string InvoiceNumber);
}
