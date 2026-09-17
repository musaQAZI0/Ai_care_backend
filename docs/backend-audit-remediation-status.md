# Backend audit remediation status ? 2026-09-17

Source: the three AI_CARE_BACKEND_AUDIT_PART_* documents in the parent workspace.
This is an implementation checkpoint, not a production-readiness sign-off.

## Applied locally

| Finding | Change | Verification |
|---|---|---|
| eMAR stock lost updates | Administration locks the medication safety profile before stock and PRN validation. Manual stock adjustments acquire the same row lock. Balances, ledger, MAR, escalations and audit commit together. | Build passes; new PostgreSQL concurrency tests compile but require a running test database. |
| Duplicate MAR finalization | Retains the existing unique terminal-ledger index, checks the conditional MAR update affected exactly one row, and returns conflict for that specific unique violation. | New same-MAR concurrent regression case. |
| eMAR idempotency commit gap | Serializes submissions for the same organization/actor/key with a transaction advisory lock; stores the replay key in the ledger transaction. | Existing replay regression remains; database execution pending. |
| Safeguarding final-risk validation | Normalizes and validates Low/Medium/High/Critical; closure evaluates the requested final risk and final referral evidence. | New High/Critical escalation regression cases. |
| Safeguarding partial writes and action/closure race | Case, action, event and audit writes share a transaction. Update/action paths lock the case first; closed cases reject new actions. | Build passes; PostgreSQL workflow regressions pending. |
| Safeguarding branch leakage | Case queries and locks enforce the existing ITenantContext.IsOrganizationWide/BranchId policy within the current organization. | Broader cross-branch regression expansion still required. |
| Render production test data | render.yaml sets TestingData__Enabled=false; Demo__Enabled already false. | Local configuration only; deployed dashboard overrides were not inspected or changed. |

## Validation

- `dotnet build backend/AiCare.Backend.sln --no-restore --verbosity quiet`: passed, 0 warnings, 0 errors.
- `dotnet test backend/AiCare.Backend.sln --no-restore --verbosity minimal --filter 'FullyQualifiedName!~Regression'`: 113 passed, 0 failed.
- New regression tests were compiled in the test run above but excluded from execution by that filter.
- PostgreSQL is not listening on localhost:5432; Docker Desktop's Linux engine is unavailable. Database regression execution is pending.
- No deployment, migration application, commit, or push was performed.

## Concrete next change set ? awaiting approval after automatic review rejection

Automatic approval review rejected the proposed combined finance/workforce/health/migration edit before execution. It identified workflow disruption and migration failure on historical duplicates as risks. No part of that rejected command was applied.

1. FinanceGovernanceController: wrap invoice/payroll generation and audit in an execution-strategy transaction. Serialize generation per organization and operation so overlapping periods cannot race. Exclude visits already represented by a corresponding finance line. Return 409 if no eligible source visits remain. Preserve each source visit's branch; calculate invoice totals as the sum of rounded line amounts; reject negative rates.
2. New EF migration: unique partial indexes on `(organization_id, visit_id)` for non-null source visits in finance_invoice_lines and finance_payroll_lines, plus a nonnegative medication stock constraint. Existing duplicate or negative records must cause a visible migration failure, never automatic deletion or rewriting.
3. WorkforceDevelopmentController: transaction and row lock around training-booking completion, the generated training record, and audit. Concurrent completion must not create two compliance records.
4. WorkforceComplianceController: return 503 when evidence queries fail instead of returning successful legacy readiness.
5. Program.cs: log database health failures internally and return a generic public error; require Administrator access to /status/config. Update endpoint authorization tests accordingly.
6. Regression coverage: simultaneous finance generation, overlapping periods, rollback, duplicate training completion, unavailable evidence, and restricted configuration status.

Approval of this set is for local code and test changes. It does not apply migrations to any deployed database.

### Read-only preflight for the proposed migration

Run against the intended database before approving deployment:

```sql
select organization_id, visit_id, count(*)
from finance_invoice_lines
where visit_id is not null
group by organization_id, visit_id having count(*) > 1;

select organization_id, visit_id, count(*)
from finance_payroll_lines
where visit_id is not null
group by organization_id, visit_id having count(*) > 1;

select medication_id, organization_id, stock_on_hand
from medication_safety_profiles where stock_on_hand < 0;
```

If rows are returned, financial/medication owners must resolve them through an audited correction process before those constraints can be deployed.

## Remaining audit work

- eMAR: review profile upsert's absolute stock replacement, legacy medication write paths, retry/commit ambiguity, key reuse with a different request/resource, and backdated PRN history semantics. The applied row locks do not constitute completion of the whole medication audit.
- Authorization: a central IContextualAuthorization already exists, but coverage is incomplete. HttpTenantContext currently treats BackOffice as platform owner and allows branchless callers broad access. Review that policy against the intended role matrix; apply resource authorization consistently across visits, messaging, family grants and attachments. Safeguarding filtering alone does not close P0.2.
- Safeguarding: explicit governed reopen and need-to-know permissions remain.
- Finance: the P0 atomicity/idempotency fix remains unapplied pending approval; payment overages, rates, multi-week funding and approval/export lifecycle remain.
- Privacy: transactional anonymization, revision/concurrency protection and export/discovery coverage remain.
- Complaints: transition state machine and transactional state/event/audit remain.
- Messaging: participant/care-team/resource scope, per-recipient read semantics and durable notifications remain.
- Reporting: unsupported metric rejection, ownership, applied filters and evidence validation remain.
- Documents/family access: sensitive-download audit and permission validity checks remain.
- Integrations: payload limits, provider schemas/adapters and controlled encrypted configuration remain.
- Infrastructure: production startup safeguards, runtime-schema migration consolidation and complete regression coverage remain.
- Timesheets: first establish existing behavior, then implement approved/locked periods and payroll exports where absent.
- Architecture: incremental application-service extraction, smaller Program.cs and standard transactional audit/outbox patterns remain.

Pre-existing edits in PersonJourneyController.cs and scripts/seed-pilot-data.ps1, the user's UAT script, and frontend edits were preserved.
