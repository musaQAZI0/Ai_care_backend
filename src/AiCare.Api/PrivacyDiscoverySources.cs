namespace AiCare.Api;

internal sealed record PrivacyDiscoverySource(string Type, string Sql);

internal static class PrivacyDiscoverySources
{
    internal static readonly PrivacyDiscoverySource[] All =
    [
        new("ServiceUser", "select \"Id\" from \"ServiceUsers\" where \"Id\"=@person and \"OrganizationId\"=@organization"),
        new("PersonRecord", "select \"Id\" from \"PersonRecords\" where \"ServiceUserId\"=@person and \"OrganizationId\"=@organization"),
        new("Assessment", "select \"Id\" from \"CareAssessments\" where \"ServiceUserId\"=@person and \"OrganizationId\"=@organization"),
        new("Risk", "select \"Id\" from \"RiskAssessments\" where \"ServiceUserId\"=@person and \"OrganizationId\"=@organization"),
        new("CarePlan", "select \"Id\" from \"CarePlans\" where \"ServiceUserId\"=@person and \"OrganizationId\"=@organization"),
        new("Visit", "select \"Id\" from \"Visits\" where \"ServiceUserId\"=@person and \"OrganizationId\"=@organization"),
        new("Medication", "select \"Id\" from \"Medications\" where \"ServiceUserId\"=@person and \"OrganizationId\"=@organization"),
        new("MedicationAdministration", "select mar.\"Id\" from \"MedicationAdministrationRecords\" mar join \"Medications\" m on m.\"Id\"=mar.\"MedicationId\" where m.\"ServiceUserId\"=@person and mar.\"OrganizationId\"=@organization"),
        new("CareNote", "select \"Id\" from \"CareNotes\" where \"ServiceUserId\"=@person and \"OrganizationId\"=@organization"),
        new("Observation", "select \"Id\" from \"HealthObservations\" where \"ServiceUserId\"=@person and \"OrganizationId\"=@organization"),
        new("Incident", "select \"Id\" from \"Incidents\" where \"ServiceUserId\"=@person and \"OrganizationId\"=@organization"),
        new("Document", "select \"Id\" from \"Documents\" where \"ServiceUserId\"=@person and \"OrganizationId\"=@organization"),
        new("Invoice", "select \"Id\" from \"Invoices\" where \"ServiceUserId\"=@person and \"OrganizationId\"=@organization"),
        new("SafeguardingCase", "select id from safeguarding_cases where service_user_id=@person and organization_id=@organization"),
        new("SafeguardingEvent", "select e.id from safeguarding_case_events e join safeguarding_cases c on c.id=e.case_id where c.service_user_id=@person and e.organization_id=@organization"),
        new("Conversation", "select id from conversations where service_user_id=@person and organization_id=@organization"),
        new("ConversationMessage", "select m.id from conversation_messages m join conversations c on c.id=m.conversation_id where c.service_user_id=@person and c.organization_id=@organization"),
        new("ConversationMessageEvent", "select e.id from conversation_message_events e join conversations c on c.id=e.conversation_id where c.service_user_id=@person and e.organization_id=@organization"),
        new("Complaint", "select id from family_feedback_cases where service_user_id=@person and organization_id=@organization"),
        new("ComplaintEvent", "select e.id from complaint_case_events e join family_feedback_cases c on c.id=e.case_id where c.service_user_id=@person and e.organization_id=@organization"),
        new("EmarLedger", "select id from emar_ledger where service_user_id=@person and organization_id=@organization"),
        new("MedicationStockTransaction", "select s.id from medication_stock_transactions s join \"Medications\" m on m.\"Id\"=s.medication_id where m.\"ServiceUserId\"=@person and s.organization_id=@organization"),
        new("EmarEscalation", "select e.id from emar_escalations e join \"MedicationAdministrationRecords\" mar on mar.\"Id\"=e.mar_record_id join \"Medications\" m on m.\"Id\"=mar.\"MedicationId\" where m.\"ServiceUserId\"=@person and e.organization_id=@organization"),
        new("FinanceInvoiceLine", "select id from finance_invoice_lines where service_user_id=@person and organization_id=@organization"),
        new("FinancePayment", "select p.id from finance_payments p join \"Invoices\" i on i.\"Id\"=p.invoice_id where i.\"ServiceUserId\"=@person and p.organization_id=@organization"),
        new("FundingReconciliation", "select id from finance_funding_reconciliations where service_user_id=@person and organization_id=@organization"),
        new("IntegrationWebhook", "select id from integration_webhook_events where organization_id=@organization and payload_json::text like @personPattern"),
        new("IntegrationJob", "select id from integration_jobs where organization_id=@organization and (options_json::text like @personPattern or coalesce(output_json,'{}'::jsonb)::text like @personPattern)"),
        new("IntegrationFailure", "select id from integration_sync_failures where organization_id=@organization and payload_json::text like @personPattern")
    ];
}
