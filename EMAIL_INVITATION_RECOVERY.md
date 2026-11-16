# Staff invitation email recovery

Repository findings: appsettings defaults Email:Enabled to false. The Render blueprint previously had no email variables. This does not prove the live Render environment is missing them; live settings must be inspected.

## Render direct HTTPS delivery

Configure the backend service environment, storing the API key as a secret:

```text
Email__Enabled=true
EmailQueue__Enabled=false
Resend__ApiKey=<real sending key, stored only in Render>
Resend__FromAddress=<sender on a domain verified in Resend>
Resend__FromName=AiCare
Frontend__BaseUrl=https://azidotechnology.com
```

Direct delivery already exists; SQS/AWS credentials are not required in this mode. Do not switch an existing queued deployment to direct delivery until outstanding queued jobs and worker processing have been reviewed. No deployment configuration was changed remotely by this implementation.

For an intentionally queued deployment, retain EmailQueue__Enabled=true and configure its HTTPS queue URL, credentials/region, consumer and Resend settings. Do not enable SMS.

Deploy the backend configuration/code and frontend feedback changes. Missing delivery settings now fail before an inactive account is inserted. Configuration/provider failures return 503 with an actionable message rather than a generic validation 400. Delivery failures after account insertion leave the account inactive and mark the invitation failed. Provider acceptance is not inbox delivery.

## Existing failed recipient

Earlier attempts may have created an inactive account and failed invitation. Repeated Create requests then report an existing username/email. Do not delete the account, change its role, activate it manually, or blindly replay the request. Inspect its invitation status and audit history through an authorised operator before a governed resend/recovery. This change prevents configuration failures from stranding new accounts; it does not silently recreate existing accounts or implement a resend endpoint.

## Owner verification

Run the existing backend test/build and release commands, including AccountInvitationServiceTests and AccountInvitationEmailConfigurationTests, and frontend secureAuth/ActionFeedback tests. These were not executed here.

With one authorised test recipient, submit once after configuration and deployment. Confirm 201, provider acceptance/delivery status, inbox/spam, valid single-use activation, intended role/branch and successful sign-in. Capture the response message and trace ID on failure; never share bearer tokens, API keys or activation links. No email was sent by the implementation work.


## Deployment diagnostics

The administrator invitation form now has Check email setup, using authenticated GET /api/phase1/account-invitations/delivery-status. It reports Direct/Queue mode and configuration readiness only; it neither exposes secrets nor sends an email. Both backend and frontend must be deployed together. A successful configuration check does not validate key ownership, domain verification, provider acceptance or inbox delivery.

The duplicate-user error explicitly confirms no new email was sent and directs recovery of the existing inactive account. The endpoint does not silently resend or bypass activation. Check the same Resend account/team that owns the configured key; an empty log in a different team cannot establish whether that key was used.
