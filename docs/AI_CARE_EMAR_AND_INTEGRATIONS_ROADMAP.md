# AI Care — Native eMAR & Integration Roadmap

## 1. Purpose

This document records the current AI Care backend position and the agreed direction for medication management, native eMAR, pharmacy handling, and future integrations.

The product direction is:

- AI Care remains the primary care-management platform.
- AI Care owns a native eMAR rather than depending on an external eMAR provider.
- External eMAR, pharmacy, NHS/FHIR, and terminology integrations remain optional adapters.
- A tenant can use AI Care medication/eMAR even when its pharmacy has no API integration.
- Tenants can manually add/manage pharmacies, but only AI Care-supported external providers should be connectable to production medication workflows.
- Medication safety, tenant isolation, provenance, auditability, and human review are core requirements.

---

## 2. Current Backend Foundation — Already Done

The repository already contains a strong Phase 1 foundation.

### Platform

- ASP.NET Core Web API.
- PostgreSQL with Entity Framework Core.
- JWT authentication.
- Role-based authorization.
- Organisation and branch multi-tenancy fields on core records.
- Supabase-backed document storage support.
- Health/config/storage endpoints.
- Audit events.
- Payroll/invoice foundations.
- Notifications and messaging.
- Family portal functionality.
- Render deployment configuration.

### Core care domain

Existing domain/API coverage includes:

- Service users.
- Person records.
- Care workers.
- Visits and rota functionality.
- Care plans.
- Care-plan outcomes.
- Risk assessments.
- Care assessments.
- Family members.
- Documents.
- Care notes.
- Health observations.
- Incidents.
- Reporting/compliance foundations.

### Medication/eMAR foundation

The current backend already has:

- Medication records linked to service users.
- Medication Administration Records (MAR).
- Scheduled and administered timestamps.
- Administration outcomes and notes.
- PRN flag support.
- Medication stock operations.
- Receipt/return/waste/disposal/correction/adjustment transactions.
- Negative-stock prevention.
- Reconciled stock balance checks.
- Reorder-level/low-stock escalation logic.
- Witness handling for relevant stock operations.
- Self-witness rejection.
- Same-organisation active-witness validation.
- PRN-effect recording.
- Prevention of duplicate PRN-effect recording.
- eMAR correction events that append rather than overwrite the original event.
- eMAR escalation acknowledgement/resolution/reopening.
- Immutable audit-event enforcement.
- Organisation/branch scoping around medication safety operations.

This means the native eMAR should be evolved from the current implementation rather than rebuilt from zero.

---

## 3. Target Product Architecture

```text
AI CARE
│
├── Care Record
│   ├── Service Users
│   ├── Assessments
│   ├── Care Plans
│   ├── Risks
│   ├── Notes
│   ├── Observations
│   ├── Incidents / Safeguarding
│   └── Timeline
│
├── Operations
│   ├── Rota
│   ├── Visits
│   ├── Tasks
│   ├── Check-in / Check-out
│   ├── Staff
│   ├── Timesheets
│   └── Handover
│
├── Medication
│   ├── Medication Orders
│   ├── Medication Schedules
│   ├── Native eMAR
│   ├── PRN Protocols
│   ├── Stock
│   ├── Reconciliation
│   ├── Pharmacy Directory
│   ├── Escalations
│   └── Medication Audit / Ledger
│
└── Integration Layer
    ├── dm+d Terminology
    ├── Pharmacy Providers
    ├── External eMAR Providers
    ├── NHS/FHIR Providers
    └── Future Approved Providers
```

External systems enhance AI Care; they must not be required for the native eMAR to function.

---

## 4. Main Architecture Refactor — High Priority

### Current issue

Important eMAR safety/business logic currently exists directly inside API controllers and uses raw SQL for several operations.

This works as a foundation, but production medication workflows should have clearer separation of responsibilities and be easier to test independently.

### Target

```text
Controller
    ↓
Application Command / Service
    ↓
Domain Rules
    ↓
Repository / Unit of Work
    ↓
PostgreSQL
```

Controllers should mainly:

1. authenticate/authorize;
2. validate the request contract;
3. call an application use case;
4. return an HTTP result.

Medication safety decisions should live below the API layer.

### Suggested Application structure

```text
AiCare.Application/
└── Medication/
    ├── Orders/
    ├── Scheduling/
    ├── Administration/
    │   ├── AdministerMedicationCommand.cs
    │   ├── RefuseMedicationCommand.cs
    │   ├── OmitMedicationCommand.cs
    │   └── CorrectAdministrationCommand.cs
    ├── Prn/
    ├── Stock/
    ├── Reconciliation/
    ├── Escalations/
    ├── Pharmacy/
    └── Integrations/
```

### Suggested Domain structure

Split the medication domain out of the large `Models.cs` file over time:

```text
AiCare.Domain/
└── Medication/
    ├── MedicationOrder.cs
    ├── MedicationSchedule.cs
    ├── MedicationAdministration.cs
    ├── MedicationLedgerEvent.cs
    ├── PrnProtocol.cs
    ├── MedicationStockTransaction.cs
    ├── MedicationReconciliation.cs
    ├── Pharmacy.cs
    └── Enums/
```

Do this incrementally. Do not break existing endpoints unnecessarily.

---

## 5. Upgrade the Medication Model

The current `Medication` record is useful for the prototype but too small for the intended medication workflow.

### Target MedicationOrder

```text
MedicationOrder
├── Id
├── ServiceUserId
├── DmdCode
├── MedicationName
├── Form
├── Strength
├── Dose
├── DoseUnit
├── Route
├── Frequency
├── Schedule
├── StartDate
├── EndDate
├── IsPrn
├── PrnProtocolId
├── MinimumInterval
├── MaximumDose
├── Indication
├── Instructions
├── Prescriber
├── SupplyingPharmacyId
├── Source
├── ExternalReference
├── VerificationStatus
├── Status
├── OrganizationId
└── BranchId
```

Do not remove old fields until migration/backward-compatibility requirements have been addressed.

### Provenance

Medication data should retain where it came from, for example:

- authorised manual entry;
- pharmacy integration;
- imported record;
- supported external eMAR;
- future NHS/FHIR integration.

An external update must not silently destroy the previous medication history.

---

## 6. Build Pharmacy as a First-Class Domain

The current medication model stores pharmacy as a string. Replace/evolve this into a real entity.

### Pharmacy

```text
Pharmacy
├── Id
├── OrganizationId
├── Name
├── Address
├── Postcode
├── Phone
├── Email
├── ContactPerson
├── IsActive
├── ExternalProvider
└── ExternalPharmacyId
```

A service user should be able to have a preferred/nominated pharmacy, while a medication order can preserve the pharmacy that actually supplied it.

### Important

Do **not** store arbitrary provider API credentials directly on the Pharmacy entity.

Connection configuration belongs in a separate secure integration model.

---

## 7. Pharmacy Workflow Without an API

AI Care must work with pharmacies that expose no software integration.

```text
Pharmacy supplies medication
          ↓
Authorised staff receives medication
          ↓
Medication order reviewed/entered in AI Care
          ↓
Stock receipt recorded
          ↓
Medication schedule / MAR generated
          ↓
Carer administers medication
          ↓
eMAR ledger + stock + audit
```

Tenant UI should allow:

- Add pharmacy.
- Edit pharmacy details.
- Assign preferred pharmacy to a service user.
- Associate supplying pharmacy with a medication order.
- Record medication receipt.
- Reconcile stock.
- Record return/waste/disposal using governed workflows.

A missing pharmacy API must never stop the care organisation from using AI Care eMAR.

---

## 8. Pharmacy Integration Architecture

Do not allow a tenant to paste an arbitrary URL/API key and let that external service modify live medication data.

Use approved provider adapters.

```text
IPharmacyIntegration
        │
        ├── ProviderAIntegration
        ├── ProviderBIntegration
        ├── FutureFhirIntegration
        └── FutureApprovedProvider
```

Suggested interface responsibilities:

```text
TestConnection
PullMedicationChanges
PullSupplyInformation
PushSupportedAcknowledgements
GetSyncStatus
```

Exact capabilities must be provider-specific; do not assume every pharmacy API supports the same operations.

### Tenant UI

```text
Settings
└── Pharmacy & Medication Integrations
    ├── Pharmacies
    │   ├── Add Pharmacy
    │   └── Manage Pharmacy
    │
    └── Integrations
        ├── Supported Provider A      [Connect]
        ├── Supported Provider B      [Connect]
        └── Request an Integration
```

### Request Integration

Allow tenants to submit:

- provider/pharmacy system name;
- website/contact details;
- tenant account/reference if appropriate;
- business requirement;
- notes.

AI Care administrators then evaluate the provider before implementing/enabling an adapter.

---

## 9. Native eMAR — Required V1 Workflow

Target medication administration flow:

```text
Medication Order
      ↓
Medication Schedule
      ↓
Due MAR Entry
      ↓
Medication Round
      ↓
Administer / Refuse / Omit / Unavailable
      ↓
Immutable Ledger Event
      ↓
PRN Follow-up / Exception Handling
      ↓
Stock Transaction
      ↓
Escalation if required
      ↓
Manager Exception Dashboard
```

### Required capabilities

#### Medication rounds

- Show medication due by service user/time window.
- Clear overdue/upcoming state.
- Prevent accidental duplicate administration submissions.
- Preserve server timestamp and actor identity.
- Record relevant visit/context where required.

#### Outcomes

Support governed outcome/reason handling for cases such as:

- administered;
- refused;
- omitted/not given;
- unavailable;
- other approved exception categories.

Use controlled reason codes where practical instead of relying only on free text.

#### Corrections

Never overwrite the original medication administration history.

```text
Original event
      ↓
Correction event
      ↓
Reason + actor + timestamp
```

The current append-only correction direction should be retained.

---

## 10. Medication Scheduling & Due Windows

Add a first-class scheduling model rather than relying only on a schedule string.

The scheduling layer should support, as requirements are validated:

- specific administration times;
- frequency;
- start/end dates;
- PRN orders;
- due windows;
- discontinued/paused medication;
- schedule changes with version/provenance;
- timezone-aware behaviour;
- daylight-saving-time edge cases.

MAR generation must be deterministic and testable.

Do not silently regenerate historical MAR records when a future medication schedule changes.

---

## 11. PRN Protocols

PRN should become a first-class governed workflow.

Suggested model:

```text
PrnProtocol
├── MedicationOrderId
├── Indication
├── DoseInstructions
├── MinimumInterval
├── MaximumDose
├── MaximumDosePeriod
├── WhenToEscalate
├── EffectReviewRequired
├── EffectReviewAfterMinutes
├── Instructions
└── ReviewMetadata
```

Existing PRN-effect functionality should be preserved and moved behind application/domain services.

The UI should make required follow-up obvious and expose unresolved PRN-effect reviews to managers.

---

## 12. Stock & Reconciliation

Existing stock transaction functionality should be retained and expanded.

### Stock ledger

```text
Receipt                  + quantity
Administration           - quantity
Return                   - quantity
Waste                    - quantity
Disposal                 - quantity
Correction/Adjustment    +/- quantity
```

Never silently edit old stock transactions. Corrections should be traceable.

### Reconciliation

Add an explicit reconciliation workflow:

```text
Expected balance
       ↓
Physical count
       ↓
Match?
 ┌─────┴─────┐
Yes          No
 ↓            ↓
Close     discrepancy
          reason/review
              ↓
          correction if authorised
```

Record actor, timestamp, discrepancy, reason, witness where required, and resulting balance.

---

## 13. eMAR Exception Dashboard

Managers need a medication safety view rather than finding exceptions manually.

Suggested APIs/UI should expose:

- overdue medication;
- omitted/refused doses requiring review;
- unresolved PRN-effect reviews;
- low stock;
- reconciliation discrepancies;
- failed integration/sync events;
- pending medication verification;
- open eMAR escalations.

All results must be organisation/branch scoped and permission controlled.

---

## 14. Integration Layer

Native AI Care medication/eMAR remains the source of its own operational workflow. Integrations should be isolated behind contracts.

### Core abstractions

```text
IMedicationTerminologyService
IPharmacyIntegration
IExternalMedicationProvider
IHealthcareInteroperabilityProvider
```

Possible implementations over time:

```text
IMedicationTerminologyService
└── DmdTerminologyService

IExternalMedicationProvider
├── CamascopeProvider (optional, if commercially/technically supported)
└── FutureExternalEmarProvider

IHealthcareInteroperabilityProvider
└── FhirProvider
```

Do not put provider-specific DTOs or API logic inside the core medication domain.

---

## 15. dm+d Terminology

Add dm+d through `IMedicationTerminologyService` when the medication model is ready.

Use it for medication terminology/coding and lookup support, not as a substitute for clinical judgement.

Store both the external identifier/code and the human-readable display data needed for historical records.

Do not let a future terminology refresh silently rewrite the meaning of an already-recorded administration event.

---

## 16. FHIR Readiness

Do not redesign the database as a FHIR database.

Keep AI Care's own domain model and create a mapping boundary.

```text
AI Care Domain
      ↓
FHIR Mapping Layer
      ↓
External FHIR API
```

Potential mappings include:

- Service User → Patient.
- Medication Order → MedicationRequest.
- Administration → MedicationAdministration.
- Allergy data → AllergyIntolerance.
- Observation → Observation.
- Care Worker → Practitioner where appropriate.
- Organisation → Organization.

FHIR is an interoperability representation, not the core business model.

---

## 17. NHS/PDS Strategy

PDS should be treated as a later integration, not a V1 dependency.

AI Care must retain its own internal `ServiceUserId`.

```text
ServiceUser
├── Id                     ← AI Care UUID
├── Health/NHS Identifier  ← external identifier when available
└── Verification metadata  ← future
```

Do not use NHS Number as the database primary key.

Add an abstraction later so demographic providers can be introduced without coupling the ServiceUser domain to PDS.

---

## 18. Native vs External eMAR Modes

Long-term tenant configuration may support:

```text
MedicationMode
├── NativeEmar
├── ExternalEmar
└── Hybrid
```

`NativeEmar` should be the standard AI Care mode.

External/hybrid modes must define a clear source of truth per data category. Never allow two systems to independently modify the same medication administration record without explicit synchronization/conflict rules.

For example:

```text
Medication orders     → External provider (if configured)
Administrations       → AI Care
Stock                 → AI Care
```

or another explicitly supported mapping.

Do not create a generic unrestricted hybrid mode.

---

## 19. Integration Configuration & Security

Create a separate integration configuration model instead of putting secrets on Pharmacy or Medication records.

Suggested concepts:

```text
IntegrationConnection
├── Id
├── OrganizationId
├── BranchId (optional)
├── ProviderType
├── ProviderAccountReference
├── Status
├── LastSuccessfulSyncAt
├── LastFailedSyncAt
├── ConfigurationMetadata
└── SecretReference
```

Secrets should be handled through appropriate secure configuration/secret storage and should not be returned by normal API responses or written to logs/audit details.

Each provider should receive only the minimum data/capabilities required for its integration.

---

## 20. Synchronisation & Provenance

Every imported medication-related item should be traceable.

Recommended metadata:

```text
SourceSystem
ExternalReference
ImportedAt
LastSyncedAt
SourceVersion
VerificationStatus
VerifiedBy
VerifiedAt
```

Integration processing should support:

- idempotency;
- duplicate prevention;
- retry handling;
- failure logging;
- sync status;
- controlled conflict handling;
- tenant isolation;
- audit/provenance.

Do not silently discard conflicting external updates.

---

## 21. Data Integrity Requirements

Before production pilots, medication workflows need explicit tests/controls for:

- duplicate submissions;
- concurrent administration attempts;
- transaction boundaries;
- optimistic concurrency/version checks where appropriate;
- server-generated identifiers;
- server timestamps;
- tenant/branch isolation;
- role/permission boundaries;
- append-only administration/correction history;
- referential integrity;
- timezone/DST behaviour;
- retries/idempotency;
- integration replay/duplicate messages.

Medication administration is not a normal CRUD workflow. Historical clinical records must not be silently replaced.

---

## 22. Offline Strategy

Do not rush offline eMAR.

Start online-first while the medication model and safety workflow stabilise.

Before medication administration is allowed offline, design and test:

- encrypted local storage;
- device/user authentication;
- unique operation IDs;
- ordered sync;
- duplicate protection;
- conflict detection;
- server acknowledgement;
- expired/stale medication-order handling;
- clear unsynchronised state;
- safe recovery after app/device failure.

Never silently discard an offline medication event.

---

## 23. Clinical Safety & Governance Workstream

The software implementation and clinical/governance review should progress together.

The care-provider/client team can supervise real-world workflows and validate operational requirements, but formal safety/governance responsibilities should still be handled appropriately for the product and deployment context.

Maintain at minimum:

- medication workflow requirements;
- hazard log;
- identified safety risks;
- mitigations;
- traceability from requirements to tests;
- release evidence;
- incident/feedback process;
- change-control history;
- clinical review/sign-off process where applicable.

AI features must not independently prescribe, diagnose, alter medication orders, administer medication, or silently change official care records.

---

## 24. Testing Plan

### Unit tests

Prioritise domain/application tests for:

- due-dose calculation;
- administration eligibility;
- duplicate administration prevention;
- PRN minimum interval/maximum-dose rules where configured;
- PRN follow-up creation;
- stock calculations;
- negative-stock prevention;
- reconciliation;
- correction logic;
- escalation state transitions;
- witness validation;
- tenant isolation.

### Integration tests

Test:

- PostgreSQL transactions;
- concurrent requests;
- API authorization;
- cross-tenant access rejection;
- migrations;
- provider adapter idempotency;
- provider failure/retry behaviour.

### End-to-end scenarios

Test complete journeys such as:

```text
Create medication
→ verify
→ generate schedule
→ generate MAR
→ administer
→ decrement stock
→ audit
```

and:

```text
PRN due/required
→ administer
→ create effect review
→ record effect
→ resolve escalation
```

and:

```text
Pharmacy delivery
→ receipt
→ reconciliation
→ low stock/reorder threshold later
→ manager review
```

---

## 25. Recommended Implementation Phases

### Phase A — Refactor existing eMAR foundation

- Move eMAR business logic out of controllers.
- Introduce application commands/services.
- Introduce repositories/unit-of-work boundaries where useful.
- Preserve existing API behaviour.
- Add regression tests before major model changes.

### Phase B — Medication domain upgrade

- Introduce MedicationOrder.
- Add structured scheduling.
- Add medication status/version/provenance.
- Introduce PRN protocol.
- Add verification state.
- Migrate existing Medication data safely.

### Phase C — Pharmacy domain

- Create Pharmacy entity.
- Add tenant pharmacy CRUD.
- Add preferred pharmacy to service-user workflow.
- Add supplying pharmacy to medication order.
- Remove dependence on free-text pharmacy over time.

### Phase D — Complete native eMAR V1

- Due medication/round APIs.
- Governed outcomes/reasons.
- Duplicate/concurrency protection.
- PRN workflow.
- Stock ledger.
- Reconciliation.
- Corrections.
- Escalations.
- Manager exception dashboard.

### Phase E — Integration framework

- Add provider contracts.
- Add IntegrationConnection model.
- Add sync/provenance/idempotency infrastructure.
- Add tenant integration settings UI/API support.
- Add Request Integration workflow.

### Phase F — dm+d

- Implement medication terminology provider.
- Add coded medication search/selection.
- Preserve historical display/provenance.

### Phase G — External pharmacy providers

Only when a real customer/provider requirement exists:

- evaluate provider API;
- security/data-flow review;
- map supported capabilities;
- implement dedicated adapter;
- sandbox/integration testing;
- controlled tenant enablement.

### Phase H — NHS/FHIR/PDS

Implement based on customer/business/onboarding requirements rather than making them blockers for native AI Care.

### Phase I — Optional external eMAR adapters

Support systems such as Camascope only when there is a commercial/customer requirement and a supported integration route.

AI Care's native eMAR remains independent.

---

## 26. Immediate Development Backlog

Recommended order from the current repository state:

1. Add regression tests around current eMAR safety endpoints.
2. Refactor `EmarOperationsController` business logic into Application services/commands.
3. Make eMAR ledger concepts first-class domain/application concepts instead of controller-owned SQL details.
4. Introduce `MedicationOrder` without immediately deleting the existing Medication contract.
5. Create `Pharmacy` entity and migrations.
6. Add tenant pharmacy management APIs.
7. Add preferred pharmacy/supplying pharmacy relationships.
8. Introduce structured medication scheduling and deterministic MAR generation.
9. Formalise PRN protocols.
10. Formalise medication verification/reconciliation.
11. Add concurrency/idempotency protection for medication administration.
12. Add manager eMAR exception APIs.
13. Add `IMedicationTerminologyService`.
14. Add `IPharmacyIntegration` and `IExternalMedicationProvider` contracts.
15. Add secure `IntegrationConnection` configuration.
16. Add integration sync/provenance records.
17. Integrate dm+d.
18. Add pharmacy adapters only for providers required by actual customers.
19. Add FHIR mappings/integrations later.
20. Add PDS only when the product use case and onboarding requirements justify it.

---

## 27. Definition of Native eMAR V1 Complete

Native eMAR V1 should not be considered complete merely because a MAR record can be created.

Minimum completion criteria:

- Structured medication order exists.
- Pharmacy can be recorded without requiring an API.
- Medication schedule can generate predictable due MAR entries.
- Authorised staff can record governed administration outcomes.
- Duplicate/concurrent administration is protected.
- PRN protocol and required follow-up are supported.
- Stock ledger is traceable.
- Reconciliation workflow exists.
- Corrections are append-only and traceable.
- Escalations are visible and manageable.
- Manager exception view exists.
- Every action is tenant scoped and auditable.
- Key workflows have automated tests.
- Clinical/workflow review has been performed before production use.

---

## 28. Product Principle

The long-term principle is:

> **AI Care owns the care workflow and native eMAR. External systems are controlled integrations around the product, not dependencies that define the product.**

This gives care organisations three practical paths:

```text
1. AI Care Native eMAR + Manual Pharmacy

2. AI Care Native eMAR + Connected Pharmacy Provider

3. AI Care + Supported External eMAR/Medication Provider
```

The first path must always remain fully usable. This allows smaller home-care organisations to adopt AI Care without waiting for pharmacy/NHS integrations, while the integration architecture keeps the platform ready for larger customers and future interoperability.
