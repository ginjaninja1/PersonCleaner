using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PersonCleaner.V2.Tasks
{
    /// <summary>
    /// Manual-only live contract probe for the Emby mutation APIs used by PersonCleaner.
    /// It operates exclusively on entities carrying ProbeMarker in their names.
    /// </summary>
    public sealed class EmbyMutationProbeTask : IScheduledTask
    {
        private const string ProbeMarker = "[PersonCleaner-V2-MutationProbe]";
        private const string OrphanMarker = ProbeMarker + " Orphan sentinel ";
        private const string ResolverTokenProvider = "PersonCleanerMergeToken";
        private readonly ILibraryManager library;
        private readonly ILogger logger;

        public EmbyMutationProbeTask(ILibraryManager library, ILogManager logs)
        {
            this.library = library;
            logger = logs.GetLogger("PersonCleaner v2 mutation probe");
        }

        public string Name => "PersonCleaner - Probe Emby person mutations";
        public string Key => "PersonCleanerEmbyMutationProbeV2";
        public string Description => "Manual-only: creates isolated probe entities and verifies person/credit read, replace, deduplicate, provider-ID, and unreferenced-person lifecycle semantics.";
        public string Category => "GinjaNinja Tools";
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var created = new List<long>();
            logger.Info("{0} ===== live Emby mutation contract probe starting =====", ProbeMarker);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObserveAndRemovePriorSentinels(cancellationToken);
                progress.Report(5);

                var suffix = Guid.NewGuid().ToString("N").Substring(0, 10);
                var source = CreatePerson(ProbeMarker + " Source " + suffix, created);
                var target = CreatePerson(ProbeMarker + " Target " + suffix, created);
                var media = CreateSeries(ProbeMarker + " Media " + suffix, created);
                progress.Report(15);

                ProbeProviderIds(source);
                progress.Report(30);

                ProbePeopleReadContracts(media, source, target);
                progress.Report(50);

                ProbeCreditMoveAndPreservation(media, source, target);
                progress.Report(70);

                ProbeDuplicateCollapse(media, source, target);
                progress.Report(80);

                ProbeSharedProviderIdRelease(media, source, target);
                progress.Report(88);

                var sentinelId = ProbeImplicitPersonCreationAndOrphanLifecycle(media, suffix, cancellationToken);
                created.Remove(sentinelId); // Deliberately leave it for Emby's own dead-person cleanup.
                progress.Report(95);

                logger.Info("{0} PASS: all immediate mutation contracts behaved as expected.", ProbeMarker);
                logger.Info("{0} Orphan sentinel InternalId={1} was deliberately left unreferenced. Run Emby's normal database/library cleanup, then run this probe again; the next run reports whether Emby removed it automatically.", ProbeMarker, sentinelId);
            }
            finally
            {
                RemoveCreated(created);
                progress.Report(100);
                logger.Info("{0} ===== live Emby mutation contract probe finished =====", ProbeMarker);
            }
            return Task.CompletedTask;
        }

        private void ObserveAndRemovePriorSentinels(CancellationToken cancellationToken)
        {
            var prior = library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { typeof(Person).Name },
                Recursive = true
            }, cancellationToken).OfType<Person>().Where(x => (x.Name ?? string.Empty).StartsWith(OrphanMarker, StringComparison.Ordinal)).ToList();

            if (prior.Count == 0)
                logger.Info("{0} No prior orphan sentinel remains. If a previous probe created one, Emby's cleanup removed it.", ProbeMarker);
            else
                logger.Info("{0} {1} prior orphan sentinel(s) still exist: {2}. Automatic cleanup has not removed them yet; removing these probe-only rows explicitly now.", ProbeMarker, prior.Count, string.Join(", ", prior.Select(x => x.InternalId + " (" + x.Name + ")")));

            if (prior.Count > 0) library.DeleteItems(prior.Select(x => x.InternalId).ToArray());
        }

        private Person CreatePerson(string name, List<long> created)
        {
            var person = new Person { Name = name, Id = library.GetNewItemIdFromName(name, typeof(Person)), DateCreated = DateTime.UtcNow };
            library.CreateItem(person, null);
            created.Add(person.InternalId);
            Require(person.InternalId > 0, "CreateItem did not assign an internal ID to " + name + ".");
            logger.Info("{0} Created Person {1}: {2}.", ProbeMarker, person.InternalId, name);
            return person;
        }

        private Series CreateSeries(string name, List<long> created)
        {
            var media = new Series { Name = name, Id = library.GetNewItemIdFromName(name, typeof(Series)), DateCreated = DateTime.UtcNow };
            library.CreateItem(media, null);
            created.Add(media.InternalId);
            Require(media.InternalId > 0 && media.SupportsPeople, "Probe series was not created as people-capable media.");
            logger.Info("{0} Created Series {1}: {2}.", ProbeMarker, media.InternalId, name);
            return media;
        }

        private void ProbeProviderIds(Person source)
        {
            source.SetProviderId(MetadataProviders.Tmdb, "pc-probe-original");
            library.UpdateItem(source, null, ItemUpdateType.MetadataEdit);
            var loaded = (Person)library.GetItemById(source.InternalId);
            Require(loaded.GetProviderId(MetadataProviders.Tmdb) == "pc-probe-original", "Provider-ID set did not round-trip.");

            loaded.SetProviderId(MetadataProviders.Tmdb, "pc-probe-replaced");
            library.UpdateItem(loaded, null, ItemUpdateType.MetadataEdit);
            loaded = (Person)library.GetItemById(source.InternalId);
            Require(loaded.GetProviderId(MetadataProviders.Tmdb) == "pc-probe-replaced", "Provider-ID replacement did not round-trip.");

            loaded.ProviderIds.Remove(MetadataProviders.Tmdb.ToString());
            library.UpdateItem(loaded, null, ItemUpdateType.MetadataEdit);
            loaded = (Person)library.GetItemById(source.InternalId);
            Require(string.IsNullOrWhiteSpace(loaded.GetProviderId(MetadataProviders.Tmdb)), "Provider-ID removal did not round-trip.");
            logger.Info("{0} PASS provider IDs: set, replace, and remove via UpdateItem(MetadataEdit).", ProbeMarker);
        }

        private void ProbePeopleReadContracts(Series media, Person source, Person target)
        {
            library.UpdatePeople(media, new List<PersonInfo>
            {
                Info(source, PersonType.Actor, "Probe Source Role"),
                Info(target, PersonType.Director, "Probe Preserved Director")
            }, false);

            var convenience = library.GetItemPeople(media);
            var explicitRows = ReadPeople(media.InternalId);
            Require(convenience.Count == 2 && convenience.All(x => x.Id == 0), "The convenience GetItemPeople overload no longer exhibits the expected unhydrated-ID contract; review executor assumptions.");
            Require(explicitRows.Count == 2 && explicitRows.Any(x => x.Id == source.InternalId) && explicitRows.Any(x => x.Id == target.InternalId), "Explicit people query did not hydrate expected IDs.");
            logger.Info("{0} PASS reads: convenience overload returned IDs [{1}]; explicit EnableIds query returned [{2}].", ProbeMarker, string.Join(",", convenience.Select(x => x.Id)), string.Join(",", explicitRows.Select(x => x.Id)));
        }

        private void ProbeCreditMoveAndPreservation(Series media, Person source, Person target)
        {
            var rows = ReadPeople(media.InternalId);
            var sourceRow = rows.Single(x => x.Id == source.InternalId && x.Type == PersonType.Actor && x.Role == "Probe Source Role");
            sourceRow.Id = target.InternalId;
            sourceRow.Name = target.Name;
            sourceRow.ProviderIds = target.ProviderIds;
            library.UpdatePeople(media, rows, false);

            var after = ReadPeople(media.InternalId);
            Require(!after.Any(x => x.Id == source.InternalId), "Source credit remained after move.");
            Require(after.Any(x => x.Id == target.InternalId && x.Type == PersonType.Actor && x.Role == "Probe Source Role"), "Moved target credit is missing or changed.");
            Require(after.Any(x => x.Id == target.InternalId && x.Type == PersonType.Director && x.Role == "Probe Preserved Director"), "Unrelated target credit was lost during full-list replacement.");
            logger.Info("{0} PASS move: source relationship moved to target and unrelated relationship survived UpdatePeople full replacement.", ProbeMarker);
        }

        private void ProbeDuplicateCollapse(Series media, Person source, Person target)
        {
            library.UpdatePeople(media, new List<PersonInfo>
            {
                Info(source, PersonType.Actor, "Duplicate Role"),
                Info(target, PersonType.Actor, "Duplicate Role"),
                Info(target, PersonType.Writer, "Preserve Writer")
            }, false);
            var rows = ReadPeople(media.InternalId);
            var sourceRows = rows.Where(x => x.Id == source.InternalId && x.Type == PersonType.Actor && x.Role == "Duplicate Role").ToList();
            foreach (var row in sourceRows) rows.Remove(row); // Executor behavior when target relationship already exists.
            library.UpdatePeople(media, rows, false);

            var after = ReadPeople(media.InternalId);
            Require(after.Count(x => x.Id == target.InternalId && x.Type == PersonType.Actor && x.Role == "Duplicate Role") == 1, "Duplicate target relationship was not collapsed to one row.");
            Require(after.Any(x => x.Id == target.InternalId && x.Type == PersonType.Writer && x.Role == "Preserve Writer"), "Unrelated writer relationship was lost during duplicate collapse.");
            logger.Info("{0} PASS duplicate handling: existing target relationship retained once; source duplicate removed; unrelated row preserved.", ProbeMarker);
        }

        private void ProbeSharedProviderIdRelease(Series media, Person source, Person target)
        {
            const string sharedId = "pc-probe-shared-identity";
            source = (Person)library.GetItemById(source.InternalId);
            target = (Person)library.GetItemById(target.InternalId);
            source.SetProviderId(MetadataProviders.Tmdb, sharedId);
            target.SetProviderId(MetadataProviders.Tmdb, sharedId);
            library.UpdateItem(source, null, ItemUpdateType.MetadataEdit);
            library.UpdateItem(target, null, ItemUpdateType.MetadataEdit);

            library.UpdatePeople(media, new List<PersonInfo> { Info(source, PersonType.Actor, "Shared Identity Role") }, false);
            var initiallyResolved = ReadPeople(media.InternalId).Single();
            logger.Info("{0} Shared-ID setup resolved the relationship to Person {1}; requested source was {2}, survivor is {3}.", ProbeMarker, initiallyResolved.Id, source.InternalId, target.InternalId);

            source = (Person)library.GetItemById(source.InternalId);
            source.ProviderIds.Remove(MetadataProviders.Tmdb.ToString());
            library.UpdateItem(source, null, ItemUpdateType.MetadataEdit);
            target = (Person)library.GetItemById(target.InternalId);

            var releasedRows = ReadPeople(media.InternalId);
            var releasedRow = releasedRows.Single();
            releasedRow.Id = target.InternalId;
            releasedRow.Guid = target.Id;
            releasedRow.Name = target.Name;
            releasedRow.ProviderIds = target.ProviderIds;
            library.UpdatePeople(media, releasedRows, false);
            var releaseOnlyResult = ReadPeople(media.InternalId).Single().Id;
            logger.Info("{0} Shared-ID release without a resolver token resolved to Person {1}; expected survivor {2}. This records whether Emby's provider resolver cache remained stale.", ProbeMarker, releaseOnlyResult, target.InternalId);

            // The failed control rewrite can restore the shared ID onto the cached
            // shadow through UpdateValuesIfNeeded. Re-establish the production
            // precondition before testing the tokenized path.
            source = (Person)library.GetItemById(source.InternalId);
            source.ProviderIds.Remove(MetadataProviders.Tmdb.ToString());
            library.UpdateItem(source, null, ItemUpdateType.MetadataEdit);
            Require(string.IsNullOrWhiteSpace(((Person)library.GetItemById(source.InternalId)).GetProviderId(MetadataProviders.Tmdb)), "Could not re-release the shared provider ID after the cache-control rewrite.");

            var token = Guid.NewGuid().ToString("N");
            target = (Person)library.GetItemById(target.InternalId);
            target.ProviderIds[ResolverTokenProvider] = token;
            library.UpdateItem(target, null, ItemUpdateType.MetadataEdit);
            try
            {
                var rows = ReadPeople(media.InternalId);
                var row = rows.Single();
                row.Id = target.InternalId;
                row.Guid = target.Id;
                row.Name = target.Name;
                row.ProviderIds = new ProviderIdDictionary { [ResolverTokenProvider] = token };
                library.UpdatePeople(media, rows, false);
            }
            finally
            {
                target = (Person)library.GetItemById(target.InternalId);
                target.ProviderIds.Remove(ResolverTokenProvider);
                library.UpdateItem(target, null, ItemUpdateType.MetadataEdit);
            }

            var after = ReadPeople(media.InternalId);
            Require(after.Count == 1 && after[0].Id == target.InternalId && after[0].Role == "Shared Identity Role", "The temporary resolver token did not make UpdatePeople resolve the credit to the survivor.");
            Require(string.IsNullOrWhiteSpace(((Person)library.GetItemById(source.InternalId)).GetProviderId(MetadataProviders.Tmdb)), "Shared provider ID reappeared on the shadow person.");
            Require(!((Person)library.GetItemById(target.InternalId)).ProviderIds.ContainsKey(ResolverTokenProvider), "Temporary resolver token remained on the survivor.");
            logger.Info("{0} PASS shared-ID resolver token: UpdatePeople resolved the relationship to survivor Person {1}, and the temporary token was removed.", ProbeMarker, target.InternalId);
        }

        private long ProbeImplicitPersonCreationAndOrphanLifecycle(Series media, string suffix, CancellationToken cancellationToken)
        {
            var name = OrphanMarker + suffix;
            library.UpdatePeople(media, new List<PersonInfo> { new PersonInfo { Name = name, Type = PersonType.Actor, Role = "Transient Role" } }, false);
            var linked = ReadPeople(media.InternalId).Single(x => x.Name == name);
            Require(linked.Id > 0, "UpdatePeople did not create/resolve a Person row for an ID-less PersonInfo.");
            Require(library.GetItemById(linked.Id) is Person, "Implicitly resolved Person row cannot be loaded.");

            library.UpdatePeople(media, new List<PersonInfo>(), false);
            Require(ReadPeople(media.InternalId).Count == 0, "Empty UpdatePeople replacement did not unlink the transient person.");
            var deadIds = library.GetInternalItemIds(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { typeof(Person).Name },
                ItemIds = new[] { linked.Id },
                IsDeadPerson = true
            }, cancellationToken);
            Require(deadIds.Contains(linked.Id), "Unreferenced transient person is not classified as IsDeadPerson.");
            logger.Info("{0} PASS implicit lifecycle: ID-less PersonInfo resolved to Person {1}; unlinking left it present and classified IsDeadPerson=true.", ProbeMarker, linked.Id);
            return linked.Id;
        }

        private List<PersonInfo> ReadPeople(long mediaId) => library.GetItemPeople(new InternalPeopleQuery
        {
            ItemIds = new[] { mediaId },
            EnableIds = true,
            EnableProviderIds = true,
            EnableGroupByName = false
        });

        private static PersonInfo Info(Person person, PersonType type, string role) => new PersonInfo
        {
            Id = person.InternalId,
            Guid = person.Id,
            Name = person.Name,
            ProviderIds = person.ProviderIds,
            Type = type,
            Role = role
        };

        private void RemoveCreated(List<long> created)
        {
            foreach (var id in created.Distinct().Reverse().ToList())
            {
                try
                {
                    var item = library.GetItemById(id);
                    if (item == null) continue;
                    if (!(item.Name ?? string.Empty).StartsWith(ProbeMarker, StringComparison.Ordinal))
                    {
                        logger.Error("{0} REFUSED cleanup of InternalId={1}: name no longer has the probe marker.", ProbeMarker, id);
                        continue;
                    }
                    library.DeleteItems(new[] { id });
                    logger.Info("{0} Removed explicit fixture {1} ({2}).", ProbeMarker, id, item.Name);
                }
                catch (Exception ex) { logger.ErrorException(ProbeMarker + " cleanup failed for InternalId=" + id, ex); }
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(ProbeMarker + " FAIL: " + message);
        }
    }
}
