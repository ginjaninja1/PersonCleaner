using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PersonCleaner.V2.Domain
{
    public sealed class ReviewLiveCredit
    {
        public long PersonEmbyId { get; set; }
        public long MediaEmbyId { get; set; }
        public string MediaType { get; set; }
        public string MediaName { get; set; }
        public string Role { get; set; }
    }

    public static class ReviewCaseCreditInventory
    {
        public static List<IdentityCreditOutcome> Missing(IdentityCasePlan plan, IEnumerable<ReviewLiveCredit> liveCredits)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            var people = new HashSet<long>(plan.CurrentPeople.Select(x => x.EmbyId));
            var existing = new HashSet<string>(plan.Credits.Select(x => Key(x.SourcePersonEmbyId, x.MediaEmbyId, x.Role)), StringComparer.Ordinal);
            var result = new List<IdentityCreditOutcome>();
            foreach (var row in (liveCredits ?? Enumerable.Empty<ReviewLiveCredit>())
                .Where(x => x != null && people.Contains(x.PersonEmbyId) && x.MediaEmbyId > 0)
                .GroupBy(x => Key(x.PersonEmbyId, x.MediaEmbyId, x.Role), StringComparer.Ordinal).Select(x => x.First())
                .OrderBy(x => x.MediaName, StringComparer.Ordinal).ThenBy(x => x.MediaEmbyId).ThenBy(x => x.Role, StringComparer.Ordinal).ThenBy(x => x.PersonEmbyId))
            {
                var key = Key(row.PersonEmbyId, row.MediaEmbyId, row.Role);
                if (!existing.Add(key)) continue;
                var target = plan.Outcomes.FirstOrDefault(x => x.TargetKind == IdentityTargetKinds.Existing && x.TargetEmbyId == row.PersonEmbyId)
                    ?? plan.Outcomes.FirstOrDefault(x => x.SourceEmbyIds.Contains(row.PersonEmbyId));
                if (target == null) continue;
                result.Add(new IdentityCreditOutcome
                {
                    AssignmentId = "review-live-" + StableHash(key),
                    SourcePersonEmbyId = row.PersonEmbyId,
                    TargetOutcomeId = target.OutcomeId,
                    MediaEmbyId = row.MediaEmbyId,
                    MediaType = string.IsNullOrWhiteSpace(row.MediaType) ? "media" : row.MediaType,
                    MediaName = string.IsNullOrWhiteSpace(row.MediaName) ? "Emby media " + row.MediaEmbyId.ToString(CultureInfo.InvariantCulture) : row.MediaName,
                    Role = row.Role ?? string.Empty,
                    Disposition = "KEEP",
                    Rationale = "Loaded from live Emby when the case review opened; this relationship was outside the gathered evidence scope.",
                    IsReviewSupplemental = true
                });
            }
            return result;
        }

        private static string Key(long personId, long mediaId, string role) => personId.ToString(CultureInfo.InvariantCulture) + "\n" + mediaId.ToString(CultureInfo.InvariantCulture) + "\n" + (role ?? string.Empty);
        private static string StableHash(string value)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)).Take(8).Select(x => x.ToString("x2", CultureInfo.InvariantCulture)));
        }
    }
}
