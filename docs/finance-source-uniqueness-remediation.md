# Finance source uniqueness and regression databases

The indexes protect source claims for `(organization_id, visit_id)` in
`finance_invoice_lines` and `finance_payroll_lines`. Invoice and payroll-run
IDs are intentionally not part of the key: a visit must not be billed or paid
again in a later batch.

The old regression factory reused a persistent `aicare_regression` database.
Clinical seeding runs after startup migrations and does not insert finance
lines. Finance regression runs generated new completed visits in the same fixed
period, while the earlier batch generator could select retained visits from
previous runs. This created a route to duplicate claims without parallel tests.

Each `PostgresRegressionFactory` now creates a unique
`aicare_regression_<guid>` database. The environment connection supplies the
server and credentials, not the database name. EF creates and migrates the
isolated database. The role needs `CREATEDB` permission on a development/test
server. xUnit disposes the host and drops only that fixture's generated database.
A killed process can leave its generated database behind.

## Existing database migration

No existing database is reset and no financial line is deleted. The migration
preserves every legacy line and its visit reference. For each existing
organization/visit group it deterministically marks the earliest line as the
protected source-claim anchor. Additional legacy lines remain in place with
`is_source_claim_anchor = false` so billing staff can reconcile invoice totals,
payments and payroll exports without losing history.

New lines default to `is_source_claim_anchor = true`. Partial unique indexes
protect those anchors, so concurrent or repeated generation cannot create
another protected claim for the same organization and visit. Application
eligibility checks continue to consider every historical line, including lines
awaiting reconciliation.

Run `scripts/verify-finance-source-uniqueness.sql` before and after migration.
After deployment, review every non-anchor line and record required business
corrections through the governed credit/refund workflow. Do not delete a
duplicate or clear its `visit_id` merely to hide it.

The complaint assignment workflow also accepts the existing `Submitted`
initial status. Finance regression coverage repeats invoice and payroll batch
requests and expects conflicts.
