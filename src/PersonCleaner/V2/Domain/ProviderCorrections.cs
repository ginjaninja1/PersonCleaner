using System;
using System.Collections.Generic;
using System.Linq;

namespace PersonCleaner.V2.Domain
{
    public static class CorrectionKinds
    {
        public const string MediaCredit = "media-credit";
        public const string MediaCreditRole = "media-credit-role";
        public const string PersonExternalId = "person-external-id";
        public const string MediaExternalId = "media-external-id";
        public const string PersonField = "person-field";
        public const string LocalPersonBinding = "local-person-binding";
        public const string LocalMediaBinding = "local-media-binding";
        public const string IdentityRelation = "identity-relation";
        public const string IdentityTarget = "identity-target";
        public const string LocalCreditTarget = "local-credit-target";

        public static readonly string[] All =
        {
            MediaCredit, MediaCreditRole, PersonExternalId, MediaExternalId,
            PersonField, LocalPersonBinding, LocalMediaBinding, IdentityRelation,
            IdentityTarget, LocalCreditTarget
        };
    }

    public static class CorrectionOperations
    {
        public const string Unusable = "unusable";
        public const string Replace = "replace";
        public const string Same = "same";
        public const string Different = "different";

        public static readonly string[] All = { Unusable, Replace, Same, Different };
    }

    public sealed class ProviderCorrection
    {
        public long CorrectionId { get; set; }
        public string Kind { get; set; }
        public string Operation { get; set; }
        public string Provider { get; set; }
        public string MediaType { get; set; }
        public string ProviderMediaId { get; set; }
        public string ProviderPersonId { get; set; }
        public string FieldName { get; set; }
        public string CurrentValue { get; set; }
        public string ReplacementValue { get; set; }
        public string SecondaryProvider { get; set; }
        public string SecondaryId { get; set; }
        public long? EmbyId { get; set; }
        public string Reason { get; set; }
        public string Note { get; set; }
        public bool Enabled { get; set; } = true;
        public long CreatedUtc { get; set; }
        public long UpdatedUtc { get; set; }

        public void NormalizeAndValidate()
        {
            Kind = Lower(Kind); Operation = Lower(Operation); Provider = Lower(Provider);
            MediaType = Lower(MediaType); FieldName = Lower(FieldName); SecondaryProvider = Lower(SecondaryProvider);
            ProviderMediaId = Trim(ProviderMediaId); ProviderPersonId = Trim(ProviderPersonId);
            CurrentValue = Trim(CurrentValue); ReplacementValue = Trim(ReplacementValue); SecondaryId = Trim(SecondaryId);
            Reason = string.IsNullOrWhiteSpace(Reason) ? "PROVIDER_MISMATCH" : Reason.Trim();
            Note = Trim(Note);
            if (!CorrectionKinds.All.Contains(Kind, StringComparer.Ordinal)) throw new ArgumentException("Unknown correction type.");
            if (!CorrectionOperations.All.Contains(Operation, StringComparer.Ordinal)) throw new ArgumentException("Unknown correction operation.");
            if (Kind == CorrectionKinds.IdentityRelation)
            {
                RequireProvider(Provider); RequireProvider(SecondaryProvider);
                if (ProviderPersonId.Length == 0 || SecondaryId.Length == 0 || Provider == SecondaryProvider && ProviderPersonId == SecondaryId) throw new ArgumentException("Enter two distinct provider person records.");
                if (Operation != CorrectionOperations.Same && Operation != CorrectionOperations.Different) throw new ArgumentException("Identity relations must be same or different.");
                return;
            }
            if (Kind == CorrectionKinds.IdentityTarget)
            {
                RequireProvider(Provider);
                if (ProviderPersonId.Length == 0) throw new ArgumentException("Enter the provider person record that identifies the outcome.");
                if (Operation != CorrectionOperations.Replace || ReplacementValue.Length == 0) throw new ArgumentException("Choose an existing Emby person or a provider-identified new person.");
                return;
            }
            if (Kind == CorrectionKinds.LocalCreditTarget)
            {
                if (!EmbyId.HasValue || EmbyId.Value <= 0) throw new ArgumentException("Enter a valid Emby media ID.");
                if (CurrentValue.Length == 0 || Operation != CorrectionOperations.Replace || ReplacementValue.Length == 0) throw new ArgumentException("Choose the corrected destination for this Emby credit.");
                return;
            }
            if (Kind == CorrectionKinds.LocalPersonBinding || Kind == CorrectionKinds.LocalMediaBinding)
            {
                if (Kind == CorrectionKinds.LocalPersonBinding) RequirePersonProvider(Provider); else RequireProvider(Provider);
                if (!EmbyId.HasValue || EmbyId.Value <= 0) throw new ArgumentException("Enter a valid Emby item ID.");
                RequireUnusableOrReplacement();
                return;
            }
            RequireProvider(Provider);
            if (Kind == CorrectionKinds.PersonField || Kind == CorrectionKinds.PersonExternalId)
            {
                if (ProviderPersonId.Length == 0) throw new ArgumentException("Enter a provider person ID.");
                if (FieldName.Length == 0) throw new ArgumentException("Enter the field or external-ID provider.");
                if (Kind == CorrectionKinds.PersonField && FieldName != "name" && FieldName != "birthday") throw new ArgumentException("Person fields are name or birthday.");
                RequireUnusableOrReplacement();
                return;
            }
            if (MediaType != MediaTypes.Movie && MediaType != MediaTypes.Series && MediaType != MediaTypes.Episode) throw new ArgumentException("Media type must be movie, series or episode.");
            if (ProviderMediaId.Length == 0) throw new ArgumentException("Enter a provider media ID.");
            if (Kind == CorrectionKinds.MediaExternalId)
            {
                if (FieldName.Length == 0) throw new ArgumentException("Enter the external-ID provider.");
                RequireUnusableOrReplacement();
                return;
            }
            if (ProviderPersonId.Length == 0) throw new ArgumentException("Enter the provider person ID currently assigned to the credit.");
            RequireUnusableOrReplacement();
        }

        private void RequireUnusableOrReplacement()
        {
            if (Operation != CorrectionOperations.Unusable && Operation != CorrectionOperations.Replace) throw new ArgumentException("Choose unusable or replacement.");
            if (Operation == CorrectionOperations.Replace && ReplacementValue.Length == 0) throw new ArgumentException("Enter the replacement value.");
        }

        private static void RequireProvider(string provider)
        {
            if (provider != ProviderNames.Tmdb && provider != ProviderNames.Tvdb) throw new ArgumentException("Provider must be tmdb or tvdb.");
        }
        private static void RequirePersonProvider(string provider)
        {
            if (provider != ProviderNames.Tmdb && provider != ProviderNames.Tvdb && provider != ProviderNames.Imdb) throw new ArgumentException("Person provider must be tmdb, tvdb or imdb.");
        }
        private static string Lower(string value) => Trim(value).ToLowerInvariant();
        private static string Trim(string value) => (value ?? string.Empty).Trim();
    }

    public sealed class CorrectionApplication
    {
        public long CorrectionId { get; set; }
        public int MatchedCount { get; set; }
        public int ChangedCount { get; set; }
        public string Summary { get; set; }
        public bool Triggered => MatchedCount > 0;
    }

    public sealed class CorrectionApplicationTracker
    {
        private readonly Dictionary<long, CorrectionApplication> rows;
        private readonly Dictionary<long, ProviderCorrection> rules;

        public CorrectionApplicationTracker(IEnumerable<ProviderCorrection> corrections)
        {
            rules = (corrections ?? Enumerable.Empty<ProviderCorrection>()).Where(x => x.Enabled).OrderBy(x => x.CorrectionId).ToDictionary(x => x.CorrectionId);
            rows = rules.Values.ToDictionary(x => x.CorrectionId, x => new CorrectionApplication { CorrectionId = x.CorrectionId, Summary = Describe(x) });
        }

        public IReadOnlyList<ProviderCorrection> Rules => rules.Values.OrderBy(x => x.CorrectionId).ToList();
        public IReadOnlyList<CorrectionApplication> Results => rows.Values.OrderBy(x => x.CorrectionId).ToList();
        public void Match(ProviderCorrection rule, int changed = 1)
        {
            var row = rows[rule.CorrectionId]; row.MatchedCount++; row.ChangedCount += Math.Max(0, changed);
        }

        private static string Describe(ProviderCorrection rule)
        {
            if (rule.Kind == CorrectionKinds.MediaCredit)
                return rule.Provider.ToUpperInvariant() + (rule.Operation == CorrectionOperations.Unusable ? " has no usable person identity" : " uses person " + rule.ReplacementValue) + " for " + rule.MediaType + ":" + rule.ProviderMediaId + " credit " + Display(rule.CurrentValue, rule.ProviderPersonId) + ".";
            if (rule.Kind == CorrectionKinds.MediaCreditRole)
                return rule.Provider.ToUpperInvariant() + " role is corrected for " + rule.MediaType + ":" + rule.ProviderMediaId + ".";
            if (rule.Kind == CorrectionKinds.PersonExternalId)
                return rule.Provider.ToUpperInvariant() + " " + rule.FieldName + " person cross-reference is corrected for person " + rule.ProviderPersonId + ".";
            if (rule.Kind == CorrectionKinds.MediaExternalId)
                return rule.Provider.ToUpperInvariant() + " " + rule.FieldName + " media cross-reference is corrected for " + rule.MediaType + ":" + rule.ProviderMediaId + ".";
            if (rule.Kind == CorrectionKinds.PersonField)
                return rule.Provider.ToUpperInvariant() + " " + rule.FieldName + " is corrected for person " + rule.ProviderPersonId + ".";
            if (rule.Kind == CorrectionKinds.LocalPersonBinding)
                return "Emby person " + rule.EmbyId + " " + rule.Provider.ToUpperInvariant() + " binding is corrected.";
            if (rule.Kind == CorrectionKinds.LocalMediaBinding)
                return "Emby media " + rule.EmbyId + " " + rule.Provider.ToUpperInvariant() + " binding is corrected.";
            if (rule.Kind == CorrectionKinds.IdentityTarget)
                return rule.Provider.ToUpperInvariant() + " person " + rule.ProviderPersonId + " is assigned to " + rule.ReplacementValue + ".";
            if (rule.Kind == CorrectionKinds.LocalCreditTarget)
                return "Emby media " + rule.EmbyId + " credit " + rule.CurrentValue + " is assigned to " + rule.ReplacementValue + ".";
            return rule.Provider + ":" + rule.ProviderPersonId + " and " + rule.SecondaryProvider + ":" + rule.SecondaryId + " are explicitly " + rule.Operation + ".";
        }
        private static string Display(string preferred, string fallback) => string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;
    }

    public static class ProviderCorrectionOverlay
    {
        public static IReadOnlyList<string> ExplicitlyDiscreditedLocalBindings(ResolutionInput input, LocalPerson person)
        {
            if (input == null || person == null) return new string[0];
            var result = new List<string>();
            foreach (var provider in new[] { ProviderNames.Tmdb, ProviderNames.Tvdb })
            {
                var providerPersonId = GetPersonBinding(person, provider);
                if (string.IsNullOrWhiteSpace(providerPersonId)) continue;
                var exclusions = (input.ActiveCorrections ?? new List<ProviderCorrection>()).Where(x => x.Enabled && x.Kind == CorrectionKinds.MediaCredit && x.Operation == CorrectionOperations.Unusable && x.Provider == provider && x.ProviderPersonId == providerPersonId).ToList();
                if (exclusions.Count == 0) continue;
                var localMedia = new HashSet<long>((input.LocalCredits ?? new List<LocalCredit>()).Where(x => x.PersonEmbyId == person.EmbyId).Select(x => x.MediaEmbyId));
                var providerMediaIds = new HashSet<string>((input.Media ?? new List<MediaSeed>()).Where(x => localMedia.Contains(x.EmbyId)).Select(x => x.ProviderAcquisitionId(provider)).Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.Ordinal);
                if ((input.ProviderCredits ?? new List<ObservedProviderCredit>()).Any(x => x.Provider == provider && x.ProviderPersonId == providerPersonId && providerMediaIds.Contains(x.ProviderMediaId))) continue;
                if (exclusions.Any(x => (input.Media ?? new List<MediaSeed>()).Any(m => localMedia.Contains(m.EmbyId) && m.MediaType == x.MediaType && m.ProviderAcquisitionId(provider) == x.ProviderMediaId)))
                    result.Add(provider + ":" + providerPersonId);
            }
            return result;
        }

        public static void ApplyMediaIdentities(IList<ProviderMediaIdentity> media, CorrectionApplicationTracker tracker)
        {
            foreach (var rule in tracker.Rules.Where(x => x.Kind == CorrectionKinds.MediaExternalId))
            foreach (var record in media.Where(x => MatchMedia(rule, x.Provider, x.MediaType, x.ProviderMediaId)))
            {
                var matches = record.ExternalIds.Where(x => string.Equals(x.Provider, rule.FieldName, StringComparison.OrdinalIgnoreCase) && ValueMatches(rule.CurrentValue, x.Id)).ToList();
                if (matches.Count == 0 && rule.Operation == CorrectionOperations.Replace && string.IsNullOrWhiteSpace(rule.CurrentValue))
                {
                    record.ExternalIds.Add(new MediaExternalIdentity { Provider = rule.FieldName, Id = rule.ReplacementValue }); tracker.Match(rule); continue;
                }
                foreach (var match in matches)
                {
                    record.ExternalIds.Remove(match);
                    if (rule.Operation == CorrectionOperations.Replace && !record.ExternalIds.Any(x => x.Provider == rule.FieldName && x.Id == rule.ReplacementValue)) record.ExternalIds.Add(new MediaExternalIdentity { Provider = rule.FieldName, Id = rule.ReplacementValue });
                    tracker.Match(rule);
                }
            }
        }

        public static void Apply(ResolutionInput input, CorrectionApplicationTracker tracker)
        {
            ApplyLocalBindings(input, tracker);
            ApplyCredits(input, tracker);
            ApplyPeople(input, tracker);
            ApplyRelations(input, tracker);
            var effectiveKeys = new HashSet<string>(input.ProviderCredits.Select(x => x.PersonKey), StringComparer.Ordinal);
            input.ProviderPeople = input.ProviderPeople.Where(x => effectiveKeys.Contains(x.Key)).ToList();
            var byKey = input.ProviderPeople.ToDictionary(x => x.Key, StringComparer.Ordinal);
            foreach (var person in input.ProviderPeople) { person.Credits.Clear(); person.CanonicalMediaKeys.Clear(); }
            foreach (var credit in input.ProviderCredits)
            {
                if (!byKey.TryGetValue(credit.PersonKey, out var person)) continue;
                if (string.IsNullOrWhiteSpace(credit.PersonName)) credit.PersonName = person.Name;
                credit.CleanPersonName = TextNormalizer.PersonName(credit.PersonName);
                person.Credits.Add(credit); person.CanonicalMediaKeys.Add(credit.CanonicalMediaKey);
            }
        }

        private static void ApplyLocalBindings(ResolutionInput input, CorrectionApplicationTracker tracker)
        {
            foreach (var rule in tracker.Rules.Where(x => x.Kind == CorrectionKinds.LocalPersonBinding))
            foreach (var person in input.LocalPeople.Where(x => x.EmbyId == rule.EmbyId))
            {
                var before = GetPersonBinding(person, rule.Provider); var after = rule.Operation == CorrectionOperations.Replace ? rule.ReplacementValue : null;
                if (!ValueMatches(rule.CurrentValue, before)) continue;
                SetPersonBinding(person, rule.Provider, after); tracker.Match(rule, string.Equals(before, after, StringComparison.Ordinal) ? 0 : 1);
            }
            foreach (var rule in tracker.Rules.Where(x => x.Kind == CorrectionKinds.LocalMediaBinding))
            foreach (var media in input.Media.Where(x => x.EmbyId == rule.EmbyId))
            {
                var before = GetMediaBinding(media, rule.Provider); var after = rule.Operation == CorrectionOperations.Replace ? rule.ReplacementValue : null;
                if (!ValueMatches(rule.CurrentValue, before)) continue;
                SetMediaBinding(media, rule.Provider, after); tracker.Match(rule, string.Equals(before, after, StringComparison.Ordinal) ? 0 : 1);
            }
        }

        private static void ApplyCredits(ResolutionInput input, CorrectionApplicationTracker tracker)
        {
            foreach (var rule in tracker.Rules.Where(x => x.Kind == CorrectionKinds.MediaCredit || x.Kind == CorrectionKinds.MediaCreditRole))
            {
                var matches = input.ProviderCredits.Where(x => MatchMedia(rule, x.Provider, x.MediaType, x.ProviderMediaId) && x.ProviderPersonId == rule.ProviderPersonId && ValueMatches(rule.CurrentValue, x.Role)).ToList();
                foreach (var credit in matches)
                {
                    if (rule.Kind == CorrectionKinds.MediaCredit)
                    {
                        if (rule.Operation == CorrectionOperations.Unusable) input.ProviderCredits.Remove(credit);
                        else { credit.ProviderPersonId = rule.ReplacementValue; credit.PersonName = null; credit.CleanPersonName = null; }
                    }
                    else
                    {
                        if (rule.Operation == CorrectionOperations.Unusable) { credit.Role = "Unspecified role"; credit.RoleCategory = "Unknown"; credit.RoleName = null; }
                        else { credit.Role = rule.ReplacementValue; credit.RoleName = RoleName(rule.ReplacementValue); credit.RoleCategory = RoleCategory(rule.ReplacementValue); }
                    }
                    tracker.Match(rule);
                }
            }
        }

        private static void ApplyPeople(ResolutionInput input, CorrectionApplicationTracker tracker)
        {
            foreach (var rule in tracker.Rules.Where(x => x.Kind == CorrectionKinds.PersonField))
            foreach (var person in input.ProviderPeople.Where(x => x.Provider == rule.Provider && x.ProviderId == rule.ProviderPersonId))
            {
                var before = rule.FieldName == "birthday" ? person.Birthday : person.Name;
                if (!ValueMatches(rule.CurrentValue, before)) continue;
                var after = rule.Operation == CorrectionOperations.Replace ? rule.ReplacementValue : null;
                if (rule.FieldName == "birthday") person.Birthday = after;
                else { person.Name = after ?? string.Empty; person.CleanName = TextNormalizer.PersonName(after); }
                tracker.Match(rule, string.Equals(before, after, StringComparison.Ordinal) ? 0 : 1);
            }
            foreach (var rule in tracker.Rules.Where(x => x.Kind == CorrectionKinds.PersonExternalId))
            foreach (var person in input.ProviderPeople.Where(x => x.Provider == rule.Provider && x.ProviderId == rule.ProviderPersonId))
            {
                person.ExternalIds.TryGetValue(rule.FieldName, out var before);
                if (!ValueMatches(rule.CurrentValue, before)) continue;
                if (rule.Operation == CorrectionOperations.Unusable) person.ExternalIds.Remove(rule.FieldName); else person.ExternalIds[rule.FieldName] = rule.ReplacementValue;
                tracker.Match(rule, rule.Operation == CorrectionOperations.Unusable && before == null || before == rule.ReplacementValue ? 0 : 1);
            }
        }

        private static void ApplyRelations(ResolutionInput input, CorrectionApplicationTracker tracker)
        {
            var keys = new HashSet<string>(input.ProviderPeople.Select(x => x.Key), StringComparer.Ordinal);
            foreach (var rule in tracker.Rules.Where(x => x.Kind == CorrectionKinds.IdentityRelation))
            {
                var a = rule.Provider + ":" + rule.ProviderPersonId; var b = rule.SecondaryProvider + ":" + rule.SecondaryId;
                if (!keys.Contains(a) || !keys.Contains(b)) continue;
                input.Bridges.RemoveAll(x => SamePair(x.ProviderA + ":" + x.ProviderIdA, x.ProviderB + ":" + x.ProviderIdB, a, b));
                input.Bridges.Add(new ManualBridge { ProviderA = rule.Provider, ProviderIdA = rule.ProviderPersonId, ProviderB = rule.SecondaryProvider, ProviderIdB = rule.SecondaryId, IsRejected = rule.Operation == CorrectionOperations.Different });
                tracker.Match(rule);
            }
        }

        private static bool MatchMedia(ProviderCorrection rule, string provider, string type, string id) => rule.Provider == provider && rule.MediaType == type && rule.ProviderMediaId == id;
        private static bool ValueMatches(string expected, string actual) => string.IsNullOrWhiteSpace(expected) || string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
        private static bool SamePair(string a, string b, string c, string d) => a == c && b == d || a == d && b == c;
        private static string GetPersonBinding(LocalPerson x, string provider) => provider == ProviderNames.Tmdb ? x.TmdbId : provider == ProviderNames.Tvdb ? x.TvdbId : x.ImdbId;
        private static void SetPersonBinding(LocalPerson x, string provider, string value) { if (provider == ProviderNames.Tmdb) x.TmdbId = value; else if (provider == ProviderNames.Tvdb) x.TvdbId = value; else x.ImdbId = value; }
        private static string GetMediaBinding(MediaSeed x, string provider) => provider == ProviderNames.Tmdb ? x.TmdbId : x.TvdbId;
        private static void SetMediaBinding(MediaSeed x, string provider, string value)
        {
            if (provider == ProviderNames.Tmdb) { x.TmdbId = value; x.TmdbAcquisitionId = value; }
            else { x.TvdbId = value; x.TvdbAcquisitionId = value; }
        }
        private static string RoleName(string role) { var i = (role ?? string.Empty).IndexOf(':'); return i < 0 ? role : role.Substring(i + 1).Trim(); }
        private static string RoleCategory(string role)
        {
            var value = role ?? string.Empty;
            if (value.IndexOf("Director", StringComparison.OrdinalIgnoreCase) >= 0) return "Director";
            if (value.IndexOf("Writer", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf("Screenplay", StringComparison.OrdinalIgnoreCase) >= 0) return "Writer";
            if (value.IndexOf("Producer", StringComparison.OrdinalIgnoreCase) >= 0) return "Producer";
            if (value.IndexOf("Creator", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf("Showrunner", StringComparison.OrdinalIgnoreCase) >= 0) return "Creator";
            return value.StartsWith("Actor", StringComparison.OrdinalIgnoreCase) || value.StartsWith("Guest Star", StringComparison.OrdinalIgnoreCase) ? "Actor" : "Unknown";
        }
    }
}
