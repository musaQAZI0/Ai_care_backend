using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261013110000_CareOperationOptions")]
public sealed class CareOperationOptions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table if not exists care_operation_options (
          id uuid primary key,
          organization_id uuid not null,
          kind text not null,
          value text not null,
          active boolean not null default true,
          created_by text not null default 'Migration',
          created_at timestamptz not null default now(),
          updated_by text not null default '',
          updated_at timestamptz null,
          constraint ck_care_operation_option_kind check(kind in ('DiaryCategory','CareProvided','DurationReason')),
          constraint uq_care_operation_option unique(organization_id,kind,value)
        );
        insert into care_operation_options(id,organization_id,kind,value)
        select gen_random_uuid(),o."Id",d.kind,d.value
        from "Organizations" o cross join (values
          ('DiaryCategory','General'),('DiaryCategory','Care delivery'),('DiaryCategory','Staffing'),
          ('DiaryCategory','Safeguarding'),('DiaryCategory','Medication'),('DiaryCategory','Family contact'),
          ('CareProvided','All planned care provided'),('CareProvided','Some planned care provided'),
          ('CareProvided','No planned care provided'),
          ('DurationReason','Client request'),('DurationReason','Care needs changed'),
          ('DurationReason','Carer delay'),('DurationReason','Other')
        ) d(kind,value)
        where true
        on conflict(organization_id,kind,value) do nothing;
        create or replace function seed_care_operation_options() returns trigger language plpgsql as $$
        begin
          insert into care_operation_options(id,organization_id,kind,value)
          select gen_random_uuid(),new."Id",d.kind,d.value from (values
            ('DiaryCategory','General'),('DiaryCategory','Care delivery'),('DiaryCategory','Staffing'),
            ('DiaryCategory','Safeguarding'),('DiaryCategory','Medication'),('DiaryCategory','Family contact'),
            ('CareProvided','All planned care provided'),('CareProvided','Some planned care provided'),
            ('CareProvided','No planned care provided'),
            ('DurationReason','Client request'),('DurationReason','Care needs changed'),
            ('DurationReason','Carer delay'),('DurationReason','Other')
          ) d(kind,value);
          return new;
        end $$;
        create trigger trg_seed_care_operation_options after insert on "Organizations"
          for each row execute function seed_care_operation_options();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        drop trigger if exists trg_seed_care_operation_options on "Organizations";
        drop function if exists seed_care_operation_options();
        drop table if exists care_operation_options;
        """);
}
