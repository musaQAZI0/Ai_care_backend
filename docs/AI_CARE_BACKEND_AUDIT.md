# AI Care — Living Backend Audit

## Purpose

This is the living technical audit for the AI Care backend. It is updated as each backend module is inspected. It complements `AI_CARE_FULL_PRODUCT_ROADMAP.md` and `AI_CARE_EMAR_AND_INTEGRATIONS_ROADMAP.md`.

The audit does **not** treat the existence of a controller or endpoint as proof of production readiness. Findings distinguish static code review, regression-test evidence, architecture concerns, potential bugs, and production risks.

## Status legend

- 🟢 **Working** — strong implementation/foundation found in reviewed code.
- 🟡 **Needs Improvement** — functionality exists but architecture, consistency, maintainability, validation, or workflow should be improved.
- 🔴 **Potential Bug** — code evidence indicates behavior that may be incorrect and requires correction/verification.
- ⚫ **Missing** — expected first-class functionality has not been found in the audited area.
- 🟠 **Production Risk** — implementation may work normally but has a reliability, security, concurrency, safety, or governance risk that should be addressed before serious production rollout.

---

# Audit Progress

| Module | Status | Current audit conclusion |
|---|---|---|
| Authentication & Security | 🟢 🟡 | Strong security foundation; controller is oversized and mixes security persistence/business logic |
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
| Safeguarding | 🟡 | Functionality appears distributed rather than isolated; dedicated deep audit still pending |
| Medication Safety | 🟢 🟡 | Reconciliation/version/history foundation is strong |
| Native eMAR Administration | 🟢 🟠 | Strong safety gates; stock concurrency requires hardening |
| PRN | 🟢 🟡 | Limits, intervals and effect review are implemented |
| Medication Stock | 🟢 🟠 | Transaction/history/witness flows exist; lost-update concurrency risk identified |
| eMAR Corrections / Ledger | 🟢 🟡 | Append-only correction events preserve original ledger event; architecture should be consolidated |
| Workforce | ⏳ | Next audit batch |
| Timesheets | ⏳ | Pending |
| Finance | ⏳ | Pending |
| Messaging | ⏳ | Pending |
| Notifications | ⏳ | Pending |
| Family Portal | ⏳ | Pending |
| Documents | ⏳ | Pending |
| Reporting / Compliance | ⏳ | Pending |
| Complaints | ⏳ | Pending |
| Integrations | ⏳ | Pending |
| Audit / Privacy | ⏳ | Pending |
| Monitoring / Deployment | ⏳ | Pending |
| Database / API Architecture | ⏳ | Pending |
| Test / CI Coverage | ⏳ | Pending |

---

# 1. Authentication & Security

**Status: 🟢 Working foundation / 🟡 Needs Improvement**

Reviewed implementation includes JWT authentication, login lockout, MFA checks, TOTP/recovery-code verification, session creation, opaque refresh tokens, refresh-token rotation/compromise handling, password change/reset, tenant signup controls, strong-password validation, logout and audit events.

Platform configuration also includes JWT issuer/audience/lifetime validation, CORS allow-listing, rate limiting, request IDs, security headers, active-session validation, MFA-enrolment restrictions, portal-role restrictions, privileged-access middleware and sensitive-operation controls.

### Findings

- Keep the current functionality.
- `AuthController` is too large and contains persistence/security implementation details that belong in Application/Infrastructure services.
- Refactor progressively toward `AuthController -> AuthenticationService/use cases -> token/session/security repositories`.
- Some security persistence paths use silent/fail-open style exception handling and should be reviewed carefully before production.
- Verify production password-reset/invite/email delivery end-to-end rather than assuming token creation means delivery is complete.

---

# 2. Multi-Tenancy / Branch Scoping

**Status: 🟢 Foundation / 🟠 Production verification required**

Organisation and branch identifiers are used widely and `ITenantContext` is present. Many sensitive records are tenant scoped.

### Findings

- Organisation isolation is consistently considered.
- Branch filtering is not expressed consistently across every controller/query.
- Some reads are organisation-wide while writes use organisation + branch.
- This may be intentional for manager-level cross-branch workflows, so it is not automatically a bug.
- Define a formal scope matrix by role and resource: organisation-wide vs branch-only vs assigned-person/assigned-worker.
- Add cross-tenant and cross-branch regression tests for every sensitive module.
- Prefer reusable tenant-scoped repositories/query policies so developers cannot accidentally forget scope predicates.

---

# 3. Person / Service User Lifecycle

**Status: 🟢 Working / 🟡 Needs Improvement**

The backend has meaningful lifecycle behavior rather than simple person CRUD. Admission/transfer/discharge workflows include prerequisite checks, handover information, database transactions and audit/lifecycle records.

### Findings

- Admission validates important readiness conditions such as funding, initial plan and medication reconciliation confirmation.
- Transfer validates destination organisation/branch and records handover information.
- Discharge includes medication handover/property confirmation and active-admission checks.
- Continue toward a coherent Person 360 read model rather than many frontend round trips.
- Keep lifecycle history immutable/auditable.

---

# 4. Assessments & Risk Governance

**Status: 🟢 Working / 🟡 Needs Improvement**

Assessments implement governed states rather than plain CRUD. Reviewed behavior includes Draft/InReview/Current-style lifecycle, manager/admin approval, declarations/signatures, corrections, version increments, review dates and risk scoring.

Regression tests exercise invalid JSON, role restrictions, approval requirements, immutable corrections, risk-score validation and audit events.

### Findings

- Preserve existing lifecycle and tests.
- Move raw SQL/business rules out of API controllers over time.
- Build configurable/versioned assessment templates and typed answers.
- Preserve every approved historical version.
- Add stronger review dashboards and assessment-to-risk/care-plan linking.

---

# 5. Care Plans

**Status: 🟢 Strong foundation**

The lifecycle controller is one of the cleaner reviewed modules. It delegates to application services, validates lifecycle commands, uses expected revision values and separates submit/review/approve/sign/activate/revision/acknowledgement operations.

### Findings

- Use this module as a pattern for other controller refactors.
- Continue immutable published versions and explicit revision creation.
- Add richer goals/outcomes/interventions and change summaries as described in the product roadmap.

---

# 6. Care Tasks

**Status: 🟢 Working / 🟡 Needs Improvement**

Task editing is restricted to appropriate care-plan state. Plan tasks can be materialised into visit tasks. Task outcomes are validated, incomplete/refused outcomes require reasons, relevant failures can create visit exceptions, and audit events are generated.

### Findings

- Controller currently owns task materialisation, authorization, SQL, escalation and domain rules.
- Extract application use cases/services.
- Verify active-care-plan selection against the final lifecycle/version rules.
- Maintain separate reusable care task definitions and visit task instances.

---

# 7. Scheduling / Rota

**Status: 🟢 Working / 🟡 Needs Improvement / 🟠 Scope verification**

Advanced scheduling functionality includes worker absence and scheduling-policy concepts.

### Findings

- Too much scheduling persistence/business logic is directly in controllers/raw SQL.
- Standardise branch-level visibility rules for worker absences and scheduling data.
- Validate overlapping absence handling, recurring-series concurrency, DST/timezone boundaries and multi-coordinator edits.
- Move rules into dedicated Application/domain scheduling policies.

---

# 8. Visit Operations

**Status: 🟢 Working / 🟡 Needs Improvement / 🟠 Scope verification**

Visit operations include Late/Missed/Shortened/Cancelled/NoAccess/RefusedCare exception handling, escalation deadlines, manager workflows, handovers and acknowledgements.

### Findings

- Some manager-level queries appear organisation scoped rather than explicitly branch scoped.
- Verify this against the intended role/scope matrix before changing behavior.
- Add systematic authorization tests for worker assignment, branch managers, cross-branch managers and administrators.
- Keep exception and handover history auditable.

---

# 9. Care Documentation / Daily Notes

**Status: 🟢 Working / 🟡 Needs Improvement**

Notes support amendments with reason and retain old/new values. Manager review is separate. Observation thresholds can create deterioration workflows, alerts can be acknowledged/resolved and a service-user timeline exists. PostgreSQL regression tests exercise key flows.

### Findings

- Current note record is updated while amendment history stores previous/current values.
- This preserves history, but a stronger long-term design is immutable note versions/addenda.
- Keep late-entry, amendment reason, author and review provenance explicit.

---

# 10. Incidents

**Status: 🟢 Working / 🟡 Needs Improvement**

Incident governance includes investigation chronology, evidence reviewed, findings, root cause, lessons learned, corrective/preventive actions, completion evidence, event history and controlled closure.

### Findings

- Incident closure requires a completed investigation and completed CAPA actions.
- Completing an investigation/final closure is manager/admin restricted.
- Investigation uses a mutable current record/upsert plus event history; consider versioned investigation revisions for stronger provenance.
- Extract investigation/CAPA rules and SQL from the controller.

---

# 11. Capacity / Authority / Best Interest

**Status: 🟢 Working / 🟡 Needs Improvement**

Capacity is decision-specific and records understand/retain/use-or-weigh/communicate factors, support provided and assessor information. New decisions version/supersede prior current decisions. Best-interest decisions require a current lacks-capacity decision and supporting evidence. Authority records require verification evidence, support versioning and explicit revocation reasons.

### Findings

- Strong governance foundation.
- Standardise organisation/branch read/write scope.
- Move SQL and lifecycle rules into application/domain services.
- Preserve superseded/revoked history permanently.

---

# 12. Safeguarding

**Status: 🟡 Deep audit pending**

Safeguarding-related functionality appears distributed through the broader API/governance implementation rather than exposed as one obvious isolated controller in the reviewed API tree.

### Findings

- Do not treat the absence of a controller named `SafeguardingController` as proof that safeguarding is missing.
- Deep-audit exact safeguarding routes, persistence, permissions, restricted visibility, chronology and family/reporting exclusion before final classification.
- Safeguarding must have stricter need-to-know authorization than ordinary incidents.

---

# 13. Medication Safety & Native eMAR

**Overall status: 🟢 Advanced foundation / 🟡 Architecture improvement / 🟠 Production concurrency risk**

The reviewed medication/eMAR implementation is substantially more than CRUD. It contains safety gates around medication verification, staff authorization/competency, MAR outcomes, PRN, witnesses, stock, corrections, escalations and audit history.

## Medication safety profile / reconciliation — 🟢 🟡

Medication safety profiles support reconciliation statuses including Draft, NeedsReview, Verified, Superseded and Discontinued. Verified reconciliation requires source information, reviewer and reviewed time. Profile changes increment a version and reconciliation history is recorded.

### Improve

- Move reconciliation rules from controller/raw SQL into the Medication Application/domain module.
- Continue richer `MedicationOrder` design and Pharmacy entity from the eMAR roadmap.
- Treat verified reconciliation as a governed workflow, not simply editable profile metadata.

## Production administration — 🟢 🟠

Before recording a governed administration, the implementation checks important conditions including:

- production eMAR safety flag;
- required `Idempotency-Key`;
- role/assigned-worker access;
- medication authorization;
- active medication restriction;
- route-specific competency;
- existing terminal MAR outcome;
- authoritative server time constraints;
- complete verified medication reconciliation;
- medication active dates;
- allergy conflict;
- duplicate medication conflict;
- dose administration window and override;
- omission/refusal reason codes;
- administered dose quantity;
- PRN limits/protocol;
- witness rules;
- reconciled stock availability.

This is a strong safety foundation and should be **preserved during refactoring**.

## Idempotency — 🟢

Governed administration requires an idempotency key and supports replay of a previously recorded operation. This should remain mandatory for medication administration writes.

## PRN — 🟢 🟡

The implementation checks PRN maximum doses in a 24-hour window and minimum dose interval. Governed PRN administration creates an effect-review escalation. A PRN effect requires an existing governed Administration ledger event and duplicate effect recording is prevented.

### Improve

- Make `PrnProtocol` a first-class domain model.
- Validate observation/effect timing against the configured review protocol.
- Keep manager exception queues for overdue PRN effect reviews.

## Witnessing — 🟢

The reviewed flow prevents self-witnessing and requires an active same-organisation user where a witness is required.

### Improve

- Standardise which roles are eligible witnesses by tenant policy.
- Consider branch/assignment/competency requirements for specialist medication workflows.

## Corrections / immutable ledger — 🟢 🟡

Corrections append a new `Correction` ledger event referencing `corrects_ledger_id`; the original ledger event is not deleted. This is the correct direction for eMAR provenance.

### Improve

- Validate allowed corrected outcomes centrally.
- Define whether a correction changes the effective/current MAR read model while retaining all original events.
- Add correction reason categories and manager review where required.

## Escalations — 🟢

The backend supports escalation progress actions and creates escalation records for cases including missed/late doses, PRN effect review and low stock.

### Improve

- Add a first-class medication exception dashboard/read model.
- Add due/overdue escalation worker processing and notification routing.

## Medication stock — 🟢 functionality / 🟠 Production Risk

Stock transactions support receipt/return/waste/disposal/correction/adjustment, negative-balance prevention, witnesses, transaction history and low-stock escalation. Governed administration also reduces stock and writes a stock transaction.

### Important production risk: lost-update concurrency

Current flows read the stock balance, calculate the new balance in application code, and later update the stored balance inside a transaction. Two concurrent operations can potentially read the same starting balance and both calculate from it. A transaction alone does not necessarily prevent this lost-update pattern.

**Required hardening before serious production rollout:** make the stock mutation atomic at PostgreSQL level, or use row locking/optimistic concurrency. Example design:

```sql
UPDATE medication_safety_profiles
SET stock_on_hand = stock_on_hand - @dose
WHERE medication_id = @medication
  AND organization_id = @organization
  AND branch_id = @branch
  AND stock_on_hand >= @dose
RETURNING stock_on_hand;
```

The returned database balance should be used as the authoritative `stock_after`. Stock ledger insertion and MAR administration should remain in the same transaction.

Also add concurrency regression tests with two simultaneous stock/administration operations.

## eMAR architecture — 🟡

Medication functionality is currently spread across `MedicationSafetyController`, `ProductionEmarController`, `EmarOperationsController` and older MAR/API paths. The behavior is valuable, but the domain boundary is fragmented.

Target incremental refactor:

```text
API Controllers
      ↓
Medication Application Module
├── Orders
├── Reconciliation
├── Scheduling
├── Administration
├── PRN
├── Stock
├── Corrections
└── Escalations
      ↓
Medication Domain
      ↓
Infrastructure / PostgreSQL
```

**Do not rebuild the eMAR.** Preserve its current safety checks and move them behind cleaner application/domain boundaries with regression tests.

---

# Cross-Cutting Findings So Far

## A. Controller-heavy architecture — 🟡

The most repeated technical issue is large API controllers containing business rules, raw SQL, persistence helpers, authorization decisions and workflow transitions together.

Recommended migration pattern:

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

Refactor one module at a time behind regression tests. Do not perform a large rewrite.

## B. Branch authorization semantics — 🟠 Verify and standardise

Several modules clearly enforce organisation scope, while branch scope varies between operations. This needs a formal access-scope design before modifying individual queries.

Create tests for:

- worker assigned to record;
- worker not assigned;
- coordinator same branch;
- coordinator different branch;
- manager same branch;
- manager cross-branch if permitted;
- administrator;
- different organisation.

## C. Historical record integrity — 🟡

The codebase already has good examples of superseding/versioning and append-only ledger events. Extend the same pattern consistently to completed notes, investigations, care records and other governed data rather than destructive editing.

## D. Runtime correctness is not yet proven by static audit

A static review can establish that controls and workflows exist in code. Production readiness also requires passing regression/integration tests, migrations against PostgreSQL, concurrency testing, configuration verification, backup/restore checks, deployment health and real workflow validation.

---

# Current Priority Fix Queue

1. **P0 / 🟠 — eMAR stock concurrency:** atomic database stock mutation + concurrency tests.
2. **P0 / 🟠 — tenant/branch authorization matrix:** define intended scope and regression-test sensitive modules.
3. **P0 / 🟡 — preserve immutable governed history:** standardise corrections/versioning/addenda.
4. **P1 / 🟡 — controller refactor:** progressively extract application/domain use cases; no rewrite.
5. **P1 — continue deep audit:** Workforce → Timesheets → Finance → Messaging → Notifications → Family Portal → Documents → Reporting → Complaints → Integrations → Audit/Privacy → Monitoring/Deployment → DB/API → Tests/CI.

---

# Update Policy

This file is intentionally a **living audit**. As additional modules are reviewed:

1. update the Audit Progress table;
2. add the detailed module findings;
3. add confirmed risks to the Priority Fix Queue;
4. downgrade/remove a risk only when code/tests demonstrate it has been resolved;
5. keep roadmap ideas separate from confirmed implementation findings;
6. do not mark runtime behavior as verified unless it has actually been executed/tested.
