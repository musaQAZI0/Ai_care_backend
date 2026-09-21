# Finance source uniqueness and regression databases

The indexes protect (organization_id, visit_id) on finance_invoice_lines and
finance_payroll_lines for non-null visits. Invoice/run IDs are not part of the key:
a visit must not be billed or paid twice across batches.

The old regression factory reused a persistent aicare_regression database.
Clinical seeding runs after startup migrations and does not insert finance lines.
Finance regression runs generate new completed visits in the same fixed period;
the earlier batch generator also selected retained visits from previous runs.
This provides a route to duplicate claims without parallel tests. The PostgreSQL
collection disables parallelization, but that never cleared historical data.
The reported 23505 proves duplicate invoice keys exist; query the supplied SQL
to identify the actual legacy records before attributing individual rows.

Each PostgresRegressionFactory now has a unique aicare_regression_<guid> database.
The environment connection supplies server and credentials, not the database name.
EF creates and migrates the isolated database. The role needs CREATEDB permission
on a development/test server. xUnit disposes the host and drops only that fixture's
generated database. A killed process can leave its generated database behind.
Tests within a class still share a fixture and must create their own scenario data.

No production or existing regression database is reset or edited. Existing
production duplicate claims require a reviewed financial reconciliation (including
invoice totals, payments, and payroll exports) before deployment. Do not delete an
arbitrary duplicate or null its visit_id to bypass the index. The migration now
checks both tables first and gives an actionable error without changing data.
Run scripts/verify-finance-source-uniqueness.sql before and after migration.

The complaint assignment also accepts the existing Submitted initial status.
Finance regression coverage now repeats both batch requests and expects conflicts.
