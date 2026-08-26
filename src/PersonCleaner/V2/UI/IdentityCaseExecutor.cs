using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using PersonCleaner.V2.Domain;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace PersonCleaner.V2.UI
{
    internal sealed class IdentityCaseExecutor
    {
        private const string ResolverTokenProvider = "PersonCleanerCaseToken";
        private readonly ILibraryManager library;
        public IdentityCaseExecutor(ILibraryManager library) { this.library = library ?? throw new ArgumentNullException(nameof(library)); }

        public IdentityCaseApplyReceipt Apply(IdentityCasePlan plan, Action<IdentityCaseApplyReceipt> persistCommit)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (persistCommit == null) throw new ArgumentNullException(nameof(persistCommit));
            Preflight(plan);
            var receipt = new IdentityCaseApplyReceipt();
            foreach (var outcome in plan.Outcomes.Where(x => x.TargetEmbyId.HasValue)) receipt.OutcomeEmbyIds[outcome.OutcomeId] = outcome.TargetEmbyId.Value;
            var personSnapshots = SnapshotPeople(plan.CurrentPeople.Select(x => x.EmbyId));
            var mediaSnapshots = plan.Credits.Where(x => x.Disposition == "MOVE").Select(x => x.MediaEmbyId).Distinct().ToDictionary(x => x, ReadPeople);
            var written = HasMutations(plan);
            try
            {
                ApplyExistingProviderIds(plan, receipt);
                foreach (var media in plan.Credits.Where(x => x.Disposition == "MOVE").GroupBy(x => x.MediaEmbyId).OrderBy(x => x.Key))
                {
                    ApplyMedia(plan, media.Key, media.ToList(), receipt);
                }
                ApplyNewProviderIds(plan, receipt);
                Postflight(plan, receipt);
                receipt.Summary = plan.ApplyCaption.Substring("Apply: ".Length) + ".";
                persistCommit(receipt);
                return receipt;
            }
            catch (Exception failure)
            {
                if (!written) throw;
                try
                {
                    Restore(personSnapshots, mediaSnapshots, receipt);
                    throw new InvalidOperationException("Apply failed and every changed person ID and media credit was restored: " + failure.Message, failure);
                }
                catch (InvalidOperationException ex) when (ex.InnerException == failure) { throw; }
                catch (Exception rollback)
                {
                    throw new InvalidOperationException("Apply failed and automatic rollback also failed. Emby must be inspected before retrying. Apply error: " + failure.Message + " Rollback error: " + rollback.Message, failure);
                }
            }
        }

        private static bool HasMutations(IdentityCasePlan plan)
        {
            if (plan.Credits.Any(x => x.Disposition == "MOVE") || plan.Outcomes.Any(x => x.TargetKind == IdentityTargetKinds.New)) return true;
            foreach (var snapshot in plan.CurrentPeople)
            {
                var outcome = plan.Outcomes.FirstOrDefault(x => x.TargetEmbyId == snapshot.EmbyId);
                if (!Same(snapshot.TmdbId, outcome?.ProviderIds.FirstOrDefault(x => x.Provider == ProviderNames.Tmdb)?.ProviderId) || !Same(snapshot.TvdbId, outcome?.ProviderIds.FirstOrDefault(x => x.Provider == ProviderNames.Tvdb)?.ProviderId) || !Same(snapshot.ImdbId, outcome?.ProviderIds.FirstOrDefault(x => x.Provider == ProviderNames.Imdb)?.ProviderId)) return true;
            }
            return false;
        }

        private void Preflight(IdentityCasePlan plan)
        {
            if (plan.State != IdentityPlanStates.Complete) throw new InvalidOperationException("The reviewed case is not complete.");
            if (plan.Outcomes.Any(x => x.TargetKind == IdentityTargetKinds.New && (!x.ProviderIds.Any(y => y.Source == "native") || !plan.Credits.Any(c => c.TargetOutcomeId == x.OutcomeId))))
                throw new InvalidOperationException("A proposed new person lacks a provider-native ID or assigned media credit.");
            if (plan.Outcomes.SelectMany(x => x.ProviderIds.Select(y => new { Outcome = x, Id = y })).GroupBy(x => x.Id.Provider + ":" + x.Id.ProviderId, StringComparer.OrdinalIgnoreCase).Any(x => x.Select(y => y.Outcome.OutcomeId).Distinct().Count() > 1))
                throw new InvalidOperationException("The plan assigns one provider person ID to more than one final identity.");
            foreach (var snapshot in plan.CurrentPeople)
            {
                var retained = plan.Outcomes.FirstOrDefault(x => x.TargetEmbyId == snapshot.EmbyId && plan.Credits.Any(c => c.TargetOutcomeId == x.OutcomeId));
                if (retained != null)
                foreach (var provider in new[] { ProviderNames.Tmdb, ProviderNames.Tvdb, ProviderNames.Imdb })
                    if (!string.IsNullOrWhiteSpace(LocalProviderId(snapshot, provider)) && !retained.ProviderIds.Any(x => x.Provider == provider))
                        throw new InvalidOperationException("The reviewed plan omits the current " + provider.ToUpperInvariant() + " ID from retained Emby person " + snapshot.EmbyId + ". Rebuild the evidence before applying.");
                var live = library.GetItemById(snapshot.EmbyId) as Person ?? throw new InvalidOperationException("Emby person " + snapshot.EmbyId + " no longer exists.");
                if (!string.Equals(live.Name ?? string.Empty, snapshot.Name ?? string.Empty, StringComparison.Ordinal) || !Same(ProviderId(live, ProviderNames.Tmdb), snapshot.TmdbId) || !Same(ProviderId(live, ProviderNames.Tvdb), snapshot.TvdbId) || !Same(ProviderId(live, ProviderNames.Imdb), snapshot.ImdbId))
                    throw new InvalidOperationException("Emby person " + snapshot.EmbyId + " changed after this plan was calculated. Rebuild the evidence before applying.");
            }
            foreach (var outcome in plan.Outcomes.Where(x => x.TargetEmbyId.HasValue))
                if (!(library.GetItemById(outcome.TargetEmbyId.Value) is Person)) throw new InvalidOperationException("Target Emby person " + outcome.TargetEmbyId.Value + " no longer exists.");
            foreach (var mediaGroup in plan.Credits.GroupBy(x => x.MediaEmbyId))
            {
                if (library.GetItemById(mediaGroup.Key) == null) throw new InvalidOperationException("Emby media " + mediaGroup.Key + " no longer exists.");
                var live = ReadPeople(mediaGroup.Key);
                foreach (var credit in mediaGroup)
                    if (!live.Any(x => x.Id == credit.SourcePersonEmbyId && RoleText(x) == credit.Role))
                        throw new InvalidOperationException("The reviewed credit '" + credit.Role + "' on Emby media " + credit.MediaEmbyId + " changed after calculation.");
            }
            var currentIds = new HashSet<long>(plan.CurrentPeople.Select(x => x.EmbyId));
            var global = library.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { typeof(Person).Name }, Recursive = true }, CancellationToken.None).OfType<Person>().ToList();
            foreach (var outcome in plan.Outcomes)
            foreach (var id in outcome.ProviderIds)
            {
                var owner = global.FirstOrDefault(x => !currentIds.Contains(x.InternalId) && Same(ProviderId(x, id.Provider), id.ProviderId));
                if (owner != null && (!outcome.TargetEmbyId.HasValue || owner.InternalId != outcome.TargetEmbyId.Value))
                    throw new InvalidOperationException(id.Provider.ToUpperInvariant() + " person ID " + id.ProviderId + " is now owned by out-of-scope Emby person " + owner.InternalId + ".");
            }
        }

        private bool ApplyExistingProviderIds(IdentityCasePlan plan, IdentityCaseApplyReceipt receipt)
        {
            var desired = plan.CurrentPeople.ToDictionary(x => x.EmbyId, x => plan.Outcomes.FirstOrDefault(o => o.TargetEmbyId == x.EmbyId));
            var changed = false;
            foreach (var snapshot in plan.CurrentPeople)
            {
                var outcome = desired[snapshot.EmbyId];
                var person = (Person)library.GetItemById(snapshot.EmbyId);
                var personChanged = false;
                foreach (var provider in new[] { ProviderNames.Tmdb, ProviderNames.Tvdb, ProviderNames.Imdb })
                {
                    var before = ProviderId(person, provider);
                    var after = outcome?.ProviderIds.FirstOrDefault(x => x.Provider == provider)?.ProviderId;
                    if (Same(before, after)) continue;
                    if (!string.IsNullOrWhiteSpace(before)) { SetProviderId(person, provider, null); personChanged = true; changed = true; }
                    receipt.Changes.Add(new IdentityCaseAppliedChange { Kind = "person-provider-id", SourceEmbyId = person.InternalId, TargetEmbyId = person.InternalId, OutcomeId = outcome?.OutcomeId, Provider = provider, OldValue = before, NewValue = after, Summary = (string.IsNullOrWhiteSpace(after) ? "Remove " : string.IsNullOrWhiteSpace(before) ? "Add " : "Replace ") + provider.ToUpperInvariant() + " ID on Emby person " + person.InternalId + "." });
                }
                if (personChanged) library.UpdateItem(person, null, ItemUpdateType.MetadataEdit);
            }
            foreach (var snapshot in plan.CurrentPeople)
            {
                var outcome = desired[snapshot.EmbyId];
                var person = (Person)library.GetItemById(snapshot.EmbyId); var personChanged = false;
                foreach (var provider in new[] { ProviderNames.Tmdb, ProviderNames.Tvdb, ProviderNames.Imdb })
                {
                    var after = outcome?.ProviderIds.FirstOrDefault(x => x.Provider == provider)?.ProviderId;
                    if (string.IsNullOrWhiteSpace(after) || Same(ProviderId(person, provider), after)) continue;
                    SetProviderId(person, provider, after); personChanged = true; changed = true;
                }
                if (personChanged) library.UpdateItem(person, null, ItemUpdateType.MetadataEdit);
            }
            return changed;
        }

        private void ApplyMedia(IdentityCasePlan plan, long mediaId, List<IdentityCreditOutcome> changes, IdentityCaseApplyReceipt receipt)
        {
            var media = library.GetItemById(mediaId) ?? throw new InvalidOperationException("Emby media " + mediaId + " no longer exists.");
            var people = ReadPeople(mediaId);
            var tokens = new Dictionary<long, string>();
            try
            {
                foreach (var outcome in changes.Select(x => plan.Outcomes.First(o => o.OutcomeId == x.TargetOutcomeId)).Where(x => receipt.OutcomeEmbyIds.ContainsKey(x.OutcomeId)).Distinct())
                {
                    var person = (Person)library.GetItemById(receipt.OutcomeEmbyIds[outcome.OutcomeId]);
                    var token = Guid.NewGuid().ToString("N"); tokens[person.InternalId] = token; person.ProviderIds[ResolverTokenProvider] = token; library.UpdateItem(person, null, ItemUpdateType.MetadataEdit);
                    foreach (var row in people.Where(x => x.Id == person.InternalId)) SetResolver(row, person, token);
                }
                foreach (var change in changes)
                {
                    var outcome = plan.Outcomes.First(x => x.OutcomeId == change.TargetOutcomeId);
                    var sources = people.Where(x => x.Id == change.SourcePersonEmbyId && RoleText(x) == change.Role).ToList();
                    foreach (var source in sources)
                    {
                        long targetId;
                        if (receipt.OutcomeEmbyIds.TryGetValue(outcome.OutcomeId, out targetId))
                        {
                            var target = (Person)library.GetItemById(targetId);
                            if (people.Any(x => x.Id == targetId && x.Type == source.Type && string.Equals(x.Role ?? string.Empty, source.Role ?? string.Empty, StringComparison.Ordinal))) people.Remove(source);
                            else SetResolver(source, target, tokens[targetId]);
                        }
                        else
                        {
                            source.Id = 0; source.Guid = Guid.Empty; source.Name = outcome.DisplayName; source.ProviderIds = ProviderDictionary(outcome);
                        }
                    }
                }
                library.UpdatePeople(media, people, false);
                var after = ReadPeople(mediaId);
                foreach (var outcome in changes.Select(x => plan.Outcomes.First(o => o.OutcomeId == x.TargetOutcomeId)).Where(x => !receipt.OutcomeEmbyIds.ContainsKey(x.OutcomeId)).Distinct())
                {
                    var resolved = after.FirstOrDefault(x => outcome.ProviderIds.Where(y => y.Source == "native").All(y => Same(PersonInfoProviderId(x, y.Provider), y.ProviderId)));
                    if (resolved == null || resolved.Id <= 0) throw new InvalidOperationException("Emby did not create or resolve the provider-identified person for outcome " + outcome.OutcomeId + ".");
                    receipt.OutcomeEmbyIds[outcome.OutcomeId] = resolved.Id;
                    receipt.Changes.Add(new IdentityCaseAppliedChange { Kind = "create-person", TargetEmbyId = resolved.Id, OutcomeId = outcome.OutcomeId, Summary = "Create provider-identified Emby person " + resolved.Id + " (" + outcome.DisplayName + ")." });
                }
                foreach (var change in changes)
                {
                    var targetId = receipt.OutcomeEmbyIds[change.TargetOutcomeId];
                    receipt.Changes.Add(new IdentityCaseAppliedChange { Kind = "move-credit", SourceEmbyId = change.SourcePersonEmbyId, TargetEmbyId = targetId, OutcomeId = change.TargetOutcomeId, MediaEmbyId = change.MediaEmbyId, Role = change.Role, Summary = "Move '" + change.Role + "' on Emby media " + change.MediaEmbyId + " from person " + change.SourcePersonEmbyId + " to person " + targetId + "." });
                }
            }
            finally
            {
                foreach (var id in tokens.Keys)
                {
                    var person = library.GetItemById(id) as Person; if (person == null) continue;
                    person.ProviderIds.Remove(ResolverTokenProvider); library.UpdateItem(person, null, ItemUpdateType.MetadataEdit);
                }
            }
        }

        private void ApplyNewProviderIds(IdentityCasePlan plan, IdentityCaseApplyReceipt receipt)
        {
            foreach (var outcome in plan.Outcomes.Where(x => x.TargetKind == IdentityTargetKinds.New))
            {
                var person = library.GetItemById(receipt.OutcomeEmbyIds[outcome.OutcomeId]) as Person ?? throw new InvalidOperationException("The new Emby person for " + outcome.DisplayName + " could not be reloaded.");
                foreach (var provider in new[] { ProviderNames.Tmdb, ProviderNames.Tvdb, ProviderNames.Imdb }) SetProviderId(person, provider, outcome.ProviderIds.FirstOrDefault(x => x.Provider == provider)?.ProviderId);
                library.UpdateItem(person, null, ItemUpdateType.MetadataEdit);
            }
        }

        private void Postflight(IdentityCasePlan plan, IdentityCaseApplyReceipt receipt)
        {
            foreach (var outcome in plan.Outcomes.Where(x => receipt.OutcomeEmbyIds.ContainsKey(x.OutcomeId)))
            {
                var person = library.GetItemById(receipt.OutcomeEmbyIds[outcome.OutcomeId]) as Person ?? throw new InvalidOperationException("Postflight could not load result person " + receipt.OutcomeEmbyIds[outcome.OutcomeId] + ".");
                foreach (var provider in new[] { ProviderNames.Tmdb, ProviderNames.Tvdb, ProviderNames.Imdb })
                    if (!Same(ProviderId(person, provider), outcome.ProviderIds.FirstOrDefault(x => x.Provider == provider)?.ProviderId)) throw new InvalidOperationException("Postflight found an unexpected " + provider.ToUpperInvariant() + " ID on Emby person " + person.InternalId + ".");
            }
            foreach (var credit in plan.Credits)
            {
                var target = receipt.OutcomeEmbyIds[credit.TargetOutcomeId]; var people = ReadPeople(credit.MediaEmbyId);
                if (!people.Any(x => x.Id == target && RoleText(x) == credit.Role) || target != credit.SourcePersonEmbyId && people.Any(x => x.Id == credit.SourcePersonEmbyId && RoleText(x) == credit.Role))
                    throw new InvalidOperationException("Postflight found an unexpected assignment for '" + credit.Role + "' on Emby media " + credit.MediaEmbyId + ".");
            }
        }

        private Dictionary<long, PersonSnapshot> SnapshotPeople(IEnumerable<long> ids)
        {
            return ids.Distinct().ToDictionary(x => x, x => { var p = (Person)library.GetItemById(x); return new PersonSnapshot { Person = p, ProviderIds = p.ProviderIds.ToDictionary(y => y.Key, y => y.Value, StringComparer.OrdinalIgnoreCase) }; });
        }
        private void Restore(Dictionary<long, PersonSnapshot> people, Dictionary<long, List<PersonInfo>> media, IdentityCaseApplyReceipt receipt)
        {
            foreach (var item in people.Values) { item.Person.ProviderIds = new ProviderIdDictionary(); foreach (var id in item.ProviderIds) item.Person.ProviderIds[id.Key] = id.Value; library.UpdateItem(item.Person, null, ItemUpdateType.MetadataEdit); }
            foreach (var item in media) library.UpdatePeople(library.GetItemById(item.Key), item.Value.Select(Clone).ToList(), false);
            foreach (var id in receipt.OutcomeEmbyIds.Values.Where(x => !people.ContainsKey(x)).Distinct())
            {
                var person = library.GetItemById(id) as Person; if (person == null) continue;
                person.ProviderIds.Remove(MetadataProviders.Tmdb.ToString()); person.ProviderIds.Remove(MetadataProviders.Tvdb.ToString()); person.ProviderIds.Remove(MetadataProviders.Imdb.ToString()); person.ProviderIds.Remove(ResolverTokenProvider);
                library.UpdateItem(person, null, ItemUpdateType.MetadataEdit);
            }
        }
        private List<PersonInfo> ReadPeople(long mediaId) => library.GetItemPeople(new InternalPeopleQuery { ItemIds = new[] { mediaId }, EnableIds = true, EnableProviderIds = true, EnableGroupByName = false });
        private static PersonInfo Clone(PersonInfo x) => new PersonInfo { Id = x.Id, Guid = x.Guid, Name = x.Name, Type = x.Type, Role = x.Role, ProviderIds = Copy(x.ProviderIds) };
        private static ProviderIdDictionary Copy(ProviderIdDictionary source) { var result = new ProviderIdDictionary(); if (source != null) foreach (var x in source) result[x.Key] = x.Value; return result; }
        private static ProviderIdDictionary ProviderDictionary(IdentityOutcome outcome) { var result = new ProviderIdDictionary(); foreach (var x in outcome.ProviderIds) result[ProviderName(x.Provider)] = x.ProviderId; return result; }
        private static void SetResolver(PersonInfo row, Person person, string token) { row.Id = person.InternalId; row.Guid = person.Id; row.Name = person.Name; row.ProviderIds = new ProviderIdDictionary { [ResolverTokenProvider] = token }; }
        private static void SetProviderId(Person person, string provider, string value) { var key = ProviderName(provider); foreach (var existing in person.ProviderIds.Keys.Where(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase)).ToList()) person.ProviderIds.Remove(existing); if (!string.IsNullOrWhiteSpace(value)) person.ProviderIds[key] = value; }
        private static string ProviderId(Person person, string provider) => person?.ProviderIds?.FirstOrDefault(x => string.Equals(x.Key, ProviderName(provider), StringComparison.OrdinalIgnoreCase)).Value;
        private static string PersonInfoProviderId(PersonInfo person, string provider) => person?.ProviderIds?.FirstOrDefault(x => string.Equals(x.Key, ProviderName(provider), StringComparison.OrdinalIgnoreCase)).Value;
        private static string LocalProviderId(LocalPerson person, string provider) => provider == ProviderNames.Tmdb ? person.TmdbId : provider == ProviderNames.Tvdb ? person.TvdbId : person.ImdbId;
        private static string ProviderName(string provider) => provider == ProviderNames.Tmdb ? MetadataProviders.Tmdb.ToString() : provider == ProviderNames.Tvdb ? MetadataProviders.Tvdb.ToString() : MetadataProviders.Imdb.ToString();
        private static string RoleText(PersonInfo person) => person.Type + (string.IsNullOrWhiteSpace(person.Role) ? string.Empty : ": " + person.Role);
        private static bool Same(string a, string b) => string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        private sealed class PersonSnapshot { public Person Person { get; set; } public Dictionary<string, string> ProviderIds { get; set; } }
    }
}
