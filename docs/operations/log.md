# Operations log

Releases, incidents, restore tests and configuration changes, newest first. Date (DD/MM/YYYY), what, result. No secret values.

| Date | Type | What | Result |
|---|---|---|---|
| 03/11/2026 | Release | `vlms-2026.11.03` (`2fbb6d6`): web and jobs (privacy notice `7c95170`, per-chapter Inactive staff `a082e99`, gate restoration `12cf7e6`). No migrations (21 applied, latest `20261027164035_ClearDeletedAccountReferences`). Pre-release `vlms-backup.service` run succeeded. Rollback copies `/opt/vlms/web.previous.20261103T205742Z`, `/opt/vlms/jobs.previous.20261103T205742Z`. `/opt/vlms/web` was world-writable (`drwx---rwx`, from the 30/10 upload); now `drwx------ vlms:vlms`. | Success: both report `1.0.0+2fbb6d6…`, `/health` Healthy, timers active, no failed units. Public register notice matches `7c95170`. Signed-in checks (privacy page, application form, Inactive/reactivation) not yet done. |
