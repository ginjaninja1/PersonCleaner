using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Common.Net; // IHttpClient, HttpRequestOptions - Probe 8
using MediaBrowser.Model.Entities; // ProviderIdsExtensions: HasProviderId/GetProviderId/SetProviderId
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PersonCleaner.Tasks
{
    /// <summary>
    /// One-shot diagnostics task that runs the 7 probes queued in the
    /// Person Cleaner design proposal (section 7), to turn open ILSpy
    /// questions into confirmed evidence from a live server.
    ///
    /// Everything this task creates is prefixed "[PersonCleaner-Probe]" so
    /// it is unambiguous in the UI, and every probe logs exactly what it
    /// created/touched at Info level so the run can be reviewed and, for
    /// Probes 1-4 and 7, cleaned up automatically at the end regardless of
    /// outcome.
    ///
    /// Probe 6 is the only probe that touches real library content, and
    /// even then only via a normal, non-destructive metadata refresh of
    /// one existing item (the same operation "Identify"/"Refresh metadata"
    /// performs in the UI) - it does not modify, delete, or relink
    /// anything. The item it chose is always logged clearly before it's
    /// touched.
    ///
    /// This task is diagnostics-only. It is not part of the eventual
    /// cleanup pipeline and should be disabled/removed once its questions
    /// are answered.
    /// </summary>
    public class PersonCleanerDiagnosticsTask : IScheduledTask
    {
        private const string ProbeMarker = "[PersonCleaner-Probe]";

        private readonly ILibraryManager libraryManager;
        private readonly ITaskManager taskManager;
        private readonly IFileSystem fileSystem;
        private readonly IHttpClient httpClient;
        private readonly ILogger logger;

        // Ids of everything this run creates, so we can clean up at the end
        // even if a later probe throws.
        private readonly List<long> createdInternalIds = new List<long>();

        public PersonCleanerDiagnosticsTask(
            ILibraryManager libraryManager,
            ITaskManager taskManager,
            IFileSystem fileSystem,
            IHttpClient httpClient,
            ILogManager logManager)
        {
            this.libraryManager = libraryManager;
            this.taskManager = taskManager;
            this.fileSystem = fileSystem;
            this.httpClient = httpClient;
            this.logger = logManager.GetLogger("PersonCleaner");
        }

        public string Name => "Person Cleaner - Run Diagnostic Probes";

        public string Key => "PersonCleanerDiagnosticsTask";

        public string Description =>
            "Runs the queued Emby-behaviour probes (person delete/relink lifecycle, scan-concurrency, " +
            "provider id mapping) against this server and logs the results. Creates only clearly-marked " +
            "throwaway test data, cleaned up at the end of the run.";

        public string Category => "GinjaNinja Tools";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            this.logger.Info("{0} ===== Diagnostics run starting =====", ProbeMarker);
            this.logger.Info(
                "{0} Everything this run creates is prefixed \"{0}\" and will be listed by name/id below. " +
                "Probe 6 is the only probe that touches real library content, and only via a normal metadata refresh.",
                ProbeMarker);

            try
            {
                await this.RunProbe("Probe 1+2 - Person delete/relink lifecycle", this.Probe1And2_PersonDeleteLifecycle, cancellationToken);
                progress.Report(20);

                await this.RunProbe("Probe 3 - Scan-concurrency write race", this.Probe3_ConcurrentWriteDuringScan, cancellationToken);
                progress.Report(35);

                await this.RunProbe("Probe 4 - Task manager state visibility", this.Probe4_TaskManagerStateVisibility, cancellationToken);
                progress.Report(50);

                await this.RunProbe("Probe 5 - TMDB-to-TVDB cross-link, via Emby's own refresh", this.Probe5_TmdbToTvdbCrossLink, cancellationToken);
                progress.Report(65);

                await this.RunProbe("Probe 6 - TVDB IMDb provider-id mapping", this.Probe6_TvdbImdbMapping, cancellationToken);
                progress.Report(80);

                await this.RunProbe("Probe 7 - Provider Name/Year discrepancy detectability", this.Probe7_NameYearDiscrepancy, cancellationToken);
                progress.Report(95);

                await this.RunProbe("Probe 8 - IHttpClient injectability and shared response cache", this.Probe8_HttpClientInjectability, cancellationToken);
                progress.Report(100);
            }
            finally
            {
                this.CleanUpCreatedItems();
                progress.Report(100);
                this.logger.Info("{0} ===== Diagnostics run finished =====", ProbeMarker);
            }
        }

        private async Task RunProbe(string label, Func<CancellationToken, Task> probe, CancellationToken cancellationToken)
        {
            this.logger.Info("{0} --- {1}: starting ---", ProbeMarker, label);
            try
            {
                await probe(cancellationToken);
                this.logger.Info("{0} --- {1}: completed ---", ProbeMarker, label);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A probe failing is itself a result worth having - log and
                // move on to the remaining probes rather than aborting the run.
                this.logger.ErrorException(ProbeMarker + " " + label + ": FAILED", ex);
            }
        }

        // ------------------------------------------------------------------
        // Probe 1+2: create a throwaway, unlinked Person; confirm it is
        // IsDeadPerson; confirm DeleteItems removes it cleanly via the
        // friendly API (no CanDelete guard hit); confirm nothing recreates
        // it. See design doc section 4.
        // ------------------------------------------------------------------
        private Task Probe1And2_PersonDeleteLifecycle(CancellationToken cancellationToken)
        {
            var probeName = ProbeMarker + " Test Person " + Guid.NewGuid().ToString("N").Substring(0, 8);

            var person = new Person
            {
                Name = probeName,
                Id = this.libraryManager.GetNewItemIdFromName(probeName, typeof(Person)),
                DateCreated = DateTime.UtcNow,
            };

            this.libraryManager.CreateItem(person, null);
            this.createdInternalIds.Add(person.InternalId);

            this.logger.Info("{0} Created throwaway Person \"{1}\" (InternalId={2}, Id={3}).",
                ProbeMarker, probeName, person.InternalId, person.Id);

            var isDeadQuery = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { typeof(Person).Name },
                ItemIds = new[] { person.InternalId },
                IsDeadPerson = true,
            };

            var deadIds = this.libraryManager.GetInternalItemIds(isDeadQuery, cancellationToken);
            var isDead = deadIds.Contains(person.InternalId);

            this.logger.Info(
                "{0} Newly-created, never-linked Person reports IsDeadPerson={1} (expected true, per evidence).",
                ProbeMarker, isDead);

            // Friendly bulk delete - bypasses the CanDelete()==false guard,
            // per the evidenced CleanDatabaseScheduledTask mechanism.
            this.libraryManager.DeleteItems(new[] { person.InternalId });
            this.createdInternalIds.Remove(person.InternalId);

            var stillPresent = this.libraryManager.GetItemById(person.InternalId);
            this.logger.Info(
                "{0} After DeleteItems: GetItemById returns {1} (expected null - row genuinely removed, not just unlinked).",
                ProbeMarker, stillPresent == null ? "null" : "NON-NULL: " + stillPresent.Name);

            var reQuery = this.libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { typeof(Person).Name },
                Name = probeName,
            }, cancellationToken);

            this.logger.Info(
                "{0} Re-querying by exact name after delete finds {1} row(s) (expected 0 - confirms nothing " +
                "recreated/resurrected it purely from the delete itself; re-check this server's logs after the " +
                "NEXT scheduled library scan too, to confirm no scan-driven recreation either).",
                ProbeMarker, reQuery.Length);

            return Task.CompletedTask;
        }

        // ------------------------------------------------------------------
        // Probe 3: trigger the REAL "Scan media library" task ourselves via
        // ITaskManager.Execute, run our own write loop against a throwaway
        // item for as long as that scan is running (capped, given this
        // library's size), then check:
        //   (a) the scan's own TaskResult - did it complete, fail, or abort?
        //   (b) our throwaway item's final state - is it exactly what our
        //       own last write set, with no unexpected value (a sign of
        //       silent interleaving/corruption rather than a clean
        //       last-write-wins)?
        // This is a genuine overlap, not a simulated one - no manual
        // dashboard action needed.
        // ------------------------------------------------------------------
        private async Task Probe3_ConcurrentWriteDuringScan(CancellationToken cancellationToken)
        {
            var scanWorker = this.taskManager.ScheduledTasks
                .FirstOrDefault(t => string.Equals(t.Name, "Scan Media Library", StringComparison.OrdinalIgnoreCase));

            if (scanWorker == null)
            {
                this.logger.Info(
                    "{0} Could not find the \"Scan Media Library\" task by name on this server - skipping " +
                    "the real-scan overlap test. (Task names are visible in Probe 4's output if this needs updating.)",
                    ProbeMarker);
                return;
            }

            if (scanWorker.State != TaskState.Idle)
            {
                this.logger.Info(
                    "{0} \"Scan Media Library\" is already {1} - reusing that run for the overlap test " +
                    "rather than queuing a second one.", ProbeMarker, scanWorker.State);
            }

            var probeName = ProbeMarker + " Test Series " + Guid.NewGuid().ToString("N").Substring(0, 8);
            var series = new Series
            {
                Name = probeName,
                Id = this.libraryManager.GetNewItemIdFromName(probeName, typeof(Series)),
                DateCreated = DateTime.UtcNow,
            };
            this.libraryManager.CreateItem(series, null);
            this.createdInternalIds.Add(series.InternalId);

            TaskResult scanResult = null;
            var scanCompleted = new TaskCompletionSource<bool>();
            EventHandler<TaskCompletionEventArgs> onCompleted = (sender, args) =>
            {
                if (args.Task == scanWorker)
                {
                    scanResult = args.Result;
                    scanCompleted.TrySetResult(true);
                }
            };
            this.taskManager.TaskCompleted += onCompleted;

            try
            {
                this.logger.Info(
                    "{0} Triggering a REAL library scan now (this server has ~700k items, so this may run " +
                    "for a while - our write-loop overlap test is capped at 2 minutes regardless, the scan " +
                    "itself will keep running in the background after that if it's not finished).",
                    ProbeMarker);

                if (scanWorker.State == TaskState.Idle)
                {
                    // Deliberately NOT awaited here - Execute's returned Task only
                    // completes when the scan itself finishes, so awaiting it would
                    // give us zero overlap with our own writes below.
                    var fireAndForgetScanTask = this.taskManager.Execute(scanWorker, new TaskOptions { HasManualInteraction = false });
                    fireAndForgetScanTask.ContinueWith(
                        t => this.logger.ErrorException(ProbeMarker + " Scan task faulted", t.Exception),
                        TaskContinuationOptions.OnlyOnFaulted);
                }

                using (var overlapCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    overlapCts.CancelAfter(TimeSpan.FromMinutes(2));

                    // Guard against the brief startup race where State hasn't
                    // flipped away from Idle yet, which would otherwise make
                    // the loop below exit immediately with zero real overlap.
                    var spinDeadline = DateTime.UtcNow.AddSeconds(5);
                    while (scanWorker.State == TaskState.Idle && DateTime.UtcNow < spinDeadline
                           && !overlapCts.IsCancellationRequested)
                    {
                        await Task.Delay(50, overlapCts.Token);
                    }

                    var writeCount = 0;

                    try
                    {
                        while (!overlapCts.IsCancellationRequested && scanWorker.State != TaskState.Idle)
                        {
                            writeCount++;
                            series.Overview = "Probe overlap write #" + writeCount;
                            this.libraryManager.UpdateItem(series, null, ItemUpdateType.MetadataEdit);
                            await Task.Delay(50, overlapCts.Token);
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // Our own 2-minute overlap cap, not the task being cancelled - expected, not an error.
                    }

                    this.logger.Info(
                        "{0} Performed {1} write(s) against our throwaway item while the scan was {2}. " +
                        "None threw.",
                        ProbeMarker, writeCount,
                        scanWorker.State == TaskState.Idle ? "running (now finished)" : "still running (2-minute cap reached)");
                }

                if (scanResult != null)
                {
                    this.logger.Info(
                        "{0} Scan's own TaskResult: Status={1}{2}",
                        ProbeMarker, scanResult.Status,
                        scanResult.Status == TaskCompletionStatus.Completed
                            ? string.Empty
                            : " - ErrorMessage=\"" + scanResult.ErrorMessage + "\"");
                }
                else
                {
                    this.logger.Info(
                        "{0} Scan had not completed within the 2-minute cap - re-run this probe later, or " +
                        "check the scan's own TaskResult from the dashboard once it finishes.", ProbeMarker);
                }

                var finalState = this.libraryManager.GetItemById(series.InternalId) as Series;
                this.logger.Info(
                    "{0} Throwaway item's final Overview after the overlap: \"{1}\". This should exactly match " +
                    "the last write we issued above - if it doesn't, that's a concrete sign of interleaving " +
                    "corruption between our writes and the scan, not just theoretical.",
                    ProbeMarker, finalState?.Overview);
            }
            finally
            {
                this.taskManager.TaskCompleted -= onCompleted;
            }
        }

        // ------------------------------------------------------------------
        // Probe 4: snapshot every registered scheduled task's State, so we
        // can confirm ITaskManager.ScheduledTasks[].State is a reliable,
        // readable signal for "is a scan/cleanup running" from plugin code.
        // Passive/read-only - touches nothing.
        // ------------------------------------------------------------------
        private Task Probe4_TaskManagerStateVisibility(CancellationToken cancellationToken)
        {
            var tasks = this.taskManager.ScheduledTasks;
            this.logger.Info("{0} {1} registered scheduled task(s) visible via ITaskManager:", ProbeMarker, tasks.Length);

            foreach (var task in tasks)
            {
                this.logger.Info("{0}   - \"{1}\" (Id={2}): State={3}", ProbeMarker, task.Name, task.Id, task.State);
            }

            var scanLike = tasks.Where(t =>
                t.Name.IndexOf("scan", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.Name.IndexOf("clean", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.Name.IndexOf("valida", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            this.logger.Info(
                "{0} Scan/clean/validate-like tasks identified by name (confirm these match the real task " +
                "names on this server so our future concurrency guard checks the right Ids): {1}",
                ProbeMarker, string.Join(", ", scanLike.Select(t => t.Name + " [" + t.Id + "]")));

            return Task.CompletedTask;
        }

        // ------------------------------------------------------------------
        // Probe 5: create a throwaway Person pinned to a well-known, real
        // TMDB person id, run a normal Emby-driven metadata refresh (this
        // invokes Emby's own registered MovieDbPersonProvider, using Emby's
        // own built-in TMDB API key - no key/config from us needed), and
        // inspect the resulting ProviderIds for a Tvdb entry. This answers
        // the operationally relevant question directly: does Emby's own
        // TMDB integration, as our plugin will actually be able to use it,
        // ever produce a Tvdb id - rather than poking at TMDB's raw JSON
        // ourselves, which wouldn't tell us anything about what our plugin
        // can actually get through the public API surface.
        // ------------------------------------------------------------------
        private async Task Probe5_TmdbToTvdbCrossLink(CancellationToken cancellationToken)
        {
            // TMDB person id 287 = Brad Pitt. Arbitrary well-known public
            // figure chosen only because their TMDB id is stable and public;
            // not related to any content on this server.
            const string wellKnownTmdbPersonId = "287";
            var probeName = ProbeMarker + " Test Person (TMDB xlink) " + Guid.NewGuid().ToString("N").Substring(0, 8);

            var person = new Person
            {
                Name = probeName,
                Id = this.libraryManager.GetNewItemIdFromName(probeName, typeof(Person)),
                DateCreated = DateTime.UtcNow,
            };
            person.SetProviderId(MetadataProviders.Tmdb, wellKnownTmdbPersonId);

            this.libraryManager.CreateItem(person, null);
            this.createdInternalIds.Add(person.InternalId);

            this.logger.Info(
                "{0} Created throwaway Person \"{1}\" (InternalId={2}) pinned to real TMDB person id {3}, " +
                "then running a normal metadata refresh through Emby's own registered TMDB provider.",
                ProbeMarker, probeName, person.InternalId, wellKnownTmdbPersonId);

            var directoryService = new DirectoryService(this.fileSystem);
            var refreshOptions = new MetadataRefreshOptions(directoryService)
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = true,
                ForceSave = true,
            };

            await person.RefreshMetadata(refreshOptions, cancellationToken);

            var refreshed = this.libraryManager.GetItemById(person.InternalId);
            var tvdbId = refreshed?.GetProviderId(MetadataProviders.Tvdb);
            var imdbId = refreshed?.GetProviderId(MetadataProviders.Imdb);

            this.logger.Info(
                "{0} After Emby-driven TMDB refresh: Tvdb id = {1}, Imdb id = {2}. All provider-id keys: {3}. " +
                "If Tvdb is missing here, it confirms Emby's public refresh pathway never surfaces TMDB's " +
                "cross-link field for us (per the decompile finding that MovieDbPersonProvider only maps " +
                "imdb_id) - meaning our real plugin will need its own direct TMDB call for that field, " +
                "with its own personal API key, when we get to building it for real (not needed for this " +
                "diagnostics run).",
                ProbeMarker, tvdbId ?? "(none)", imdbId ?? "(none)",
                string.Join(", ", refreshed?.ProviderIds.Select(kv => kv.Key + "=" + kv.Value) ?? Array.Empty<string>()));
        }

        // ------------------------------------------------------------------
        // Probe 6: find one REAL existing Movie or Series in the library
        // that already has both a Tmdb and a Tvdb id, log exactly which one
        // it picked, run a normal (non-destructive) metadata refresh, and
        // inspect the resulting ProviderIds dictionary keys directly to
        // check whether an "Imdb" key is present, or whether it landed
        // under an unmapped raw TVDB source-name key.
        // ------------------------------------------------------------------
        private async Task Probe6_TvdbImdbMapping(CancellationToken cancellationToken)
        {
            var candidates = this.libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { typeof(Movie).Name, typeof(Series).Name },
                Recursive = true,
                DtoOptions = new DtoOptions(true),
            }, cancellationToken)
            .Where(i => i.HasProviderId(MetadataProviders.Tmdb) && i.HasProviderId(MetadataProviders.Tvdb))
            .ToList();

            if (candidates.Count == 0)
            {
                this.logger.Info(
                    "{0} No existing library item has both a Tmdb and a Tvdb id set - cannot run Probe 6 " +
                    "against real data. Skipping.", ProbeMarker);
                return;
            }

            var target = candidates.First();

            this.logger.Info(
                "{0} Probe 6 will run a normal metadata refresh (non-destructive - same as \"Refresh metadata\" " +
                "in the UI) on the EXISTING item: \"{1}\" ({2}, InternalId={3}, TmdbId={4}, TvdbId={5}). " +
                "No relinking, deletion, or cast changes are made to this item.",
                ProbeMarker, target.Name, target.GetType().Name, target.InternalId,
                target.GetProviderId(MetadataProviders.Tmdb), target.GetProviderId(MetadataProviders.Tvdb));

            var beforeImdb = target.GetProviderId(MetadataProviders.Imdb);
            this.logger.Info("{0} Before refresh, Imdb provider id = {1}", ProbeMarker, beforeImdb ?? "(none)");

            var directoryService = new DirectoryService(this.fileSystem);
            var refreshOptions = new MetadataRefreshOptions(directoryService)
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                ReplaceAllMetadata = false,
                ForceSave = false,
            };

            await target.RefreshMetadata(refreshOptions, cancellationToken);

            var refreshed = this.libraryManager.GetItemById(target.InternalId);
            var afterImdb = refreshed?.GetProviderId(MetadataProviders.Imdb);

            this.logger.Info(
                "{0} After refresh, Imdb provider id = {1}. All provider-id keys currently on this item: {2}",
                ProbeMarker, afterImdb ?? "(none)",
                string.Join(", ", refreshed?.ProviderIds.Select(kv => kv.Key + "=" + kv.Value) ?? Array.Empty<string>()));

            this.logger.Info(
                "{0} If the canonical key \"Imdb\" is missing above despite this item genuinely having an " +
                "IMDb page, look for an unfamiliar/raw key in the same list (e.g. matching TheTVDB's literal " +
                "source name) - that would confirm the unmapped-source-name gap hypothesized from the decompile.",
                ProbeMarker);
        }

        // ------------------------------------------------------------------
        // Probe 7: create a throwaway Series with a deliberately WRONG
        // local Name/Year against a real, well-known Tvdb id, force a
        // provider-priority-order refresh, and confirm our proposed
        // Name/Year discrepancy check would actually have caught it.
        //
        // Methodology note (learned from the first run of this probe): a
        // synthetic Series with no parent resolves to zero enabled remote
        // providers, because Movie/Series/Episode provider selection is
        // library-scoped (unlike Person, which is global). Rather than
        // parenting this throwaway item under a real library folder (which
        // would make it show up alongside real content), we borrow an
        // existing real TV library's LibraryOptions directly via the
        // public ILibraryManager.GetLibraryOptions/BaseItem.LibraryOptions
        // surface - this gives the refresh pipeline a real, correctly
        // type-scoped provider list without the item ever being parented
        // under, or visible inside, a real library.
        // ------------------------------------------------------------------
        private async Task Probe7_NameYearDiscrepancy(CancellationToken cancellationToken)
        {
            var realSeriesWithLibrary = this.libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { typeof(Series).Name },
                Recursive = true,
                DtoOptions = new DtoOptions(true),
            }, cancellationToken).FirstOrDefault();

            if (realSeriesWithLibrary == null)
            {
                this.logger.Info(
                    "{0} No existing Series found on this server to borrow library options from - " +
                    "cannot resolve a real TV-type provider list for this probe. Skipping.", ProbeMarker);
                return;
            }

            var borrowedLibraryOptions = this.libraryManager.GetLibraryOptions(realSeriesWithLibrary);

            this.logger.Info(
                "{0} Borrowing LibraryOptions from real Series \"{1}\"'s library (not parenting our test " +
                "item under it - just reusing its configured provider list) so remote providers actually run.",
                ProbeMarker, realSeriesWithLibrary.Name);

            // Well-known, stable Tvdb series id: 121361 = "Game of Thrones".
            // Deliberately mismatched local Name/Year so the eventual merge
            // result reveals what actually got written.
            const string wellKnownTvdbSeriesId = "121361";
            var probeName = ProbeMarker + " Deliberately Wrong Series " + Guid.NewGuid().ToString("N").Substring(0, 8);

            var series = new Series
            {
                Name = probeName,
                ProductionYear = 1999,
                Id = this.libraryManager.GetNewItemIdFromName(probeName, typeof(Series)),
                DateCreated = DateTime.UtcNow,
                LibraryOptions = borrowedLibraryOptions,
            };
            series.SetProviderId(MetadataProviders.Tvdb, wellKnownTvdbSeriesId);

            this.libraryManager.CreateItem(series, null);
            this.createdInternalIds.Add(series.InternalId);

            this.logger.Info(
                "{0} Created throwaway Series \"{1}\" (InternalId={2}) with Tvdb id {3} pinned but a " +
                "deliberately wrong local Name/Year, then forcing a refresh.",
                ProbeMarker, probeName, series.InternalId, wellKnownTvdbSeriesId);

            var directoryService = new DirectoryService(this.fileSystem);
            var refreshOptions = new MetadataRefreshOptions(directoryService)
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = true,
                ForceSave = true,
            };

            await series.RefreshMetadata(refreshOptions, cancellationToken);

            var refreshed = this.libraryManager.GetItemById(series.InternalId) as Series;

            this.logger.Info(
                "{0} After refresh: Name=\"{1}\", Year={2}, TmdbId={3}, TvdbId={4}. " +
                "Expected: Name/Year overwritten to the real show's values (\"Game of Thrones\", 2011) by " +
                "whichever provider is first in this library's configured priority order, confirming that " +
                "an independent per-provider Name/Year lookup (rather than trusting what merged onto the " +
                "item) is necessary for our discrepancy check to mean anything.",
                ProbeMarker, refreshed?.Name, refreshed?.ProductionYear,
                refreshed?.GetProviderId(MetadataProviders.Tmdb), refreshed?.GetProviderId(MetadataProviders.Tvdb));
        }

        // ------------------------------------------------------------------
        // Probe 8: answers §12's flagged question directly rather than
        // leaving it as a guess. Two things to establish:
        //   (a) Is IHttpClient genuinely injectable into plugin code via
        //       the same constructor-DI pattern Emby's own MovieDb/Tvdb
        //       providers use? If this class fails to construct at all,
        //       that itself is the answer (no).
        //   (b) Does calling it directly from plugin code get the same
        //       URL-keyed response caching we've already observed Emby's
        //       own providers get (confirmed in Probes 5-7's logs)? Two
        //       identical calls, a few seconds apart, and we read Emby's
        //       own "HttpClient" log lines (already proven to self-report
        //       "served from cache" - no guessing at HttpResponseInfo's
        //       members required, since Emby logs this for us).
        //
        // Deliberately does NOT establish per-provider throttling, because
        // decompile evidence (MovieDbProviderBase.GetMovieDbResponse) shows
        // that throttle is implemented locally inside Emby's own provider
        // classes, not inside IHttpClient itself - so it is not something
        // we inherit for free just by reusing IHttpClient. Our own
        // reconciliation code will need its own simple last-request-time
        // throttle for TMDB/TVDB calls, same pattern Emby uses internally.
        // ------------------------------------------------------------------
        private async Task Probe8_HttpClientInjectability(CancellationToken cancellationToken)
        {
            this.logger.Info(
                "{0} IHttpClient was successfully constructor-injected (this task wouldn't have started " +
                "otherwise) - confirms plugin code can receive the same DI'd IHttpClient Emby's own " +
                "MovieDb/Tvdb providers use.", ProbeMarker);

            // A harmless, no-key-required public endpoint, purely to test
            // injection + caching mechanics - nothing to do with TMDB/TVDB
            // or library data.
            const string probeUrl = "https://api.github.com/zen";

            this.logger.Info(
                "{0} Calling {1} via the injected IHttpClient (first call) - check the surrounding " +
                "\"HttpClient\" log lines for the GET/response, same as Emby's own providers produce.",
                ProbeMarker, probeUrl);

            await this.httpClient.GetResponse(new HttpRequestOptions
            {
                Url = probeUrl,
                CancellationToken = cancellationToken,
                BufferContent = false,
                UserAgent = "PersonCleaner-Diagnostics/1.0",
            });

            this.logger.Info(
                "{0} Calling the exact same URL again immediately - if IHttpClient's response cache " +
                "applies to plugin-issued calls the same way it did for Emby's own provider calls in " +
                "earlier probes, the log line for this one should read \"served from cache\" instead of " +
                "a fresh GET/response pair.", ProbeMarker);

            await this.httpClient.GetResponse(new HttpRequestOptions
            {
                Url = probeUrl,
                CancellationToken = cancellationToken,
                BufferContent = false,
                UserAgent = "PersonCleaner-Diagnostics/1.0",
            });

            this.logger.Info(
                "{0} Probe 8 complete - read the two \"HttpClient\" log lines immediately above this one " +
                "to see whether the second call was cache-served. Reminder: this only tells us about " +
                "response caching, not rate-limit throttling - that is confirmed (via decompile) to be " +
                "implemented locally inside Emby's own MovieDb/Tvdb provider classes, not inside " +
                "IHttpClient, so our own code will still need its own throttle for TMDB/TVDB calls.",
                ProbeMarker);
        }

        private void CleanUpCreatedItems()
        {
            if (this.createdInternalIds.Count == 0)
            {
                return;
            }

            this.logger.Info(
                "{0} Cleaning up {1} throwaway item(s) created during this run: internal ids [{2}]",
                ProbeMarker, this.createdInternalIds.Count, string.Join(", ", this.createdInternalIds));

            try
            {
                this.libraryManager.DeleteItems(this.createdInternalIds.ToArray());
                this.logger.Info("{0} Cleanup delete call completed.", ProbeMarker);
            }
            catch (Exception ex)
            {
                this.logger.ErrorException(
                    ProbeMarker + " Cleanup failed - you may need to manually remove the items listed above.",
                    ex);
            }
        }
    }
}