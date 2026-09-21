-- Read-only: run these two queries against the legacy database before migration.
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

-- After migration: retained legacy duplicates remain linked to their original
-- visits and financial batches, but are marked for explicit reconciliation.
SELECT 'invoice' AS source, id AS line_id, organization_id, visit_id, invoice_id AS batch_id,
       amount, created_at
FROM finance_invoice_lines
WHERE visit_id IS NOT NULL AND NOT is_source_claim_anchor
ORDER BY organization_id, visit_id, created_at, id;

SELECT 'payroll' AS source, id AS line_id, organization_id, visit_id, payroll_run_id AS batch_id,
       gross_pay AS amount, created_at
FROM finance_payroll_lines
WHERE visit_id IS NOT NULL AND NOT is_source_claim_anchor
ORDER BY organization_id, visit_id, created_at, id;

-- Both indexes should exist and be unique and valid.
SELECT c.relname AS index_name, i.indisunique, i.indisvalid,
       pg_get_indexdef(i.indexrelid) AS definition
FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
WHERE c.relname IN ('ux_finance_invoice_visit', 'ux_finance_payroll_visit');

-- No protected claim may be duplicated.
SELECT 'invoice' AS source, organization_id, visit_id, count(*) AS protected_claims
FROM finance_invoice_lines
WHERE visit_id IS NOT NULL AND is_source_claim_anchor
GROUP BY organization_id, visit_id HAVING count(*) > 1;

SELECT 'payroll' AS source, organization_id, visit_id, count(*) AS protected_claims
FROM finance_payroll_lines
WHERE visit_id IS NOT NULL AND is_source_claim_anchor
GROUP BY organization_id, visit_id HAVING count(*) > 1;

SELECT "MigrationId" FROM "__EFMigrationsHistory"
WHERE "MigrationId" = '20260918100000_AddFinanceSourceUniqueness';
