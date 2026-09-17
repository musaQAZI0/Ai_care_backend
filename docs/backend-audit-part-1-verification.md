# Part 1 ? core care audit verification

Verified against the local working tree on 2026-09-17. Scope: sections 1?10 of AI_CARE_BACKEND_AUDIT_PART_1_CORE_CARE.md. This pass verifies source and available tests; it does not certify production behavior or implement fixes. Earlier local remediation and user edits were preserved.

## Verdict

The feature inventory is broadly supported by the code. The document is too optimistic about authentication failure behavior, lifecycle enforcement and transactional consistency. Central authorization already exists, but is inconsistently applied. Do not mark Part 1 production-ready based on feature presence alone.

## Section-by-section evidence

| Section | Verdict | Evidence and qualification |
|---|---|---|
| 1. Executive summary | Partially verified | Core care modules and tests exist. Statements about medication, finance, messaging and other later-part modules are outside this verification. A historical successful CI run was not independently checked. |
| 2. Target architecture | Supported as a recommendation | Application, Domain and Infrastructure projects exist; CarePlans/CarePlanLifecycleService.cs demonstrates separation. Many controllers contain SQL/workflow rules; Program.cs still contains a large endpoint surface. No rewrite is indicated by this review. |
| 3. Authentication/security | Features confirmed; higher-priority defects identified | Program.cs configures JWT validation, rate limiting, session validation, enrollment/portal restrictions, privileged-access and step-up middleware. AuthController implements lockout, MFA, recovery, password reset and refresh rotation. Several security helpers swallow errors and weaken enforcement; see findings below. |
| 4. Tenancy/authorization | Risk confirmed; proposed wording needs correction | HttpTenantContext and Infrastructure/ContextualAuthorizationService already centralize some rules. The problem is unsafe policy defaults and inconsistent adoption, rather than complete absence of central authorization. PermissionTests already cover worker assignment and family isolation; they do not establish systematic cross-branch/tenant safety. |
| 5. Person lifecycle | Partially confirmed | PersonJourneyController has admission checks, same-organization transfer validation, discharge requirements and explicit execution-strategy transactions. PersonLifecycleController has separate direct status mutations and raw SQL/event writes outside a shared transaction. The claim of transactional lifecycle updates cannot be generalized to all routes. |
| 6. Assessments/risks | Feature inventory confirmed; hardening still needed | AssessmentRiskGovernanceController implements Draft/InReview/Current, role-restricted approval, correction versions, scoring and alerts. Migration 20260830160000 has unique partial indexes preventing multiple Current assessments/risks for a person/type. Approval is transactional, but other writes and audit are separate. AssessmentRiskGovernanceRegressionTests exists; database execution pending. |
| 7. Care plans | Architecture and stated controls confirmed in source | Application lifecycle service, Infrastructure store and Domain lifecycle types exist. Store checks revisions and conditional updates; controller invokes FamilyPortal permission checks for viewing/signing. Lifecycle/superseding regression tests exist. This is a sound architectural reference, not a blanket safety certification. |
| 8. Scheduling/visits | Features confirmed; concrete scope gaps confirmed | AdvancedSchedulingController checks workers by organization only. VisitOperationsController gives managers/coordinators access after an organization-only visit lookup. Exception progression, dashboard and handover acknowledgement lack consistent branch/resource checks. |
| 9. Incidents | Governed closure confirmed, with qualifications | IncidentGovernanceController requires completed investigation and no Open CAPA before Close. Investigation content is overwritten on conflict; event/audit/state writes are not one transaction, and closure checks are not serialized with action creation. Legacy updates set status Updated and deletion sets Archived without the closure workflow. These are lifecycle escape routes, not a demonstrated direct Closed-status bypass. |
| 10. Capacity/authority/best interest | Feature inventory and branch inconsistency confirmed | CapacityAuthorityController validates supported-decision evidence, current lacks-capacity prerequisite, verified authority, date ordering and revocation; records carry version/supersession fields. Reads are person/organization scoped, while current lookups and mutations also use the caller branch. Administrator access to another branch can therefore behave inconsistently. |

## Confirmed source findings to prioritize

### P1-01 ? Authentication errors can disable security checks (high)

AuthController.cs:237?245: IsLockedOut returns false on any exception; GetMfaState returns disabled on query/decryption failure; IsMfaRequired returns false on failure. Failed-login/MFA persistence errors are swallowed. Login consumes these fallback results. This is a confirmed fail-open implementation; exploitability under particular dependency failures still needs fault-injection tests. RevokeAllRefreshTokens also suppresses errors (line 255), so credential changes can report success without confirmed revocation.

Required verification/fix: distinguish missing optional data from operational errors; fail closed on unavailable security state; require successful revocation; test query, decryption and persistence failures.

### P1-02 ? Tenant policy contains overbroad defaults (high)

HttpTenantContext.cs:16?41 maps BackOffice to IsPlatformOwner and returns true before checking organization in CanAccess. Missing branch broadens access; missing organization falls back to TenantDefaults. ContextualAuthorizationService grants BackOffice care-resource access after this helper. Endpoint-specific organization filters sometimes compensate, but not uniformly. The intended BackOffice/platform-owner distinction must be explicitly defined before changing the role contract.

Required verification/fix: deny cross-organization access for ordinary tenant roles, define explicit organization-wide roles and missing-claim behavior, add tests covering every role and both tenant/branch boundaries.

### P1-03 ? Visit and scheduling scopes are inconsistent (high)

VisitOperationsController.cs:48 acknowledges any known handover ID within the organization without checking visit assignment. Lines 57?58 allow managers/coordinators any organization visit without a branch check. Exception progression (line 36) and dashboard (line 54) are organization scoped. AdvancedSchedulingController.WorkerExists checks organization only, permitting same-organization cross-branch absence operations.

Required verification/fix: apply common resource authorization to reads and mutations, including handover acknowledgement and exception IDs. Negative tests must include assigned/unassigned workers and managers from another branch.

### P1-04 ? General lifecycle status changes bypass journey prerequisites (high)

PersonLifecycleController.ChangeStatus accepts Active and Discharged with a reason alone. It does not invoke PersonJourneyController admission/discharge rules or create corresponding admission/discharge records. Referral, event, status and audit writes also lack one encompassing transaction. Dedicated journey transactions do not protect the separate status route.

Required verification/fix: route governed transitions through one use case, reject unsupported shortcuts, verify rollback and concurrent transitions. Preserve the user's existing PersonJourneyController edit.

### P1-05 ? Password-reset delivery is absent from the inspected flow

AuthController.ForgotPassword (lines 179?191) generates/stores a hashed reset token, audits the request, and returns the plaintext token only in Development/Testing. It does not call an email sender or enqueue delivery. A family invitation SMTP sender exists, but is not connected to password reset. Production reset delivery is therefore not demonstrated by this implementation; external delivery was not inspected.

### P1-06 ? Public health/config exposure remains

Program.cs:245?282 returns ex.Message from /health/db and maps /status/config without endpoint authorization. Existing PermissionTests.ConfigStatusDoesNotExposeSecrets expects anonymous 200. The endpoint reports configuration booleans, not secret values, but remains publicly accessible. Return generic health failures and define privileged configuration access.

### P1-07 ? Governed incident workflow is not fully atomic or isolated

IncidentGovernanceController.Close checks investigation/actions, then separately writes status, event and audit. AddAction does not reject a closed incident; SaveInvestigation can change incident status after closure. EfCoreCareRepository.UpdateIncident (line 421) resets status to Updated; DeleteIncident archives it. No direct arbitrary Closed assignment was found in that legacy update path. A governed reopen/archive policy and transaction/locking tests are needed.

### P1-08 ? Assessments/capacity need consistent resource branch handling

AssessmentRiskGovernanceController.CanAccess uses caller branch even for administrators, unlike HttpTenantContext.IsOrganizationWide. Capacity authority reads allow tenant.CanAccess, but current-record queries and mutations bind caller branch. Writes commonly use caller branch rather than the person's actual branch. Approval's conditional transition row count is not checked. These deserve branch, concurrent approval and rollback regressions, not just refactoring.

## Additional verification limits

- Rate limiting uses RemoteIpAddress; no ForwardedHeaders/UseForwardedHeaders reference was found in backend source. Hosting-level forwarded-header configuration is unverified, so effective production client-IP behavior remains unresolved.
- Authentication/session/clinical PostgreSQL test files exist, but existence is not a passing runtime result.
- No live database, deployed configuration, reset email delivery or historical CI run was inspected.
- No medical/legal compliance conclusion is made; this is implementation verification.

## Tests run in this pass

Command from the parent workspace:

```powershell
dotnet test backend/AiCare.Backend.sln --no-restore --verbosity quiet --filter 'FullyQualifiedName!~Regression' --logger 'trx;LogFileName=part1-verification.trx' --results-directory artifacts/audit-remediation-tests
```

Result: 113 passed, 0 failed, 0 skipped. This command builds current projects and excludes PostgreSQL regression classes by name. Result artifact: ../../artifacts/audit-remediation-tests/part1-verification.trx.

Docker engine availability check failed: dockerDesktopLinuxEngine pipe unavailable. PostgreSQL workflow/concurrency tests were not run. The previous session also found no listener at localhost:5432; that port was not rechecked in this pass.

## Recommended Part 1 remediation order

1. Security-state fail-open handling and session revocation failure tests.
2. Tenant/branch policy and consistent scheduling/visit resource authorization.
3. Lifecycle bypass removal and atomic journey/status transitions.
4. Incident transition/closure concurrency and transaction safety.
5. Assessment/capacity branch and approval consistency.
6. Password-reset delivery, health response sanitization and proxy configuration verification.
7. Application-service extraction after behavior is covered by tests.

Remain on Part 1 until these findings have been addressed and PostgreSQL regressions have passed. No Part 2/3 implementation changes were made during this verification pass.
