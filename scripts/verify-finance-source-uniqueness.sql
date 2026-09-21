-- Read-only: run against the legacy database before migration.
SELECT 'invoice' AS source, organization_id, visit_id, count(*) AS line_count,
       array_agg(id ORDER BY id) AS line_ids,
       array_agg(DISTINCT invoice_id) AS batch_ids
FROM finance_invoice_lines
WHERE visit_id IS NOT NULL
GROUP BY organization_id, visit_id HAVING count(*) > 1;

SELECT 'payroll' AS source, organization_id, visit_id, count(*) AS line_count,
       array_agg(id ORDER BY id) AS line_ids,
       array_agg(DISTINCT payroll_run_id) AS batch_ids
FROM finance_payroll_lines
WHERE visit_id IS NOT NULL
GROUP BY organization_id, visit_id HAVING count(*) > 1;

-- After migration: both indexes should exist and be unique and valid.
SELECT c.relname AS index_name, i.indisunique, i.indisvalid,
       pg_get_indexdef(i.indexrelid) AS definition
FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
WHERE c.relname IN ('ux_finance_invoice_visit', 'ux_finance_payroll_visit');

SELECT "MigrationId" FROM "__EFMigrationsHistory"
WHERE "MigrationId" = '20260918100000_AddFinanceSourceUniqueness';
