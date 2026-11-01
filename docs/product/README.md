# AiCare Backend — Product Documentation

**Product requirements have one active home:** `musaQAZI0/Ai_care_frontend/docs/product/` on the corresponding documentation-consolidation branch / after merge, on `main`.

- Use `MASTER_V1_V2_MODULES_NON_AI.md` for V1/V2 functionality. AI excluded.
- Use `LOGIN_SIGNUP_ALL_ROLES_V1_V2.md` for account creation and role separation.
- Use `EMAR_INDUSTRY_PARITY.md`, `FINANCE_INVOICE_SETTINGS.md` and `REPORTS_FUNCTIONAL_REQUIREMENTS.md` for feature-specific acceptance.
- The frontend `docs/V1_SCOPE_FREEZE.md` remains authoritative for V1 scope.

Technical backend audits and operational/API documents are intentionally retained in this backend repository. This consolidation retires two *product-level* legacy roadmaps from the backend tree but does not change source code.

**Safety:** any live eMAR medication use needs its separate clinical safety approval and provider authorization. `BackOffice` must not double as `SuperAdmin`. Avoid duplicated sources of truth.
