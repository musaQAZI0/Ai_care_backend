# AI Care — Full Product Audit & Development Roadmap

## Purpose

This document expands the roadmap beyond eMAR. It records what is already present in the AI Care backend, what should be improved, what remains to be added, and the recommended order for evolving the platform into a production-grade UK home-care/social-care SaaS.

This is an engineering roadmap, not a claim that every existing controller is already production complete. Existing functionality should be preserved, regression-tested, refactored where necessary, and strengthened rather than rewritten without reason.

---

# 1. Current Architecture

Current solution structure:

```text
AiCare.Api
AiCare.Application
AiCare.Domain
AiCare.Infrastructure
AiCare.Tests
```

Keep this four-layer structure. Do not create separate .NET projects for every feature. Instead, organise each project by domain/module.

Target internal structure:

```text
Domain
├── People
├── CarePlanning
├── Assessments
├── Risks
├── Visits
├── Scheduling
├── Workforce
├── Medication
├── Incidents
├── Safeguarding
├── Messaging
├── Finance
├── Reporting
└── Integrations

Application
├── People
├── CarePlanning
├── Assessments
├── Visits
├── Workforce
├── Medication
├── Finance
└── ...
```

A major technical improvement is to progressively move business rules out of very large controllers/`Program.cs` and into Application/domain services.

---

# 2. Current Feature Inventory

The repository currently contains implementations/foundations for the following areas.

## Identity, authentication and platform security — PRESENT

Current code includes:

- JWT authentication.
- Role-based authorization.
- Current-user context.
- Tenant context.
- MFA lifecycle functionality.
- Session security.
- Sensitive-operation step-up controls.
- Privileged/emergency/delegated access concepts.
- Access-review governance.
- API security hardening.
- Production configuration validation.
- Document-upload security.
- Idempotency support.

### Improve next

- Move from broad role checks toward granular permission policies.
- Maintain a central permission catalogue.
- Define permissions by module/action, e.g. `CarePlan.View`, `CarePlan.Publish`, `Medication.Administer`, `Medication.Correct`, `Safeguarding.ViewSensitive`, `Finance.Export`.
- Enforce branch/service-user scope in addition to role.
- Add/review account lockout, password reset, email verification and invite lifecycle.
- Add admin-visible active-session/device management where required.
- Add explicit security-event reporting and suspicious-login alerting.
- Review token rotation/revocation strategy.
- Ensure secrets and sensitive configuration never appear in logs/API output.
- Add periodic access-review workflows for production tenants.

---

# 3. Multi-Tenancy — PRESENT, MUST BE HARDENED

The current system has Organisation/Branch concepts and tenant fields across core records.

### Improve

- Make tenant isolation impossible to accidentally bypass.
- Prefer reusable tenant-scoped repositories/query filters rather than manually remembering organisation filters in every query.
- Add cross-tenant regression tests for every sensitive module.
- Define branch-level versus organisation-level resources explicitly.
- Add tenant lifecycle: trial, active, suspended, offboarding, retention/deletion.
- Add tenant feature flags/subscription entitlements.
- Add organisation settings: timezone, locale, care type, medication configuration, notification settings.
- Add tenant-level branding only where useful.
- Add safe tenant data export/offboarding process.

---

# 4. Service User / Person Record — STRONG FOUNDATION PRESENT

Current implementation includes service users, richer person records, onboarding, person journey/lifecycle, governance, admission/transfer/discharge concepts, consent/capacity/authority-related functionality and family relationships.

### Improve

Build a single coherent `Person 360` experience and backend read model instead of forcing clients to call many unrelated endpoints.

Target person record:

```text
Person
├── Identity & demographics
├── Contacts
├── NHS/health identifiers
├── GP
├── Pharmacy
├── Emergency contacts
├── Representatives
├── Consent
├── Capacity / authority
├── Communication needs
├── Personal history
├── What matters to me
├── Preferences
├── Allergies
├── Medical conditions
├── Assessments
├── Risks
├── Care plans
├── Tasks
├── Visits
├── Medication
├── Notes
├── Observations
├── Incidents
├── Documents
└── Timeline
```

### Add

- Structured contacts instead of large free-text fields.
- Structured GP/practice entity or contact model.
- Preferred/nominated Pharmacy entity.
- Identifier type/source/verification metadata.
- Duplicate-person detection/merge workflow with strict audit controls.
- Record alerts/banner warnings with expiry/review.
- Person-level timeline aggregating important events.
- Configurable person summary for carers.
- Photo/media lifecycle and access rules.
- Better discharge/archive/reactivation workflow.

---

# 5. Assessments — PRESENT

The repository contains care assessment and governed assessment/risk functionality.

### Improve

- Version assessment templates.
- Separate template definition from completed assessment instance.
- Structured questions and typed answers instead of relying mainly on JSON/free text.
- Conditional questions/branching.
- Draft → submitted → reviewed → approved lifecycle.
- Review due dates and reminders.
- Assessment comparison/history.
- Generate risks/actions/care-plan suggestions for human review.
- Prevent AI-generated assessment conclusions from becoming official without human confirmation.

### Add common assessment framework

Support configurable templates for areas such as:

- mobility;
- falls;
- nutrition/hydration;
- skin integrity;
- medication support;
- cognition;
- communication;
- personal care;
- continence;
- environment/home safety;
- mental capacity/consent where appropriate;
- other provider-specific assessments.

Templates should be configurable rather than hard-coded into controllers.

---

# 6. Risk Management — PRESENT

Risk assessment/governance foundations exist.

### Improve

- Structured likelihood/impact/severity model where appropriate.
- Risk owner.
- Controls/mitigations.
- Review schedule.
- Residual risk.
- Linked assessment/care plan/incident.
- Risk version history.
- Escalation when overdue/high-risk.
- Manager risk dashboard.
- Do not allow historical risk versions to disappear after edits.

Target flow:

```text
Assessment
   ↓
Risk identified
   ↓
Mitigation/control
   ↓
Care plan/task
   ↓
Review
   ↓
Update/version
```

---

# 7. Care Plans — GOOD FOUNDATION PRESENT

Care-plan lifecycle and superseding/version concepts already exist.

### Improve

- Break large free-text care-plan sections into configurable care-plan domains.
- Add explicit goals/outcomes/interventions.
- Assign responsible staff/roles.
- Link risks and assessments.
- Add review dates/reminders.
- Add draft/review/approve/publish/supersede lifecycle consistently.
- Add read/acknowledgement tracking for staff after material care-plan changes.
- Provide change summary between versions.
- Preserve immutable published versions.

### Add

- Care-plan templates by service type.
- Outcome progress tracking.
- Family/service-user involvement/approval where configured.
- Care-plan print/export.
- Care-plan completeness checks.
- Care-plan review dashboard.

---

# 8. Care Tasks — PRESENT

Care/visit task functionality exists.

### Improve

Separate reusable care requirements from individual visit task instances.

```text
CareTaskDefinition
      ↓
VisitTaskInstance
      ↓
Complete / Unable / Declined / Not Required
      ↓
Evidence / Note / Exception
```

### Add

- recurring tasks;
- time windows;
- mandatory/optional tasks;
- reason codes for incomplete tasks;
- task dependencies;
- escalation for missed critical tasks;
- evidence requirements where appropriate;
- task completion audit;
- care-plan-linked task generation.

---

# 9. Scheduling / Rota — ADVANCED FOUNDATION PRESENT

The repository contains advanced scheduling, availability, conflict and break-rule functionality.

### Improve

- Move scheduling rules into dedicated Application services/domain policies.
- Ensure timezone/DST correctness.
- Add optimistic concurrency when multiple coordinators edit rota.
- Improve recurring visit series editing: this occurrence / future occurrences / whole series.
- Strong cancellation/reschedule reason tracking.
- Travel-time validation.
- Staff availability and leave integration.
- Skill/competency matching.
- Continuity-of-care scoring.
- Double-booking prevention.
- Visit capacity/workload controls.

### Add

- unallocated visit board;
- drag/drop-friendly scheduling API/read model;
- bulk scheduling operations;
- shift/rota publishing lifecycle;
- worker acknowledgement of rota changes;
- configurable scheduling rules;
- route/travel provider adapter later;
- scheduling analytics: late starts, unfilled visits, travel, continuity.

---

# 10. Visit Delivery — PRESENT

Visit operations/delivery and location-event functionality exists.

### Improve

Target visit lifecycle:

```text
Scheduled
→ Assigned
→ Confirmed
→ Travelling (optional)
→ Checked In
→ In Progress
→ Checked Out
→ Completed
→ Reviewed/Locked
```

### Add/strengthen

- configurable check-in method;
- geofence/location validation where lawful/configured;
- manual override with reason and audit;
- late/missed visit escalation;
- no-access workflow;
- emergency extension;
- incomplete mandatory task warning;
- visit handover;
- visit signature/confirmation only where required;
- immutable completion summary;
- offline-safe visit workflow later.

---

# 11. Care Documentation / Daily Notes — PRESENT

Care documentation and care notes exist.

### Improve

- Structured note categories.
- Draft/final state where needed.
- Correction/addendum instead of destructive editing for completed records.
- Link notes to visit/task/care plan/risk.
- Mark concern/manager review required.
- Shift/day summary read model.
- Better chronology/timeline.
- Search/filter by person/date/category/author.
- Separate objective observations from free-text narrative.

### Add

- handover summary;
- manager review queue;
- late-entry indicator;
- addendum workflow;
- attachments/photos where policy permits;
- configurable note prompts/templates.

---

# 12. Health Observations — BASIC FOUNDATION PRESENT

Current domain contains health observations.

### Improve

- Typed observation definitions.
- Numeric/text/boolean result support.
- Unit validation.
- normal/expected ranges configured by appropriate workflows where used;
- trend API;
- source and recorder metadata;
- link to visit/task;
- correction history.

### Add

- configurable observation types;
- charts/trends for authorised users;
- escalation rules only when clinically/governance-approved;
- interoperability mapping to FHIR Observation later.

Do not build unvalidated diagnosis/clinical decision logic into this module.

---

# 13. Incidents — GOVERNANCE FOUNDATION PRESENT

Incident governance and investigation/CAPA migrations/tests exist.

### Improve

Full lifecycle:

```text
Report
→ Triage
→ Immediate actions
→ Investigation
→ Findings
→ Corrective/preventive actions
→ Review
→ Closure
```

### Add/strengthen

- witnesses/people involved;
- injury/impact fields where applicable;
- attachments/evidence;
- linked service user/staff/visit;
- severity matrix;
- manager notification;
- investigation owner;
- CAPA due dates;
- trend reporting;
- configurable notification/reporting flags;
- immutable closure/reopening history.

---

# 14. Safeguarding — PRESENT

Dedicated safeguarding governance exists.

### Improve

Safeguarding should remain more restricted than ordinary incidents.

- Fine-grained access permissions.
- Need-to-know access.
- Access auditing.
- Chronology.
- concern → review → referral/action → outcome lifecycle.
- linked incidents/documents.
- escalation deadlines.
- restricted notes/documents.
- reopening/history.
- safe exports.

Never expose safeguarding data through family portal or general reporting unless explicitly authorised by policy/permissions.

---

# 15. Medication / Native eMAR — ADVANCED FOUNDATION PRESENT

Current repository contains medication, MAR, production eMAR ledger, medication safety, stock, witnesses, PRN effects, corrections, escalation and reconciliation foundations.

### Improve/add

Follow the detailed `AI_CARE_EMAR_AND_INTEGRATIONS_ROADMAP.md`.

Main remaining work:

- richer MedicationOrder;
- Pharmacy entity;
- structured scheduling/due windows;
- PRN protocol;
- medication verification;
- deterministic MAR generation;
- concurrency/duplicate administration protection;
- manager exception dashboard;
- stock/reconciliation completion;
- application/domain refactor;
- dm+d terminology;
- optional pharmacy/external eMAR integrations.

---

# 16. Workforce / Staff — ADVANCED FOUNDATION PRESENT

Current repository contains workforce lifecycle, governance, development and compliance controllers/migrations/tests.

### Improve

Create a coherent workforce domain instead of isolated governance endpoints.

Target worker profile:

```text
Worker
├── Identity/contact
├── Employment
├── Role
├── Branches
├── Availability
├── Skills
├── Competencies
├── Training
├── DBS/right-to-work records
├── Documents
├── Supervisions
├── Appraisals
├── Restrictions
├── Leave
└── Lifecycle status
```

### Add/strengthen

- employment contracts/basic employment metadata;
- availability patterns;
- leave/absence;
- competency expiry;
- training matrix;
- supervision/appraisal scheduling;
- worker-document expiry alerts;
- skill matching with visits;
- worker status preventing assignment when suspended/inactive/restricted;
- staff self-service APIs later.

---

# 17. Timesheets — NEEDS STRONGER FIRST-CLASS MODULE

Visits provide useful source data, but build a clear timesheet workflow.

### Add

```text
Completed visits
     ↓
Timesheet entries
     ↓
Worker review (optional)
     ↓
Manager approval
     ↓
Locked pay period
     ↓
Payroll export/integration
```

Include:

- planned vs actual duration;
- travel time/mileage where configured;
- manual adjustment with reason;
- approval history;
- locked periods;
- export/integration boundary.

---

# 18. Finance / Invoicing / Payroll — FOUNDATION PRESENT

Payroll/invoice models and finance governance exist.

### Improve

Separate care delivery facts from finance calculations.

### Add/strengthen

- rate cards;
- funders/contracts;
- service rates by visit/time/day;
- invoice line generation from approved delivery;
- adjustments/credits;
- invoice approval;
- payment status;
- payroll calculation/export boundaries;
- mileage/travel rules where applicable;
- locked accounting periods;
- finance audit trail;
- CSV/accounting integrations later.

Avoid turning AI Care into a full accounting package; integrate with accounting/payroll products when required.

---

# 19. Messaging — STRONG FOUNDATION PRESENT

Conversation messaging, participants, attachments and governance functionality exists.

### Improve

- unread/read state per participant;
- message delivery state;
- archive/close thread;
- permission-aware participants;
- service-user context;
- attachment scanning/security;
- retention policy;
- search;
- moderation/admin investigation controls where appropriate.

### Add

- staff-to-staff handover threads;
- announcements;
- targeted organisation/branch messages;
- notification preference integration;
- email/SMS/push adapters without making messaging depend on them.

---

# 20. Notifications — FOUNDATION PRESENT

### Improve into event-driven notifications

Instead of controllers manually creating unrelated notifications, introduce domain/application events.

Examples:

```text
VisitMissed
MedicationOverdue
CarePlanReviewDue
RiskReviewDue
TrainingExpiring
DocumentExpiring
IncidentReported
SafeguardingEscalated
IntegrationFailed
```

Then route to:

```text
In-app
Email
SMS
Push
```

according to tenant/user preferences.

### Add

- notification preferences;
- priority/severity;
- acknowledgement where required;
- deduplication;
- quiet-hours rules where appropriate;
- escalation chain;
- delivery/retry tracking.

---

# 21. Family Portal — STRONG FOUNDATION PRESENT

Family portal services, queries, governance and document download functionality exist.

### Improve

- Explicit per-person consent/access grants.
- Fine-grained visibility categories.
- Access expiry/revocation.
- Family invitation lifecycle.
- Secure document sharing.
- Timeline filtering to family-safe events only.
- Family notification preferences.
- Audit every sensitive access.

### Add carefully

- approved care updates;
- visits summary;
- selected care-plan information;
- selected documents;
- messaging with care team;
- feedback/preferences.

Do not expose internal staff notes, safeguarding, investigations, privileged records or medication details by default.

---

# 22. Documents — FOUNDATION PRESENT

Supabase document storage and upload security exist.

### Improve

- Document metadata entity with category/type/version/status.
- Retention classification.
- Expiry/review date.
- Access classification.
- document versioning;
- signed URL/download authorization;
- malware/content checks where feasible;
- storage cleanup consistency;
- orphan detection;
- audit upload/download/delete/version events.

### Add

- document templates;
- generated PDFs/reports later;
- staff-document expiry workflows;
- person-document review workflows;
- secure sharing rules.

---

# 23. Reporting & Compliance — STRONG FOUNDATION PRESENT

Reporting compliance/data governance controllers and tests exist.

### Improve

Build reporting from stable read models/materialized queries rather than complex controller SQL where possible.

### Add report categories

- care-plan review status;
- assessment review status;
- high/open risks;
- missed/late visits;
- task completion;
- medication exceptions;
- incidents/safeguarding trends;
- workforce compliance;
- training expiry;
- rota utilisation;
- continuity;
- finance;
- audit/access;
- integration health.

### Platform capabilities

- saved report definitions;
- filters;
- CSV/PDF exports;
- scheduled reports;
- permission-aware fields;
- branch/org scope;
- export audit;
- large-report asynchronous jobs.

---

# 24. Complaints / Feedback — GOVERNANCE FOUNDATION PRESENT

Complaint governance exists.

### Improve/add

- complaint source;
- complainant/contact;
- category;
- acknowledgement target;
- investigation owner;
- actions;
- response/outcome;
- reopen/escalate;
- attachments;
- links to incidents/service users;
- trend reporting;
- deadlines/SLA configuration.

Also add a lighter feedback/compliment mechanism separate from formal complaints.

---

# 25. Capacity / Consent / Authority — ADVANCED FOUNDATION PRESENT

Capacity/authority and person governance are present.

### Improve

- Clearly separate consent, capacity assessment, representative/authority and restrictions.
- Version/history.
- validity dates;
- supporting documents;
- review due dates;
- scope of authority;
- decision-specific records where required;
- prominent but permission-aware person alerts.

These records should be linked to the workflows they govern rather than existing only as standalone records.

---

# 26. Integrations — GOOD FRAMEWORK FOUNDATION PRESENT

The repository already contains Integration Hub, import, public webhook, runtime worker, migrations and regression tests.

### Improve

Turn this into the standard boundary for every external provider.

```text
AI Care Domain
      ↓
Integration Contract
      ↓
Provider Adapter
      ↓
External System
```

### Required platform capabilities

- provider catalogue;
- tenant connection configuration;
- secret reference/storage;
- test connection;
- connection status;
- sync jobs;
- webhook ingestion;
- idempotency;
- retries/backoff;
- dead-letter/failure queue;
- import preview/staging;
- provenance;
- reconciliation/conflict handling;
- audit;
- health dashboard;
- disconnect/revoke.

### Future integrations

- dm+d terminology;
- pharmacy providers;
- optional external eMAR;
- NHS/FHIR/PDS when required/onboarded;
- email/SMS/push;
- maps/routing;
- accounting/payroll;
- identity/SSO for larger organisations.

Never let arbitrary tenant-supplied API endpoints write directly into core clinical/care records.

---

# 27. Audit & Record Immutability — GOOD FOUNDATION PRESENT

AuditEvent immutability is a strong foundation.

### Improve

- Add correlation/request ID.
- Capture source application/device where appropriate.
- Capture reason for sensitive changes.
- Separate security audit from clinical/care record history where useful.
- Provide permission-controlled audit search/export.
- Define retention.
- Ensure audit records cannot contain secrets.
- Make important completed care records use addendum/version/correction patterns instead of destructive updates.

---

# 28. Data Governance & Privacy — ADVANCED FOUNDATION PRESENT

Data governance/privacy-rights functionality exists.

### Improve/add

- data classification;
- retention schedules by record type;
- legal hold where required;
- subject-access/export workflow;
- correction/request workflow;
- deletion/anonymisation rules where legally appropriate;
- tenant offboarding;
- consent/access provenance;
- processing/export audit;
- backup retention alignment;
- data-processing inventory/documentation outside code.

Do not implement blanket deletion of care/clinical history without the applicable retention/legal rules.

---

# 29. Monitoring, Reliability & Operations — FOUNDATION PRESENT

Production monitoring/config validation and deployment files exist.

### Improve

- structured logs;
- correlation IDs;
- metrics;
- traces;
- health/readiness/liveness separation;
- DB/storage/external-provider health;
- alerting;
- error tracking;
- slow-query monitoring;
- background-job monitoring;
- backup monitoring;
- restore drills;
- migration safety;
- deployment rollback plan.

### Add operational targets

Define SLOs for:

- API availability;
- critical medication/visit endpoints;
- job processing;
- notification delivery;
- integration sync;
- backup/restore.

---

# 30. Database & Persistence Improvements

The database has grown through many migrations and some modules use direct/raw SQL.

### Improve

- Audit indexes using real query patterns.
- Add/verify foreign keys and uniqueness constraints.
- Add concurrency tokens where required.
- Use transactions for multi-record workflows.
- Replace scattered raw SQL business logic with repositories/application services where practical.
- Keep raw SQL for justified performance/reporting cases, but centralise/test it.
- Add migration CI checks.
- Test upgrades from production-like snapshots.
- Define backup/restore procedure.
- Avoid giant JSON blobs for core data that needs querying/versioning.

---

# 31. API Design Improvements

### Standardise

- API version strategy.
- ProblemDetails/error response format.
- Pagination.
- Filtering/sorting/search.
- Idempotency on sensitive commands.
- ETag/version/concurrency where needed.
- UTC storage + explicit tenant/user timezone handling.
- request validation;
- consistent route naming;
- consistent response contracts;
- deprecation policy.

### Add

- OpenAPI documentation quality gates.
- generated frontend client or typed API contracts where useful.
- endpoint permission documentation.
- rate limits per endpoint/tenant where appropriate.

---

# 32. Application Architecture Improvement

The repository has substantial functionality directly in API controllers and a very large `Program.cs`.

### Target

```text
HTTP Controller
    ↓
Application Use Case
    ↓
Domain
    ↓
Infrastructure
```

Progressively move logic into modules.

Example:

```text
AiCare.Application/Visits
AiCare.Application/Scheduling
AiCare.Application/Medication
AiCare.Application/Incidents
AiCare.Application/Safeguarding
AiCare.Application/Workforce
AiCare.Application/Integrations
```

Do not perform a massive rewrite. Refactor module-by-module with regression tests.

---

# 33. Testing — GOOD REGRESSION FOUNDATION, EXPAND FURTHER

The repository already has many regression test suites covering scheduling, care plans, governance, security, integrations, eMAR, reporting, workforce and other modules.

### Add/strengthen

- true PostgreSQL integration tests for critical persistence paths;
- concurrency tests;
- tenant-isolation matrix tests;
- authorization matrix tests;
- migration tests;
- contract/API tests;
- background-job tests;
- integration retry/idempotency tests;
- property/boundary tests for scheduling/time calculations;
- end-to-end critical journeys;
- load/performance tests for dashboard/report/list endpoints.

### Critical end-to-end journeys

```text
Tenant signup
→ admin setup
→ worker invite
→ service user onboarding
→ assessment
→ risk
→ care plan
→ schedule
→ visit
→ tasks
→ notes
→ completion
```

```text
Medication order
→ schedule
→ MAR
→ administration
→ stock
→ PRN/reconciliation if applicable
→ manager exception review
```

```text
Incident
→ investigation
→ actions
→ closure
→ reporting
```

---

# 34. Frontend Requirements to Match Backend

This repository is backend-focused, but the product should eventually expose coherent user journeys rather than one screen per endpoint.

Recommended frontend modules:

```text
Dashboard
People
Care
Rota
Visits
Medication
Workforce
Incidents
Safeguarding
Messaging
Family Portal
Finance
Reports
Integrations
Settings
```

### UX principles

- Role-specific dashboards.
- Important alerts first.
- Minimise clicks during visits/medication rounds.
- Autosave drafts where safe.
- Explicit confirmation for safety-critical actions.
- Clear offline/sync state when mobile/offline is introduced.
- Accessibility.
- Responsive tablet/mobile care workflows.
- Avoid showing every backend field to every role.

---

# 35. Mobile Carer Experience — MAJOR FUTURE WORKSTREAM

For home care, a strong mobile experience is essential.

Target:

```text
Today
├── Visits
├── Person summary
├── Care plan
├── Tasks
├── eMAR
├── Notes
├── Observations
├── Incidents
└── Handover
```

### Later offline capability

Only after online workflows are stable:

- encrypted local data;
- limited minimum dataset;
- operation IDs;
- ordered sync;
- conflict handling;
- stale-data warnings;
- remote session/device revocation;
- no silent loss of records.

---

# 36. Dashboard / Operational Intelligence — NEEDS PRODUCT-LEVEL READ MODELS

Create role-specific dashboards.

### Registered manager / admin

- visits today;
- missed/late visits;
- unallocated visits;
- care-plan reviews due;
- high/open risks;
- medication exceptions;
- incidents/safeguarding requiring action;
- staff compliance expiry;
- integration failures;
- outstanding approvals.

### Coordinator

- rota gaps;
- visit conflicts;
- unavailable workers;
- late/no-access visits;
- outstanding handovers.

### Carer

- today's visits;
- tasks;
- medication due;
- person alerts;
- unread handovers/messages.

Use dedicated read models/query services instead of assembling huge dashboards from many frontend calls.

---

# 37. Search — ADD PLATFORM-WIDE SEARCH

Add permission-aware search across appropriate records:

- people;
- workers;
- documents;
- care plans;
- incidents;
- messages;
- reports.

Search results must respect tenant, branch and role/sensitivity restrictions.

Do not expose safeguarding/restricted data through general search without explicit permission.

---

# 38. Configuration / Customisation — ADD CAREFULLY

Care providers need configuration, but unlimited customisation creates an unmaintainable product.

Support controlled configuration for:

- assessment templates;
- care-plan templates;
- task types;
- incident categories;
- reason codes;
- visit types;
- notification preferences;
- scheduling rules;
- document categories;
- report templates;
- feature flags.

Use versioned configuration and sensible defaults.

---

# 39. AI Features — LATER, HUMAN-IN-THE-LOOP

AI should assist staff rather than become an autonomous care decision-maker.

Useful future features:

- summarise approved care history;
- draft handover summaries;
- suggest documentation completeness issues;
- natural-language search over authorised records;
- draft administrative reports;
- identify operational patterns for manager review.

Do not allow AI to independently:

- diagnose;
- prescribe;
- alter medication orders;
- administer medication;
- publish care plans;
- close safeguarding cases;
- overwrite official records;
- make final clinical decisions.

Every AI-generated official-record suggestion should require appropriate human review.

---

# 40. Features Still Worth Adding for a Competitive Product

After strengthening existing modules, consider:

- Staff leave/absence management.
- Timesheet approval.
- Mileage/travel expenses.
- Contract/funder/rate management.
- Commissioner/funder portal only if customers require it.
- Staff self-service portal.
- Electronic forms/templates.
- Digital signatures/acknowledgements where appropriate.
- Organisation announcements.
- Handover module.
- Service-user goals/outcome tracking.
- Quality audits.
- Action/improvement plans.
- Policy/document acknowledgement.
- Training/competency matrix.
- Equipment/asset tracking only if customer demand exists.
- Controlled provider marketplace/integration catalogue.
- SSO for larger customers.
- Mobile push notifications.

Do not add every possible feature before pilots. Prioritise workflows customers actually use.

---

# 41. Recommended Delivery Order

## Phase 1 — Architecture & safety hardening

- Refactor the largest controller-owned business logic incrementally.
- Break down `Program.cs` composition/configuration.
- Standardise validation/errors/pagination.
- Strengthen tenant isolation.
- Strengthen permission model.
- Expand integration/database/concurrency tests.

## Phase 2 — Complete the core care record

- Person 360.
- Structured contacts/GP/pharmacy.
- Assessment templates/versioning.
- Risk lifecycle.
- Care-plan lifecycle and acknowledgement.
- Tasks.
- Timeline.
- Documentation/addendum workflows.

## Phase 3 — Home-care operations

- Scheduling/rota UX APIs.
- Recurrence and availability.
- Travel/skills/continuity rules.
- Visit lifecycle.
- Missed/late/no-access workflows.
- Handover.
- Timesheets.

## Phase 4 — Native eMAR

Follow the dedicated eMAR roadmap:

- MedicationOrder.
- Pharmacy.
- schedules;
- eMAR;
- PRN;
- stock;
- reconciliation;
- exception dashboard;
- dm+d.

## Phase 5 — Workforce

- Worker profile.
- availability;
- training;
- competencies;
- compliance;
- supervision/appraisal;
- leave;
- assignment restrictions.

## Phase 6 — Quality & governance

- incidents;
- safeguarding;
- complaints;
- quality audits;
- actions/CAPA;
- reporting/compliance dashboards.

## Phase 7 — Family & communications

- Family portal permissions/consent.
- Messaging.
- Notifications.
- Email/SMS/push adapters.

## Phase 8 — Finance

- rate cards/contracts;
- approved timesheets;
- invoices;
- payroll exports;
- accounting/payroll integrations.

## Phase 9 — External integrations

- integration catalogue;
- dm+d;
- pharmacy providers;
- optional external eMAR;
- FHIR/NHS/PDS where required;
- maps/routing;
- finance providers;
- SSO.

## Phase 10 — Mobile/offline

- carer-first mobile experience;
- secure offline visits;
- later offline medication only after rigorous conflict/safety design.

## Phase 11 — AI assistance

Add reviewed, assistive AI after the underlying structured workflows/data are reliable.

---

# 42. Priority Matrix

## P0 — Before serious production pilot

- Tenant isolation verification.
- Authorization/permission review.
- Critical audit/versioning.
- Production configuration/secrets.
- Backup/restore test.
- Monitoring/alerts.
- Person/care-plan/visit workflow stability.
- eMAR safety workflow if medication is enabled.
- Incident/safeguarding access controls.
- Critical regression/integration tests.
- Clinical/workflow/governance review appropriate to deployed features.

## P1 — Strong commercial MVP

- Person 360.
- Assessment templates.
- Care-plan review/acknowledgement.
- Advanced rota/visit delivery.
- Native eMAR.
- Workforce compliance.
- Family portal.
- Messaging/notifications.
- Operational dashboards.
- Reports.
- Timesheets/basic invoicing.

## P2 — Scale and integrations

- Pharmacy integrations.
- FHIR/NHS integrations where required.
- Accounting/payroll adapters.
- Route optimisation/maps.
- SSO.
- Mobile offline.
- scheduled reporting;
- larger-scale analytics.

## P3 — Differentiators

- Assistive AI.
- Advanced operational intelligence.
- richer automation;
- broader ecosystem integrations.

---

# 43. What Not to Do

- Do not rewrite the backend from scratch.
- Do not create dozens of microservices at the current stage.
- Do not put all new logic in controllers.
- Do not rely on role names alone for every authorization decision.
- Do not allow arbitrary external APIs to write into care/medication records.
- Do not make NHS/PDS/pharmacy integrations mandatory for the core product.
- Do not treat eMAR as simple CRUD.
- Do not destructively edit important historical care records.
- Do not build a full accounting/payroll platform when integrations can handle specialist functions.
- Do not add AI before the underlying structured workflows are dependable.
- Do not promise offline medication workflows until conflict/synchronisation safety is designed and tested.

---

# 44. Product Completion View

The target product should eventually look like:

```text
                         AI CARE
                            │
     ┌──────────────────────┼──────────────────────┐
     │                      │                      │
 CARE RECORD            OPERATIONS             WORKFORCE
     │                      │                      │
 People                  Rota                   Staff
 Assessments             Visits                 Skills
 Risks                   Tasks                  Training
 Care Plans              Check-in/out           Compliance
 Notes                   Handover               Leave
 Observations            Timesheets             Supervision
     │                      │                      │
     └──────────────┬───────┴──────────────┬──────┘
                    │                      │
                MEDICATION              QUALITY
                    │                      │
               Native eMAR             Incidents
               PRN                     Safeguarding
               Stock                   Complaints
               Reconciliation          Actions/CAPA
               Pharmacy                Audits
                    │                      │
     ┌──────────────┴──────────────────────┴──────────────┐
     │                                                    │
 PLATFORM                                             BUSINESS
     │                                                    │
 Auth/RBAC/MFA                                        Finance
 Multi-tenancy                                       Invoices
 Documents                                           Payroll export
 Messaging                                           Contracts/rates
 Notifications                                       Reports
 Family Portal
 Audit
 Privacy/Governance
 Monitoring
 Integrations
     │
 Integration Gateway
     │
 ┌───┼─────────┬─────────┬──────────┬──────────┐
 dm+d Pharmacy NHS/FHIR  Messaging Accounting  Other approved providers
```

---

# 45. Final Engineering Direction

AI Care already has considerably more than basic CRUD. The repository contains advanced governance, workforce, scheduling, visit, messaging, family portal, reporting, security, integration and eMAR foundations.

The next stage should therefore be **consolidation and productisation**, not uncontrolled feature creation.

For every module, follow this sequence:

```text
Existing implementation
       ↓
Regression tests
       ↓
Refactor business logic into Application/domain
       ↓
Complete missing workflow states
       ↓
Strengthen authorization + tenant isolation
       ↓
Add audit/version/concurrency
       ↓
Improve read models/API UX
       ↓
Pilot with real users
       ↓
Iterate from observed workflow
```

The goal is not simply to have many endpoints. The goal is to make each critical care workflow coherent, safe, testable, auditable, scalable and easy for carers/managers to use.
