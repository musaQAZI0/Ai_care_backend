# Worker workflow API failures: deployment verification

Reported endpoints: custom-forms GET (404), media-evidence GET (404), delivery/attendance POST (404), care-operations/monitoring GET (500).

## Source findings

The frontend paths match local backend routes. CustomFormsController.cs and VisitMediaEvidenceController.cs, plus migrations 20261115130000_AddCustomFormSubmissions and 20261115140000_AddVisitMediaEvidence, were untracked at inspection. The attendance action in VisitDeliveryController.cs was an uncommitted addition. A release built only from committed source cannot contain these local additions. This is strong evidence of a deployment mismatch, but live route availability and record visibility have not been confirmed. The same 404 can also intentionally protect a record outside the current tenant/branch/worker scope.

Do not redirect attendance to separate location and visit-status writes: the new action retains atomic attendance evidence and status changes. Never retry a clinical POST automatically or bypass record permissions to address a 404.

Monitoring uses raw SQL. Its optional worker/person UUID parameters were bound as untyped DBNull when absent, which can cause PostgreSQL parameter-type errors. The query now binds DbType.Guid even for null filters. Explicit aliases now preserve all three outcome columns separately and emit the camel-case visit fields expected by the frontend. Previously the three unaliased coalesce expressions collided under one dictionary key. These source defects are fixed locally; the production exception is still needed to confirm the reported 500's cause.

## Owner checks before release

1. In Render, compare the deployed commit with the intended backend release. Review the pending backend working tree and its dependencies before committing; it contains other work. Include required controllers, attendance changes, safety tests and migration files in a coherent release.
2. Run the normal backend tests/build/release gates. Focused regression source: CareOperationsWebRegressionTests.MonitoringSupportsOptionalUuidFiltersAndDistinctOutcomeFields (all four combinations), VisitAttendanceRegressionTests, VisitMediaEvidenceRegressionTests and AssessmentTemplateRegressionTests.CustomForms.cs. No tests, builds or migrations were executed by the agent.
3. Review and apply the normal deployment migrations to the intended database through the established migration gate. Verify __EFMigrationsHistory includes the custom-form/media migrations and 20261006120000_AddCareOperationsControl. Do not edit an applied migration or seed fake success data to suppress an error.
4. Deploy the validated backend before relying on the new frontend features. For an accessible person/visit, custom-form/media history should return a valid empty or populated result. Check denied tenant/branch/worker access still returns the existing protected response.
5. Test attendance with a dedicated synthetic visit, valid device location and normal consent/confirmation. Check a failed/lost response by reading visit state before another explicit action. Do not probe the POST with a real visit merely to check route existence.
6. Open monitoring with no optional filters, worker-only, person-only and both. Verify IDs, timestamps and all three outcome fields; verify nonmatching filters exclude records and branch restrictions still hold.

If monitoring still fails, capture the exception/SQLSTATE from Render logs at the request time. In Chrome: F12 > Network > select monitoring?from=... > Response. Share only the response/error text, never authorization headers or connection strings. An undefined-table error (42P01), undefined-column error (42703) or parameter-type error (42P18/42P08) needs its own confirmed diagnosis. Do not expose raw database errors in public HTTP responses.

No deployment or live repair was performed. Existing frontend navigation work and other uncommitted changes were preserved.

## Additional frontend findings

CareOperationsWorkspace discarded all successful results when any required feed rejected; errors from several map-related feeds were also swallowed. The local frontend now preserves independently successful feeds, clears failed feeds, names their failures and offers read-only refresh. OperationsMap handles webglcontextlost and offers explicit recreation with disposal/marker restoration. These changes improve failure isolation; they do not install missing backend routes or prove the monitoring 500 is repaired in production. No tests, lint, builds, migrations or deployments were executed.
