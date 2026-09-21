using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))]
[Migration("20260918120000_AddEmploymentHistory")]
public sealed class AddEmploymentHistory : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        ALTER TABLE worker_employment_profiles ADD COLUMN revision integer NOT NULL DEFAULT 1 CHECK (revision > 0);
        CREATE TABLE worker_employment_history (
            id uuid PRIMARY KEY, care_worker_id uuid NOT NULL REFERENCES "CareWorkers"("Id") ON DELETE RESTRICT,
            organization_id uuid NOT NULL, branch_id uuid NOT NULL, revision integer NOT NULL CHECK (revision > 0),
            snapshot jsonb NOT NULL, change_reason text NOT NULL CHECK (length(trim(change_reason)) > 0),
            actor_user_id uuid NULL, actor text NOT NULL, recorded_at timestamptz NOT NULL DEFAULT now(),
            UNIQUE(care_worker_id,revision)
        );
        INSERT INTO worker_employment_history(id,care_worker_id,organization_id,branch_id,revision,snapshot,change_reason,actor,recorded_at)
        SELECT gen_random_uuid(),care_worker_id,organization_id,branch_id,revision,to_jsonb(p),
            'Baseline captured during employment versioning migration',approved_by,now()
        FROM worker_employment_profiles p;
        CREATE FUNCTION prevent_employment_history_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN RAISE EXCEPTION 'Employment history is immutable'; END $$;
        CREATE TRIGGER employment_history_immutable BEFORE UPDATE OR DELETE ON worker_employment_history
        FOR EACH ROW EXECUTE FUNCTION prevent_employment_history_mutation();
        """);
    protected override void Down(MigrationBuilder m) => m.Sql("""
        DROP TABLE worker_employment_history;
        DROP FUNCTION prevent_employment_history_mutation();
        ALTER TABLE worker_employment_profiles DROP COLUMN revision;
        """);
}
