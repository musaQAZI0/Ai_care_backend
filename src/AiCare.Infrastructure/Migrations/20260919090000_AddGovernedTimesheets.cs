using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))]
[Migration("20260919090000_AddGovernedTimesheets")]
public sealed class AddGovernedTimesheets : Migration
{
 protected override void Up(MigrationBuilder m)=>m.Sql("""
 CREATE TABLE timesheet_periods (
 id uuid PRIMARY KEY, organization_id uuid NOT NULL, branch_id uuid NOT NULL,
 period_start timestamptz NOT NULL, period_end timestamptz NOT NULL CHECK(period_end>period_start),
 hourly_rate numeric(12,4) NOT NULL CHECK(hourly_rate>=0), travel_rate numeric(12,4) NOT NULL CHECK(travel_rate>=0), mileage_rate numeric(12,4) NOT NULL CHECK(mileage_rate>=0),
 status text NOT NULL DEFAULT 'Open' CHECK(status IN ('Open','Locked')),
 revision integer NOT NULL DEFAULT 1, created_by uuid NOT NULL, locked_by uuid NULL, locked_at timestamptz NULL,
 payroll_run_id uuid UNIQUE NULL REFERENCES "PayrollRuns"("Id") ON DELETE RESTRICT,
 UNIQUE(organization_id,branch_id,period_start,period_end));
 CREATE TABLE timesheet_entries (
 id uuid PRIMARY KEY, period_id uuid NOT NULL REFERENCES timesheet_periods(id) ON DELETE RESTRICT,
 organization_id uuid NOT NULL, branch_id uuid NOT NULL, visit_id uuid NOT NULL REFERENCES "Visits"("Id") ON DELETE RESTRICT,
 care_worker_id uuid NOT NULL REFERENCES "CareWorkers"("Id") ON DELETE RESTRICT,
 planned_minutes integer NOT NULL, actual_minutes numeric NULL CHECK(actual_minutes>=0),
 payable_minutes numeric NULL CHECK(payable_minutes>=0), travel_minutes numeric NOT NULL DEFAULT 0 CHECK(travel_minutes>=0),
 mileage numeric(12,4) NOT NULL DEFAULT 0 CHECK(mileage>=0), hourly_rate numeric(12,4) NOT NULL CHECK(hourly_rate>=0),
 status text NOT NULL DEFAULT 'Draft' CHECK(status IN ('Draft','Submitted','Approved')),
 revision integer NOT NULL DEFAULT 1, reason text NOT NULL, changed_by uuid NOT NULL,
 submitted_by uuid NULL, approved_by uuid NULL, updated_at timestamptz NOT NULL DEFAULT now(),
 UNIQUE(organization_id,visit_id));
 CREATE TABLE timesheet_history (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), entry_id uuid NOT NULL REFERENCES timesheet_entries(id),
 snapshot jsonb NOT NULL, recorded_at timestamptz NOT NULL DEFAULT now());
 CREATE FUNCTION govern_timesheet_entry() RETURNS trigger LANGUAGE plpgsql AS $$
 DECLARE period timesheet_periods%ROWTYPE;
 BEGIN
 SELECT * INTO period FROM timesheet_periods WHERE id=coalesce(NEW.period_id,OLD.period_id) FOR UPDATE;
 IF period.status='Locked' THEN RAISE EXCEPTION 'Locked timesheets cannot change'; END IF;
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Timesheets cannot be deleted'; END IF;
 IF NEW.organization_id<>period.organization_id OR NEW.branch_id<>period.branch_id THEN RAISE EXCEPTION 'Timesheet scope mismatch'; END IF;
 IF TG_OP='UPDATE' AND (NEW.period_id<>OLD.period_id OR NEW.visit_id<>OLD.visit_id OR NEW.care_worker_id<>OLD.care_worker_id OR NEW.organization_id<>OLD.organization_id OR NEW.branch_id<>OLD.branch_id) THEN RAISE EXCEPTION 'Timesheet source cannot change'; END IF;
 RETURN NEW;
 END $$;
 CREATE TRIGGER timesheet_entry_guard BEFORE INSERT OR UPDATE OR DELETE ON timesheet_entries FOR EACH ROW EXECUTE FUNCTION govern_timesheet_entry();
 CREATE FUNCTION record_timesheet_history() RETURNS trigger LANGUAGE plpgsql AS $$
 BEGIN INSERT INTO timesheet_history(entry_id,snapshot) VALUES(NEW.id,to_jsonb(NEW)); RETURN NEW; END $$;
 CREATE TRIGGER timesheet_entry_history AFTER INSERT OR UPDATE ON timesheet_entries FOR EACH ROW EXECUTE FUNCTION record_timesheet_history();
 CREATE FUNCTION protect_timesheet_history() RETURNS trigger LANGUAGE plpgsql AS $$
 BEGIN RAISE EXCEPTION 'Timesheet history is immutable'; END $$;
 CREATE TRIGGER timesheet_history_guard BEFORE UPDATE OR DELETE ON timesheet_history FOR EACH ROW EXECUTE FUNCTION protect_timesheet_history();
 CREATE FUNCTION protect_locked_period() RETURNS trigger LANGUAGE plpgsql AS $$
 BEGIN
 IF OLD.status='Locked' AND (TG_OP='DELETE' OR (to_jsonb(NEW)-'payroll_run_id') IS DISTINCT FROM (to_jsonb(OLD)-'payroll_run_id') OR OLD.payroll_run_id IS NOT NULL) THEN RAISE EXCEPTION 'Locked period cannot change'; END IF;
 RETURN NEW;
 END $$;
 CREATE TRIGGER timesheet_period_guard BEFORE UPDATE OR DELETE ON timesheet_periods FOR EACH ROW EXECUTE FUNCTION protect_locked_period();
 ALTER TABLE finance_payroll_lines ALTER COLUMN payable_hours TYPE numeric(16,8), ALTER COLUMN hourly_rate TYPE numeric(12,4);
 ALTER TABLE finance_payroll_lines ADD COLUMN timesheet_entry_id uuid UNIQUE NULL REFERENCES timesheet_entries(id) ON DELETE RESTRICT;
 ALTER TABLE finance_payroll_lines ADD COLUMN travel_amount numeric(12,2) NOT NULL DEFAULT 0;
 """);
 protected override void Down(MigrationBuilder m)=>m.Sql("""
 ALTER TABLE finance_payroll_lines DROP COLUMN timesheet_entry_id, DROP COLUMN travel_amount;
 DROP TABLE timesheet_history; DROP TABLE timesheet_entries; DROP TABLE timesheet_periods;
 DROP FUNCTION protect_locked_period(); DROP FUNCTION protect_timesheet_history(); DROP FUNCTION record_timesheet_history(); DROP FUNCTION govern_timesheet_entry();
 """);
}
