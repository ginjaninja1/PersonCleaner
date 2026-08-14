# TVDB Archive for Emby 4.10

This Emby scheduled-task plugin archives TVDB v4 identity, cast and filmography data into
`tvdb-archive.db` under Emby's data directory. It uses the SQLite assemblies already shipped by
Emby 4.10; no SQLite DLL is deployed alongside the plugin.

## Safe first run

1. Install `PersonCleaner.dll`, restart Emby, and open the **TVDB Archive** plugin settings.
2. Enter a TVDB v4 API key (and subscriber PIN only if the key requires one). The supplied UUID-style
   credential is consistent with a TVDB key, not a TMDB bearer token; the key is deliberately not in source.
3. Run **TVDB Archive - Probe IMDb/TVDB mappings**. Results are written to `id_probe`.
4. Run **TVDB Archive - Preview first items**. By default this processes five Emby rows of each type.
5. Inspect the SQLite views/tables and Emby log, then run **TVDB Archive - Full resumable export**.

Stopping a task is safe. Each response is committed independently and `run_state` records status,
counts and the last Emby id. Successful fetches are cached for 30 days by default; failures and
timeouts are retried after 30 minutes. HTTP 429 and server failures also use exponential backoff.

## Useful SQL

```sql
-- Find a show by Emby id, TVDB id, IMDb id, or name
SELECT * FROM searchable_media
WHERE emby_id = 123 OR tvdb_id = '121361' OR imdb_id = 'tt0944947'
   OR emby_name LIKE '%Game of Thrones%';

-- A person's archived filmography
SELECT c.*, e.name AS production_name
FROM credit c LEFT JOIN tvdb_entity e
  ON e.tvdb_id=c.subject_tvdb_id AND e.entity_type=c.subject_type
WHERE c.person_tvdb_id='12345';

-- Latest task checkpoint
SELECT * FROM run_state ORDER BY updated_utc DESC;
```

The main tables are `emby_item`, `tvdb_entity`, `remote_id`, `credit`, `fetch_cache`, `run_state`,
`id_probe`, `item_resolution`, and `api_response_cache`. `item_resolution.provenance` distinguishes
`direct`, `inferred`, `rejected`, and `unresolved` results. Use `resolution_inventory` for counts and
`direct-unavailable` identifies an Emby TVDB ID whose TVDB entity endpoint returned 404. Use
`identity_review_queue` for human review and `resolved_searchable_media` for identity-aware searches.
Raw successful entity JSON is retained in `tvdb_entity.raw_json`; overviews are not
modeled into searchable columns.

---

# Original template notes

A standardized, automation-ready repository template for rapidly scaffolding Emby Server plugins. 

## Features
* **Zero-Configuration Scaffolding**: Uses automated GitHub Actions to rename namespaces, solution files, and projects instantly upon repository creation.
* **.gitignore prepopulated**: To ensure obj, bin and .vs folders are excluded from repository
* **setup.bat**: instantiates a pre-commit in `.git/hooks/` so any commit with "[bump]" at the END of description increases the the version number in .csproj.
* **Working Plugin with thumbnail**: Ready to compile plugin with thumbnail, pluginui configuration page with autopostback (autosave) and task.
* **launchSettings.json**: Ready to launch Emby with breakpoints for debugging
* **Post Build Event**: Ready to copy compiled code to Emby plugins folder in current users %appdata%.
* **Supress dependency file**: No value in copying this into plugins folder.

## How to Instantiate a New Plugin

This template is completely automated via the cloud. You do not need to use `dotnet new` or run local renaming commands.

1. Click the green **Use this template** button at the top of this GitHub page.
2. Select **Create a new repository**.
3. Name your repository using your new plugin's name (e.g., `MyNewPlugin`).
4. Click **Create repository**.

### What happens in the background:
GitHub will instantly spin up a cloud action, read your repository name, and automatically update your folder paths, `.csproj`/`.slnx` filenames, and C# namespaces to match perfectly.

5. Open **GitHub Desktop** and clone your brand new repository down to your computer.
6. Run \repositoryroot\setup.bat to instantiate the [bump] pre-commit hook.
7. Launch the solution file inside `src/` and start coding immediately!
