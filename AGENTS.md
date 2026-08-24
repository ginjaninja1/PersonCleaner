# PersonCleaner development notes

## Latest live data run

When investigating a PersonCleaner result, inspect the live V2 SQLite database first. Do not begin with the Emby UI or search the filesystem for the database.

- Database: `%APPDATA%\Emby-Server\programdata\data\personcleaner-v2\entity-resolution.db`
- Current machine expansion: `C:\Users\Nicholas Bird\AppData\Roaming\Emby-Server\programdata\data\personcleaner-v2\entity-resolution.db`
- Identify the latest run with `SELECT * FROM resolution_run ORDER BY run_id DESC LIMIT 1;`.
- Open it read-only with `sqlite3 -readonly` because this is live Emby data.
- The data directory is outside the repository sandbox, so read-only access may require approval/escalation.

For a reported person, query the latest run's `resolution_decision` and `resolution_evidence`, then trace its provider keys through `resolution_pair`, `resolution_pair_feature`, `provider_person`, `person_external_id`, `person_alias`, and provider credits as needed.
