using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))]
[Migration("20260921110000_CompleteInvoicing")]
public sealed class CompleteInvoicing:Migration
{
 protected override void Up(MigrationBuilder m)=>m.Sql("""
 create table finance_invoice_profiles(organization_id uuid not null,branch_id uuid not null,provider_name text not null,provider_address text not null default '',provider_email text not null default '',provider_phone text not null default '',company_number text not null default '',vat_number text not null default '',remittance_details text not null default '',default_payment_terms_days int not null default 30 check(default_payment_terms_days between 1 and 365),default_vat_rate numeric(5,2) not null default 0 check(default_vat_rate between 0 and 100),vat_exemption_reason text not null default '',updated_at timestamptz not null default now(),primary key(organization_id,branch_id));
 create table finance_invoice_sequences(organization_id uuid not null,invoice_year int not null,next_value bigint not null check(next_value>0),primary key(organization_id,invoice_year));
 create table finance_invoice_details(invoice_id uuid primary key references "Invoices"("Id") on delete cascade,organization_id uuid not null,branch_id uuid not null,invoice_number text not null,invoice_date date not null,due_date date not null,payment_terms text not null,provider_name text not null,provider_address text not null default '',provider_email text not null default '',provider_phone text not null default '',company_number text not null default '',vat_number text not null default '',customer_name text not null,customer_address text not null default '',funder_name text not null default '',remittance_details text not null default '',currency char(3) not null default 'GBP',net_amount numeric(12,2) not null check(net_amount>=0),vat_rate numeric(5,2) not null check(vat_rate between 0 and 100),vat_amount numeric(12,2) not null check(vat_amount>=0),gross_amount numeric(12,2) not null check(gross_amount>=0),vat_exemption_reason text not null default '',created_at timestamptz not null default now(),unique(organization_id,invoice_number),check(due_date>=invoice_date));
 with numbered as (select i.*,extract(year from i."IssuedAt")::int invoice_year,row_number() over(partition by i."OrganizationId",extract(year from i."IssuedAt") order by i."IssuedAt",i."Id") sequence_value from "Invoices" i)
 insert into finance_invoice_details(invoice_id,organization_id,branch_id,invoice_number,invoice_date,due_date,payment_terms,provider_name,customer_name,customer_address,funder_name,net_amount,vat_rate,vat_amount,gross_amount,vat_exemption_reason)
 select n."Id",n."OrganizationId",n."BranchId",'INV-'||n.invoice_year||'-'||to_char(n.sequence_value,'FM000000'),n."IssuedAt"::date,n."IssuedAt"::date+30,'Payment due within 30 days',coalesce(o."Name",'Care provider'),coalesce(s."FullName",'Customer'),coalesce(s."Address",''),n."Funder",n."Amount",0,0,n."Amount",'Legacy invoice - VAT treatment not recorded'
 from numbered n left join "Organizations" o on o."Id"=n."OrganizationId" left join "ServiceUsers" s on s."Id"=n."ServiceUserId";
 insert into finance_invoice_sequences(organization_id,invoice_year,next_value)
 select organization_id,extract(year from invoice_date)::int,count(*)+1 from finance_invoice_details group by organization_id,extract(year from invoice_date)::int
 on conflict(organization_id,invoice_year) do update set next_value=greatest(finance_invoice_sequences.next_value,excluded.next_value);
 create index ix_finance_invoice_details_due on finance_invoice_details(organization_id,due_date);
 create table finance_credit_notes(id uuid primary key,invoice_id uuid not null references "Invoices"("Id"),organization_id uuid not null,branch_id uuid not null,credit_number text not null,amount numeric(12,2) not null check(amount>0),reason text not null,status text not null default 'Issued',issued_at timestamptz not null default now(),issued_by text not null,unique(organization_id,credit_number));
 create index ix_finance_credit_invoice on finance_credit_notes(organization_id,invoice_id);
 create table finance_refunds(id uuid primary key,invoice_id uuid not null references "Invoices"("Id"),organization_id uuid not null,branch_id uuid not null,amount numeric(12,2) not null check(amount>0),reference text not null,reason text not null,refunded_at timestamptz not null default now(),refunded_by text not null,unique(organization_id,invoice_id,reference));
 create table finance_invoice_deliveries(id uuid primary key,invoice_id uuid not null references "Invoices"("Id"),organization_id uuid not null,branch_id uuid not null,recipient_email text not null,status text not null,detail text not null default '',delivered_at timestamptz null,requested_at timestamptz not null default now(),requested_by text not null);
 create index ix_finance_deliveries_invoice on finance_invoice_deliveries(organization_id,invoice_id,requested_at desc);
 alter table "Invoices" drop constraint if exists ck_invoice_governed_status;
 alter table "Invoices" add constraint ck_invoice_governed_status check("Status" in ('Generated','Approved','Issued','Part paid','Paid','Overdue','Credited','Void')) not valid;
 """);
 protected override void Down(MigrationBuilder m)=>m.Sql("""
 alter table "Invoices" drop constraint if exists ck_invoice_governed_status;
 drop table if exists finance_invoice_deliveries;drop table if exists finance_refunds;drop table if exists finance_credit_notes;drop table if exists finance_invoice_details;drop table if exists finance_invoice_sequences;drop table if exists finance_invoice_profiles;
 """);
}
