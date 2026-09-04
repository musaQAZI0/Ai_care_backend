using Microsoft.EntityFrameworkCore.Infrastructure;using Microsoft.EntityFrameworkCore.Migrations;
#nullable disable
namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))][Migration("20260830120000_AddImportStaging")]
public sealed class AddImportStaging:Migration{protected override void Up(MigrationBuilder m)=>m.Sql("""
create table if not exists integration_import_batches(id uuid primary key,connector_id uuid null references integration_connectors(id) on delete set null,organization_id uuid not null,branch_id uuid not null,resource_type text not null,file_name text not null,file_format text not null,status text not null,mapping_json jsonb not null default '{}'::jsonb,total_rows integer not null default 0,valid_rows integer not null default 0,invalid_rows integer not null default 0,duplicate_rows integer not null default 0,created_by text not null,created_at timestamptz not null default now(),applied_at timestamptz null);
create table if not exists integration_import_rows(id uuid primary key,batch_id uuid not null references integration_import_batches(id) on delete cascade,row_number integer not null,source_json jsonb not null,mapped_json jsonb not null,validation_errors jsonb not null default '[]'::jsonb,duplicate_key text null,action text not null default 'Create',status text not null default 'Preview');
create unique index if not exists ux_import_batch_row on integration_import_rows(batch_id,row_number);create index if not exists ix_import_batches_tenant on integration_import_batches(organization_id,branch_id,created_at desc);
""");protected override void Down(MigrationBuilder m)=>m.Sql("drop table if exists integration_import_rows;drop table if exists integration_import_batches;");}
