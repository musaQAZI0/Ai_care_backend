# Missing worker branch repair

Deployment required: apply migration 20261122090000_AddAdminBranchChangeEvents using the normal release process, then deploy the backend and frontend together. No migrations, tests or builds were executed by the agent.

After deployment, administrator opens Administration > Review access for musaqazi43. Assign missing worker branch appears only for CareWorker accounts without a branch. Select the branch matching the linked worker profile, enter a reason of 10?500 characters and the administrator MFA code, then select Assign branch and end sessions. Refresh and verify the branch before asking the worker to sign in through the staff portal.

The repair cannot change an existing branch, role, activation status, password, or worker link. It rejects another organisation, destinations outside the acting administrator's branch scope, missing/mismatched worker profiles, and self changes. A conditional update prevents repeated/concurrent repair; assignment, audit history and session revocation commit together. History is immutable. Existing unrelated backend changes must not be included in a scoped release.

Manual UAT: correct assignment; invalid/cross-organisation branch rejection; mismatched profile rejection; missing MFA rejection; non-admin rejection; repeated request conflict; account/profile/compliance unchanged; audit reason saved; prior sessions invalidated; fresh worker login succeeds. Regression source added but not executed.
