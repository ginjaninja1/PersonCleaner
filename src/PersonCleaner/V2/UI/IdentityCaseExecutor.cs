using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using PersonCleaner.V2.Domain;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

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
            var liveMediaPeople = plan.Credits.Select(x => x.MediaEmbyId).Distinct().ToDictionary(x => x, ReadPeople);
            Preflight(plan, liveMediaPeople);
            var receipt = new IdentityCaseApplyReceipt();
            foreach (var outcome in IdentityCasePersonBuilder.MediaBearingOutcomes(plan).Where(x => x.TargetEmbyId.HasValue)) receipt.OutcomeEmbyIds[outcome.OutcomeId] = outcome.TargetEmbyId.Value;
            var personSnapshots = SnapshotPeople(plan.CurrentPeople.Select(x => x.EmbyId));
            var mediaSnapshots = plan.Credits.Select(x => x.MediaEmbyId).Distinct().ToDictionary(x => x, x => liveMediaPeople[x].Select(Clone).ToList());
            // Apply is authoritative. Even a plan that matched the calculation-time
            // snapshot may need to overwrite live drift that occurred before the click.
            var written = true;
            var resolverTokens = new Dictionary<long, string>();
            var targetPeople = new Dictionary<long, Person>();
            try
            {
                ApplyExistingProviderIds(plan, receipt);
                try
                {
                    foreach (var media in plan.Credits.GroupBy(x => x.MediaEmbyId).OrderBy(x => x.Key))
                        ApplyMedia(plan, media.Key, media.ToList(), liveMediaPeople, receipt, resolverTokens, targetPeople);
                }
                finally { RemoveResolverTokens(resolverTokens, targetPeople); }
                ApplyNewProviderIds(plan, receipt);
                Postflight(plan, receipt, liveMediaPeople);
                receipt.Summary = HasMutations(plan)
                    ? (plan.ApplyCaption.StartsWith("Apply: ", StringComparison.Ordinal) ? plan.ApplyCaption.Substring("Apply: ".Length) + "." : plan.ApplyCaption)
                    : "Confirmed person-ID and media-credit layout; no Emby changes were required.";
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

        internal static bool HasMutations(IdentityCasePlan plan)
        {
            if (plan == null) return false;
            var outcomes = IdentityCasePersonBuilder.MediaBearingOutcomes(plan);
            if (plan.Credits.Any(x => x.Disposition == "MOVE") || outcomes.Any(x => x.TargetKind == IdentityTargetKinds.New)) return true;
            foreach (var outcome in outcomes.Where(x => x.TargetEmbyId.HasValue))
            {
                var snapshot = plan.CurrentPeople.FirstOrDefault(x => x.EmbyId == outcome.TargetEmbyId.Value);
                if (snapshot != null && (!Same(LocalProviderId(snapshot, ProviderNames.Tmdb), DesiredProviderId(outcome, ProviderNames.Tmdb)) || !Same(LocalProviderId(snapshot, ProviderNames.Tvdb), DesiredProviderId(outcome, ProviderNames.Tvdb)) || !Same(LocalProviderId(snapshot, ProviderNames.Imdb), DesiredProviderId(outcome, ProviderNames.Imdb)))) return true;
            }
            return false;
        }

        private void Preflight(IdentityCasePlan plan, IReadOnlyDictionary<long, List<PersonInfo>> liveMediaPeople)
        {
            var finalOutcomes = IdentityCasePersonBuilder.MediaBearingOutcomes(plan);
            if (plan.State != IdentityPlanStates.Complete) throw new InvalidOperationException("The reviewed case is not complete.");
            if (finalOutcomes.Any(x => x.TargetKind == IdentityTargetKinds.New && !x.ProviderIds.Any(y => y.Source == "native")))
                throw new InvalidOperationException("A proposed new person lacks a provider-native ID or assigned media credit.");
            foreach (var snapshot in plan.CurrentPeople)
            {
                if (!(library.GetItemById(snapshot.EmbyId) is Person)) throw new InvalidOperationException("Emby person " + snapshot.EmbyId + " no longer exists.");
            }
            foreach (var outcome in finalOutcomes.Where(x => x.TargetEmbyId.HasValue))
                if (!(library.GetItemById(outcome.TargetEmbyId.Value) is Person)) throw new InvalidOperationException("Target Emby person " + outcome.TargetEmbyId.Value + " no longer exists.");
            foreach (var mediaGroup in plan.Credits.GroupBy(x => x.MediaEmbyId))
            {
                if (library.GetItemById(mediaGroup.Key) == null) throw new InvalidOperationException("Emby media " + mediaGroup.Key + " no longer exists.");
            }
        }

        private bool ApplyExistingProviderIds(IdentityCasePlan plan, IdentityCaseApplyReceipt receipt)
        {
            var finalOutcomes = IdentityCasePersonBuilder.MediaBearingOutcomes(plan);
            var desired = finalOutcomes.Where(x => x.TargetEmbyId.HasValue).ToDictionary(x => x.TargetEmbyId.Value);
            var live = plan.CurrentPeople.ToDictionary(x => x.EmbyId, x => (Person)library.GetItemById(x.EmbyId));
            var changed = false;

            // Release IDs from their current in-scope owner before assigning the
            // grid's final owner. Live state is authoritative input here; the
            // calculation-time snapshots are audit context, not a write veto.
            var releases = new Dictionary<long, HashSet<string>>();
            foreach (var outcome in finalOutcomes)
            foreach (var id in DesiredProviderIds(outcome))
            foreach (var owner in live.Values.Where(x => Same(ProviderId(x, id.Key), id.Value) && (!outcome.TargetEmbyId.HasValue || x.InternalId != outcome.TargetEmbyId.Value)))
            {
                if (!releases.TryGetValue(owner.InternalId, out var providers)) releases[owner.InternalId] = providers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                providers.Add(id.Key);
            }
            foreach (var release in releases)
            {
                var person = live[release.Key];
                foreach (var provider in release.Value) SetProviderId(person, provider, null);
                library.UpdateItem(person, null, ItemUpdateType.MetadataEdit);
            }

            foreach (var target in desired)
            {
                var outcome = target.Value;
                var person = live[target.Key]; var personChanged = false;
                foreach (var provider in new[] { ProviderNames.Tmdb, ProviderNames.Tvdb, ProviderNames.Imdb })
                {
                    var before = ProviderId(person, provider);
                    var after = DesiredProviderId(outcome, provider);
                    if (Same(before, after)) continue;
                    SetProviderId(person, provider, after); personChanged = true; changed = true;
                    receipt.Changes.Add(new IdentityCaseAppliedChange { Kind = "person-provider-id", SourceEmbyId = person.InternalId, TargetEmbyId = person.InternalId, OutcomeId = outcome?.OutcomeId, Provider = provider, OldValue = before, NewValue = after, Summary = (string.IsNullOrWhiteSpace(after) ? "Remove " : string.IsNullOrWhiteSpace(before) ? "Add " : "Replace ") + provider.ToUpperInvariant() + " ID on Emby person " + person.InternalId + "." });
                }
                if (personChanged) library.UpdateItem(person, null, ItemUpdateType.MetadataEdit);
            }
            return changed;
        }

        private void ApplyMedia(IdentityCasePlan plan, long mediaId, List<IdentityCreditOutcome> changes, Dictionary<long, List<PersonInfo>> liveMediaPeople, IdentityCaseApplyReceipt receipt, Dictionary<long, string> tokens, Dictionary<long, Person> targetPeople)
        {
            var media = library.GetItemById(mediaId) ?? throw new InvalidOperationException("Emby media " + mediaId + " no longer exists.");
            var people = liveMediaPeople[mediaId].Select(Clone).ToList();
            var changedAssignments = new HashSet<string>(StringComparer.Ordinal);
            var requiresReconciliation = changes.Any(change =>
            {
                var outcome = plan.Outcomes.First(x => x.OutcomeId == change.TargetOutcomeId);
                long targetId;
                if (!receipt.OutcomeEmbyIds.TryGetValue(outcome.OutcomeId, out targetId)) return true;
                return !people.Any(x => x.Id == targetId && RoleText(x) == change.Role) ||
                    targetId != change.SourcePersonEmbyId && people.Any(x => x.Id == change.SourcePersonEmbyId && RoleText(x) == change.Role);
            });
            if (!requiresReconciliation) return;
            foreach (var outcome in changes.Select(x => plan.Outcomes.First(o => o.OutcomeId == x.TargetOutcomeId)).Where(x => receipt.OutcomeEmbyIds.ContainsKey(x.OutcomeId)).Distinct())
            {
                var person = EnsureResolverToken(receipt.OutcomeEmbyIds[outcome.OutcomeId], tokens, targetPeople);
                foreach (var row in people.Where(x => x.Id == person.InternalId)) SetResolver(row, person, tokens[person.InternalId]);
            }
            foreach (var change in changes)
            {
                var outcome = plan.Outcomes.First(x => x.OutcomeId == change.TargetOutcomeId);
                var sources = people.Where(x => x.Id == change.SourcePersonEmbyId && RoleText(x) == change.Role).ToList();
                long existingTargetId;
                var hasExistingTarget = receipt.OutcomeEmbyIds.TryGetValue(outcome.OutcomeId, out existingTargetId);
                var alreadyAtTarget = hasExistingTarget && people.Any(x => x.Id == existingTargetId && RoleText(x) == change.Role);
                foreach (var source in sources)
                {
                    long targetId;
                    if (receipt.OutcomeEmbyIds.TryGetValue(outcome.OutcomeId, out targetId))
                    {
                        if (source.Id == targetId) continue;
                        var target = targetPeople.TryGetValue(targetId, out var cached) ? cached : EnsureResolverToken(targetId, tokens, targetPeople);
                        if (people.Any(x => x.Id == targetId && x.Type == source.Type && string.Equals(x.Role ?? string.Empty, source.Role ?? string.Empty, StringComparison.Ordinal))) people.Remove(source);
                        else SetResolver(source, target, tokens[targetId]);
                    }
                    else { source.Id = 0; source.Guid = Guid.Empty; source.Name = outcome.DisplayName; source.ProviderIds = ProviderDictionary(outcome); }
                    changedAssignments.Add(change.AssignmentId);
                }
                if (sources.Count == 0 && !alreadyAtTarget)
                {
                    var restored = CreditRow(change);
                    long targetId;
                    if (receipt.OutcomeEmbyIds.TryGetValue(outcome.OutcomeId, out targetId))
                    {
                        var target = targetPeople.TryGetValue(targetId, out var cached) ? cached : EnsureResolverToken(targetId, tokens, targetPeople);
                        SetResolver(restored, target, tokens[targetId]);
                    }
                    else
                    {
                        restored.Name = outcome.DisplayName;
                        restored.ProviderIds = ProviderDictionary(outcome);
                    }
                    people.Add(restored);
                    changedAssignments.Add(change.AssignmentId);
                }
            }
            library.UpdatePeople(media, people, false);
            var after = ReadPeople(mediaId);
            liveMediaPeople[mediaId] = after;
            foreach (var outcome in changes.Select(x => plan.Outcomes.First(o => o.OutcomeId == x.TargetOutcomeId)).Where(x => !receipt.OutcomeEmbyIds.ContainsKey(x.OutcomeId)).Distinct())
            {
                var resolved = after.FirstOrDefault(x => outcome.ProviderIds.Where(y => y.Source == "native").All(y => Same(PersonInfoProviderId(x, y.Provider), y.ProviderId)));
                if (resolved == null || resolved.Id <= 0) throw new InvalidOperationException("Emby did not create or resolve the provider-identified person for outcome " + outcome.OutcomeId + ".");
                receipt.OutcomeEmbyIds[outcome.OutcomeId] = resolved.Id;
                receipt.Changes.Add(new IdentityCaseAppliedChange { Kind = "create-person", TargetEmbyId = resolved.Id, OutcomeId = outcome.OutcomeId, Summary = "Create provider-identified Emby person " + resolved.Id + " (" + outcome.DisplayName + ")." });
            }
            foreach (var change in changes)
            {
                if (!changedAssignments.Contains(change.AssignmentId)) continue;
                var targetId = receipt.OutcomeEmbyIds[change.TargetOutcomeId];
                var moved = targetId != change.SourcePersonEmbyId;
                receipt.Changes.Add(new IdentityCaseAppliedChange { Kind = moved ? "move-credit" : "restore-credit", SourceEmbyId = change.SourcePersonEmbyId, TargetEmbyId = targetId, OutcomeId = change.TargetOutcomeId, MediaEmbyId = change.MediaEmbyId, Role = change.Role, Summary = (moved ? "Move" : "Restore") + " '" + change.Role + "' on Emby media " + change.MediaEmbyId + (moved ? " from person " + change.SourcePersonEmbyId + " to person " + targetId : " to person " + targetId) + "." });
            }
        }

        private Person EnsureResolverToken(long id, Dictionary<long, string> tokens, Dictionary<long, Person> targetPeople)
        {
            if (!targetPeople.TryGetValue(id, out var person)) targetPeople[id] = person = library.GetItemById(id) as Person ?? throw new InvalidOperationException("Target Emby person " + id + " no longer exists.");
            if (tokens.ContainsKey(id)) return person;
            var token = Guid.NewGuid().ToString("N"); tokens[id] = token; person.ProviderIds[ResolverTokenProvider] = token; library.UpdateItem(person, null, ItemUpdateType.MetadataEdit);
            return person;
        }

        private void RemoveResolverTokens(Dictionary<long, string> tokens, Dictionary<long, Person> targetPeople)
        {
            foreach (var id in tokens.Keys.ToList())
            {
                var person = targetPeople.TryGetValue(id, out var cached) ? cached : library.GetItemById(id) as Person;
                if (person == null) continue;
                person.ProviderIds.Remove(ResolverTokenProvider); library.UpdateItem(person, null, ItemUpdateType.MetadataEdit);
            }
        }

        private void ApplyNewProviderIds(IdentityCasePlan plan, IdentityCaseApplyReceipt receipt)
        {
            foreach (var outcome in IdentityCasePersonBuilder.MediaBearingOutcomes(plan).Where(x => x.TargetKind == IdentityTargetKinds.New))
            {
                var person = library.GetItemById(receipt.OutcomeEmbyIds[outcome.OutcomeId]) as Person ?? throw new InvalidOperationException("The new Emby person for " + outcome.DisplayName + " could not be reloaded.");
                foreach (var provider in new[] { ProviderNames.Tmdb, ProviderNames.Tvdb, ProviderNames.Imdb }) SetProviderId(person, provider, DesiredProviderId(outcome, provider));
                library.UpdateItem(person, null, ItemUpdateType.MetadataEdit);
            }
        }

        private void Postflight(IdentityCasePlan plan, IdentityCaseApplyReceipt receipt, IReadOnlyDictionary<long, List<PersonInfo>> liveMediaPeople)
        {
            foreach (var outcome in plan.Outcomes.Where(x => receipt.OutcomeEmbyIds.ContainsKey(x.OutcomeId)))
            {
                var person = library.GetItemById(receipt.OutcomeEmbyIds[outcome.OutcomeId]) as Person ?? throw new InvalidOperationException("Postflight could not load result person " + receipt.OutcomeEmbyIds[outcome.OutcomeId] + ".");
                foreach (var provider in new[] { ProviderNames.Tmdb, ProviderNames.Tvdb, ProviderNames.Imdb })
                    if (!Same(ProviderId(person, provider), DesiredProviderId(outcome, provider))) throw new InvalidOperationException("Postflight found an unexpected " + provider.ToUpperInvariant() + " ID on Emby person " + person.InternalId + ".");
            }
            foreach (var media in plan.Credits.GroupBy(x => x.MediaEmbyId))
            {
                var people = liveMediaPeople[media.Key];
                foreach (var credit in media)
                {
                    var target = receipt.OutcomeEmbyIds[credit.TargetOutcomeId];
                    if (!people.Any(x => x.Id == target && RoleText(x) == credit.Role) || target != credit.SourcePersonEmbyId && people.Any(x => x.Id == credit.SourcePersonEmbyId && RoleText(x) == credit.Role))
                        throw new InvalidOperationException("Postflight found an unexpected assignment for '" + credit.Role + "' on Emby media " + credit.MediaEmbyId + ".");
                }
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
        private static PersonInfo CreditRow(IdentityCreditOutcome credit)
        {
            var text = credit.Role ?? string.Empty;
            var separator = text.IndexOf(": ", StringComparison.Ordinal);
            var typeText = separator < 0 ? text : text.Substring(0, separator);
            PersonType type;
            if (!Enum.TryParse(typeText, true, out type)) throw new InvalidOperationException("Emby person type '" + typeText + "' cannot be restored for media " + credit.MediaEmbyId + ".");
            return new PersonInfo
            {
                Type = type,
                Role = separator < 0 ? null : text.Substring(separator + 2),
                ProviderIds = new ProviderIdDictionary()
            };
        }
        private static ProviderIdDictionary Copy(ProviderIdDictionary source) { var result = new ProviderIdDictionary(); if (source != null) foreach (var x in source) result[x.Key] = x.Value; return result; }
        private static ProviderIdDictionary ProviderDictionary(IdentityOutcome outcome) { var result = new ProviderIdDictionary(); foreach (var x in DesiredProviderIds(outcome)) result[ProviderName(x.Key)] = x.Value; return result; }
        private static void SetResolver(PersonInfo row, Person person, string token) { row.Id = person.InternalId; row.Guid = person.Id; row.Name = person.Name; row.ProviderIds = new ProviderIdDictionary { [ResolverTokenProvider] = token }; }
        private static void SetProviderId(Person person, string provider, string value) { var key = ProviderName(provider); foreach (var existing in person.ProviderIds.Keys.Where(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase)).ToList()) person.ProviderIds.Remove(existing); if (!string.IsNullOrWhiteSpace(value)) person.ProviderIds[key] = value; }
        private static string ProviderId(Person person, string provider) => person?.ProviderIds?.FirstOrDefault(x => string.Equals(x.Key, ProviderName(provider), StringComparison.OrdinalIgnoreCase)).Value;
        private static string PersonInfoProviderId(PersonInfo person, string provider) => person?.ProviderIds?.FirstOrDefault(x => string.Equals(x.Key, ProviderName(provider), StringComparison.OrdinalIgnoreCase)).Value;
        private static string DesiredProviderId(IdentityOutcome outcome, string provider) => IdentityCasePlanner.PreferredProviderId(outcome, provider);
        private static IEnumerable<KeyValuePair<string, string>> DesiredProviderIds(IdentityOutcome outcome)
        {
            foreach (var provider in new[] { ProviderNames.Tmdb, ProviderNames.Tvdb, ProviderNames.Imdb })
            {
                var id = DesiredProviderId(outcome, provider);
                if (!string.IsNullOrWhiteSpace(id)) yield return new KeyValuePair<string, string>(provider, id);
            }
        }
        private static string LocalProviderId(LocalPerson person, string provider) => provider == ProviderNames.Tmdb ? person.TmdbId : provider == ProviderNames.Tvdb ? person.TvdbId : person.ImdbId;
        private static string ProviderName(string provider) => provider == ProviderNames.Tmdb ? MetadataProviders.Tmdb.ToString() : provider == ProviderNames.Tvdb ? MetadataProviders.Tvdb.ToString() : MetadataProviders.Imdb.ToString();
        private static string RoleText(PersonInfo person) => person.Type + (string.IsNullOrWhiteSpace(person.Role) ? string.Empty : ": " + person.Role);
        private static bool Same(string a, string b) => string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        private sealed class PersonSnapshot { public Person Person { get; set; } public Dictionary<string, string> ProviderIds { get; set; } }
    }
}
