# AI Care — Living Backend Audit

## Purpose

This is the living technical audit for the AI Care backend. It is updated as each backend module is inspected. It complements `AI_CARE_FULL_PRODUCT_ROADMAP.md` and `AI_CARE_EMAR_AND_INTEGRATIONS_ROADMAP.md`.

The audit does **not** treat the existence of a controller or endpoint as proof of production readiness. Findings distinguish static code review, regression-test evidence, architecture concerns, potential bugs, and production risks.

## Status legend

- 🟢 **Working** — strong implementation/foundation found in reviewed code.
- 🟡 **Needs Improvement** — functionality exists but architecture, consistency, maintainability, validation, or workflow should be improved.
- 🔴 **Potential Bug** — code evidence indicates behavior that may be incorrect and requires correction/verification.
- ⚫ **Missing** — expected first-class functionality has not been found in the audited area.
- 🟠 **Production Risk** — reliability, security, concurrency, safety, data-integrity, or governance risk to address before serious production rollout.
- ⏳ **Pending** — deep audit not yet completed.

---

# Audit Progress

| Module | Status | Current audit conclusion |
|---|---|---|
| Authentication & Security | 🟢 🟡 | Strong security foundation; controller is oversized and mixes persistence/security/business logic |
| Platform Middleware | 🟢 | JWT, rate limiting, session validation, MFA restrictions, security headers and privileged controls present |
| Multi-Tenancy | 🟢 🟠 | Organisation/branch scoping is widespread; branch semantics need systematic verification |
| Person / Service User Lifecycle | 🟢 🟡 | Substantial admission/transfer/discharge and governance workflows |
| Assessments | 🟢 🟡 | Governed lifecycle, approval, correction/versioning and regression testing present |
| Risk Management | 🟢 🟡 | Scoring, approval, corrections, alerts and audit foundation present |
| Care Plans | 🟢 | Clean lifecycle foundation with expected revision/concurrency concepts |
| Care Tasks | 🟢 🟡 | Plan-task materialisation, outcomes, exceptions and audit present; controller-heavy architecture |
| Scheduling / Rota | 🟢 🟡 🟠 | Advanced rules exist; raw SQL/controller logic and branch-scope semantics need review |
| Visit Operations | 🟢 🟡 🟠 | Exception/handover functionality exists; access/branch scope requires deeper verification |
| Care Documentation | 🟢 🟡 | Amendments, review, alerts and timeline present; stronger immutable version model preferred |
| Incidents | 🟢 🟡 | Investigation, CAPA and controlled closure are implemented |
| Capacity / Authority / Best Interest | 🟢 🟡 | Decision-specific capacity, best-interest evidence and authority versioning/revocation present |
| Safeguarding | 🟡 | Dedicated controller is present; deep permission/workflow audit still pending |
| Medication Safety | 🟢 🟡 | Reconciliation/version/history foundation is strong |
| Native eMAR Administration | 🟢 🟠 | Strong safety gates; stock concurrency requires hardening |
| PRN | 🟢 🟡 | Limits, intervals and effect review are implemented |
| Medication Stock | 🟢 🟠 | Transaction/history/witness flows exist; lost-update concurrency risk identified |
| eMAR Corrections / Ledger | 🟢 🟡 | Append-only correction events preserve original ledger event; architecture should be consolidated |
| Workforce Lifecycle | 🟢 🟡 | Employment, supervision/appraisal, absence cover, return-to-work and lifecycle events exist |
| Workforce Compliance | 🟢 🟡 🟠 | Structured DBS/RTW/training/competency/availability exists; fallback behavior and scope consistency need hardening |
| Workforce Development | 🟢 🟡 🟠 | Recruitment, references, training catalogue/bookings and probation exist; completion atomicity needs hardening |
| Timesheets | ⚫ 🟡 | No clear first-class approved/locked timesheet workflow found in this batch; finance currently derives directly from completed visits |
| Finance / Invoicing / Payroll | 🟢 🟡 🟠 | Useful batch/payment/reconciliation foundation; duplicate batch, atomicity, branch scope and calculation rules need hardening |
| Messaging | ⏳ | Pending deep audit |
| Notifications | ⏳ | Pending deep audit |
| Family Portal | ⏳ | Pending deep audit |
| Documents | ⏳ | Pending deep audit |
| Reporting / Compliance | ⏳ | Pending deep audit |
| Complaints | ⏳ | Pending deep audit |
| Integrations | ⏳ | Pending deep audit |
| Audit / Privacy | ⏳ | Pending deep audit |
| Monitoring / Deployment | ⏳ | Pending deep audit |
| Database / API Architecture | ⏳ | Pending deep audit |
| Test / CI Coverage | ⏳ | Pending deep audit |

---

# 1. Authentication & Security

**Status: 🟢 Working foundation / 🟡 Needs Improvement**

JWT authentication, login lockout, MFA/TOTP/recovery codes, sessions, opaque refresh tokens, refresh rotation/compromise handling, password change/reset, strong-password validation, logout and audit events are implemented. Platform configuration also contains CORS allow-listing, rate limiting, request IDs, security headers, active-session validation, MFA restrictions, privileged access and sensitive-operation controls.

### Notes

- Keep the existing security functionality.
- Refactor the very large `AuthController` toward `Controller -> Authentication Application Service -> token/session/security repositories`.
- Review silent/fail-open persistence exception paths before production.
- Verify production reset/invite/email delivery end-to-end.

---

# 2. Multi-Tenancy / Branch Scoping

**Status: 🟢 Foundation / 🟠 Production verification required**

Organisation/branch identifiers and `ITenantContext` are widely used, but branch scope is not expressed consistently in every raw query.

### Notes

- Define a formal role/resource scope matrix: organisation-wide, branch-only, assigned-person, assigned-worker.
- Add cross-tenant and cross-branch regression tests for every sensitive module.
- Prefer reusable tenant-scoped repositories/query policies over manually repeated SQL predicates.

---

# 3. Person / Service User Lifecycle

**Status: 🟢 Working / 🟡 Needs Improvement**

Admission/transfer/discharge have prerequisite checks, handover information, transactions and lifecycle/audit records. Continue toward a coherent Person 360 read model and preserve lifecycle history.

---

# 4. Assessments & Risk Governance

**Status: 🟢 Working / 🟡 Needs Improvement**

Governed states, manager/admin approval, declarations/signatures, corrections, version increments, review dates and risk scoring exist. Regression tests cover invalid JSON, roles, approval requirements, corrections, score validation and audit.

### Notes

Move SQL/business rules out of controllers gradually; add configurable/versioned templates and typed answers while preserving approved history.

---

# 5. Care Plans

**Status: 🟢 Strong foundation**

Lifecycle operations delegate to application services, validate commands, use expected revision values and separate submit/review/approve/sign/activate/revision/acknowledgement. Use this as a pattern for other modules.

---

# 6. Care Tasks

**Status: 🟢 Working / 🟡 Needs Improvement**

Plan task editing/materialisation, visit outcomes, required failure reasons, exception generation and audit exist. Extract materialisation/authorization/SQL/escalation rules from the controller and verify active-plan selection against lifecycle/version rules.

---

# 7. Scheduling / Rota

**Status: 🟢 Working / 🟡 Needs Improvement / 🟠 Scope verification**

Advanced scheduling and absence/policy functionality exists. Standardise branch visibility, validate overlapping absence handling, recurring-series concurrency, DST/timezone boundaries and multi-coordinator edits, and move rules into application/domain scheduling policies.

---

# 8. Visit Operations

**Status: 🟢 Working / 🟡 Needs Improvement / 🟠 Scope verification**

Late/Missed/Shortened/Cancelled/NoAccess/RefusedCare exceptions, escalation deadlines, manager workflows, handovers and acknowledgements exist. Verify organisation-vs-branch manager semantics with authorization tests.

---

# 9. Care Documentation / Daily Notes

**Status: 🟢 Working / 🟡 Needs Improvement**

Amendments preserve old/new values, manager review is separate, observations can create deterioration workflows, alerts can be acknowledged/resolved and a person timeline exists. Prefer immutable note versions/addenda long term rather than mutable current note + amendment history alone.

---

# 10. Incidents

**Status: 🟢 Working / 🟡 Needs Improvement**

Investigation chronology, evidence/findings/root cause, lessons, CAPA, completion evidence and controlled closure exist. Closure requires completed investigation/CAPA. Consider versioned investigation revisions and extract controller SQL/rules.

---

# 11. Capacity / Authority / Best Interest

**Status: 🟢 Working / 🟡 Needs Improvement**

Decision-specific capacity, support/assessor evidence, superseding versions, best-interest evidence requirements and authority verification/versioning/revocation are implemented. Standardise branch scope and preserve superseded/revoked history.

---

# 12. Safeguarding

**Status: 🟡 Deep audit pending**

A dedicated `SafeguardingController.cs` is confirmed in the API project. The earlier audit note that safeguarding only appeared distributed was incomplete and is corrected here.

### Deep-audit next

- need-to-know authorization;
- branch/person scope;
- chronology;
- referral/action/outcome lifecycle;
- restricted documents/notes;
- reopening/history;
- family portal/report exclusion;
- access auditing.

---

# 13. Medication Safety & Native eMAR

**Overall: 🟢 Advanced foundation / 🟡 Architecture improvement / 🟠 Production concurrency risk**

Safety profiles/reconciliation, governed administration, idempotency, worker authorization/competency, medication restrictions, route competency, server-time checks, active dates, allergy/duplicate conflicts, dose windows, omission/refusal reasons, PRN limits, witnesses, stock, corrections, escalations and ledger history are present.

### Strong areas

- Governed administration requires an `Idempotency-Key`.
- PRN minimum interval/24-hour maximum controls and effect-review escalation exist.
- Self-witnessing is prevented and witnesses are validated.
- Corrections append a new ledger event referencing the corrected event rather than deleting the original.
- Reconciliation has statuses/version/history.

### 🟠 Stock concurrency risk

Stock is read, a new balance is calculated in application code, and the balance is then updated. Concurrent requests can potentially calculate from the same starting balance. Harden with atomic PostgreSQL mutation, row locking, or optimistic concurrency, and keep stock ledger + MAR administration in one transaction.

Preferred pattern:

```sql
UPDATE medication_safety_profiles
SET stock_on_hand = stock_on_hand - @dose
WHERE medication_id = @medication
  AND organization_id = @organization
  AND branch_id = @branch
  AND stock_on_hand >= @dose
RETURNING stock_on_hand;
```

Add concurrent administration/stock regression tests.

### Architecture

Medication behavior is spread across `MedicationSafetyController`, `ProductionEmarController`, `EmarOperationsController` and older MAR paths. Preserve all safety checks but refactor incrementally into a coherent Medication Application/domain module. Do not rebuild the eMAR.

---

# 14. Workforce Lifecycle

**Status: 🟢 Working / 🟡 Needs Improvement**

The reviewed lifecycle controller has a meaningful workforce workspace and supports employment lifecycle, supervision/appraisal scheduling and completion, absence cover, sickness return-to-work review and append-style lifecycle events/audit.

### Good controls found

- Employment status is constrained to known states.
- Employed workers require start date/job title/contract type.
- Suspended/leaver states require a reason; leavers require an end date.
- Supervision/appraisal completion requires outcome and evidence.
- An absent worker cannot cover their own absence.
- Covering worker must be accessible.
- Return-to-work review only applies to ended sickness absence.
- Not-fit-for-unrestricted-return requires restrictions.
- Duplicate return-to-work review is rejected.
- Worker self-read is supported while management roles have wider access.
- Worker existence uses organisation and branch/organisation-wide tenant context.

### 🟡 Improvements

- Raw SQL, lifecycle rules and persistence are controller-owned; extract use cases/services.
- `on conflict(care_worker_id)` employment profile behavior assumes one profile per worker; keep history in lifecycle events but consider explicit employment-profile version/history for important contract changes.
- Define how suspension/leaver status blocks scheduling and medication administration across modules; storing the status alone is not enough.
- Add optimistic concurrency/versioning for employment changes.

---

# 15. Workforce Compliance

**Status: 🟢 Working / 🟡 Needs Improvement / 🟠 Production hardening**

Structured compliance records, training, competencies and availability rules exist. A readiness summary checks DBS, right-to-work, mandatory training and availability and reports records expiring within 30 days.

### Good controls found

- Compliance/training/competency/availability writes are management/coordinator restricted.
- Worker access is checked through tenant scope.
- Structured expiry dates and verification metadata exist.
- Availability validates day/time ranges.
- Readiness gives a useful assignment-oriented summary.

### 🟠 Important finding: broad database-exception fallback

The compliance summary catches a generic `DbException` and returns a legacy readiness result. This is useful during migration, but in production it can hide real database/query failures and make the system appear healthy using legacy fields.

**Recommendation:** only use legacy fallback when an explicitly detected migration/schema condition requires it. Unexpected database errors should be logged/observed and fail safely rather than silently downgrade readiness evaluation.

### 🟠 Scope consistency

Structured compliance queries filter `organization_id` but do not consistently include `branch_id`, even though the worker itself is tenant-access checked. This may be acceptable if worker compliance is intentionally organisation-wide, but it must be explicit. Define whether a worker can move branches while retaining compliance records and encode that policy consistently.

### Additional improvements

- Validate status/category values centrally instead of accepting arbitrary strings for several compliance records.
- Validate expiry >= issue/completion/assessment date.
- Define competency requirements per visit/medication/task and enforce them at assignment time.

---

# 16. Workforce Development / Recruitment / Training

**Status: 🟢 Working / 🟡 Needs Improvement / 🟠 Transaction hardening**

Recruitment cases, references, training catalogue, training bookings/completion and probation reviews are implemented.

### Good controls found

- Recruitment uses controlled lifecycle states.
- `Cleared` requires identity, right-to-work, DBS, health declaration and onboarding checks.
- Rejected/withdrawn cases require a reason.
- Received references require evidence.
- Training catalogue supports mandatory courses and organisation-wide/branch courses.
- Booking validates future schedule and course accessibility.
- Completion requires evidence and calculates expiry from course validity.
- Completion creates a structured worker training record.
- Probation supports Passed/Extended/Failed and requires evidence; extension requires an extension date.

### 🟠 Important finding: training completion atomicity

Completing a training booking performs at least two important writes: update the booking to `Completed`, then insert the resulting `worker_training_records` row. These writes are not visibly wrapped in one explicit transaction in the reviewed controller.

If the second write fails after the booking update succeeds, the booking may say Completed without the corresponding compliance/training record.

**Recommendation:** wrap booking completion + training-record creation + audit in one database transaction and add a regression test proving rollback on failure.

### 🟡 Other improvements

- Recruitment case is updated in place; important lifecycle history should remain independently queryable/auditable.
- Some recruitment-reference lookups are organisation scoped rather than explicitly branch scoped; align with the formal scope model.
- Add unique/duplicate booking rules where needed.
- Prevent expired/inactive/superseded training from satisfying assignment requirements.

---

# 17. Timesheets

**Status: ⚫ First-class workflow not confirmed / 🟡 Product gap**

The finance implementation currently derives invoice/payroll data directly from completed visits. In the reviewed workforce/finance batch, a clear first-class workflow such as the following was not found:

```text
Completed visit
→ Timesheet entry
→ adjustment/review
→ manager approval
→ locked pay period
→ payroll export
```

This is not proof that no timesheet-related code exists elsewhere; it means a governed first-class timesheet workflow has not yet been confirmed in the audited code.

### Add/verify

- planned vs actual time;
- travel/mileage;
- manual adjustment with reason;
- worker review if required;
- manager approval;
- locked periods;
- audit history;
- payroll export boundary.

---

# 18. Finance / Invoicing / Payroll

**Status: 🟢 Useful foundation / 🟡 Needs Improvement / 🟠 Production hardening**

Finance has a dashboard, invoice batch generation from completed visits, payroll batch generation, invoice/payroll lines, payment recording and funding reconciliation.

### Good foundation

- Finance endpoints are restricted to Administrator/BackOffice.
- Invoice lines preserve visit/person/rate/quantity/funding source.
- Payroll lines preserve visit/worker/hours/rate/mileage/gross pay.
- Payments require positive amount/reference and update invoice status to Part paid/Paid.
- Funding reconciliation compares delivered and authorised hours and records exceptions.
- Finance audit events exist.

### 🟠 Duplicate batch risk

Invoice/payroll generation selects completed visits for a date range but the reviewed controller does not visibly prevent the same visit from being included in multiple generated invoice/payroll batches. Re-running a period can therefore create duplicate financial lines unless database constraints elsewhere prevent it.

**Required:** add idempotent batch identity and/or uniqueness rules around billable/payable visit inclusion, plus explicit adjustment/credit workflows instead of regenerating history.

### 🟠 Transaction atomicity

Invoice generation saves an invoice and then inserts its visit lines iteratively. Payroll generation similarly creates a run and then lines. The reviewed methods are not visibly wrapped in a single explicit transaction. Partial failure could leave a header without all expected lines or an incomplete batch.

**Required:** batch header + lines + audit should commit atomically, with rollback tests.

### 🟠 Branch scope semantics

Finance dashboard/batch visit selection is largely organisation scoped. That may be intentional for BackOffice/Administrator, but generated records use the current branch value. An organisation-wide user generating an organisation-wide batch while records receive a single default/current branch can create misleading branch attribution.

**Required:** decide whether finance batches are organisation-level or branch-level. If organisation-level, model them that way. If branch-level, filter source visits by branch.

### 🟡 Calculation/model improvements

- Invoice generation currently uses completed visit duration and an active funding hourly rate/default rate; formalise rate cards/contracts/effective dates.
- Payroll currently applies `MileageRate` as a flat amount per visit in the reviewed method, rather than multiplying by a recorded mileage quantity. Model mileage quantity and rate separately.
- Add invoice approval/void/credit/adjustment lifecycle.
- Add payroll approval/lock/export lifecycle.
- Prevent payments from exceeding invoice amount unless overpayment is explicitly supported.
- Funding reconciliation compares delivered hours in the requested period with `AuthorizedHoursPerWeek`; this needs a clearly defined period-normalisation rule before relying on the variance for production decisions.
- Separate care-delivery facts from finance calculations so historical financial results remain reproducible when rates later change.

---

# Cross-Cutting Findings

## A. Controller-heavy architecture — 🟡

Repeated pattern: API controllers contain business rules, raw SQL, persistence helpers, authorization decisions and workflow transitions. Refactor module by module behind regression tests:

```text
Controller
   ↓
Application Use Case / Service
   ↓
Domain Rules
   ↓
Repository / Unit of Work
   ↓
PostgreSQL
```

No large rewrite.

## B. Branch authorization semantics — 🟠

Create a formal scope matrix and tests for worker assigned/not assigned, coordinator same/different branch, manager same/cross branch, administrator and different organisation.

## C. Historical record integrity — 🟡

Extend existing good superseding/versioning/ledger patterns consistently to completed notes, investigations, workforce lifecycle and finance adjustments.

## D. Multi-write operations need explicit atomicity — 🟠

Several important workflows perform multiple dependent writes. Medication stock, training completion and finance batch generation should have clear transaction boundaries and rollback tests.

## E. Runtime correctness is not proven by static audit

Production readiness still requires regression/integration tests, PostgreSQL migrations, concurrency tests, configuration verification, backup/restore, deployment health and real workflow validation.

---

# Current Priority Fix Queue

1. **P0 / 🟠 eMAR stock concurrency** — atomic database stock mutation + concurrency tests.
2. **P0 / 🟠 Tenant/branch authorization matrix** — define intended scope and regression-test sensitive modules.
3. **P0 / 🟠 Finance duplicate/partial batch protection** — idempotency/unique inclusion + explicit transaction.
4. **P0 / 🟠 Workforce training completion atomicity** — booking + training record + audit in one transaction.
5. **P0/P1 / 🟠 Workforce compliance fallback** — do not hide unexpected DB failures behind legacy readiness.
6. **P1 / 🟡 First-class timesheets** — approved/locked source for payroll rather than direct raw completed-visit generation.
7. **P1 / 🟡 Preserve immutable governed history** — standardise corrections/versioning/addenda.
8. **P1 / 🟡 Controller refactor** — progressively extract application/domain use cases; no rewrite.
9. **P1 Continue deep audit** — Safeguarding → Messaging → Notifications → Family Portal → Documents → Reporting → Complaints → Integrations → Audit/Privacy → Monitoring/Deployment → DB/API → Tests/CI.

---

# Update Policy

This file is intentionally a **living audit**. For each audit batch:

1. update the progress table;
2. add detailed findings;
3. add confirmed risks to the priority queue;
4. downgrade/remove risks only when code/tests demonstrate resolution;
5. keep roadmap ideas separate from confirmed implementation findings;
6. never mark runtime behavior verified unless it was actually executed/tested.
