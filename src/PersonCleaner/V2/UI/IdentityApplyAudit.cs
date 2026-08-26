using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using PersonCleaner.V2.Domain;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace PersonCleaner.V2.UI
{
    internal static class IdentityApplyAudit
    {
        public static Dictionary<long, PersonMetadataSnapshot> CaptureBefore(IdentityCasePlan plan, ILibraryManager library)
        {
            return Capture(plan.CurrentPeople.Select(x => x.EmbyId), library);
        }

        public static void Log(IdentityCasePlan plan, IdentityCaseApplyReceipt receipt, Dictionary<long, PersonMetadataSnapshot> before, ILibraryManager library, ILogger logger)
        {
            try
            {
                var outcomeNames = plan.Outcomes.ToDictionary(x => x.OutcomeId, x => x.DisplayName ?? "(unnamed)", StringComparer.Ordinal);
                var allIds = new HashSet<long>((before ?? new Dictionary<long, PersonMetadataSnapshot>()).Keys);
                foreach (var id in receipt.OutcomeEmbyIds.Values) allIds.Add(id);
                var after = Capture(allIds, library);

                foreach (var outcome in plan.Outcomes.Where(x => x.TargetKind == IdentityTargetKinds.New))
                    logger.Info("PersonCleaner Apply case {0}: Emby {1}|ID=New: before metadata: person does not yet exist; metadata folder, person.nfo, provider IDs and folder image are not applicable.", plan.CaseId, outcome.DisplayName ?? "(unnamed)");

                foreach (var snapshot in (before ?? new Dictionary<long, PersonMetadataSnapshot>()).Values.OrderBy(x => x.EmbyId))
                    logger.Info("PersonCleaner Apply case {0}: Emby {1}|ID={2}: before metadata: {3}", plan.CaseId, snapshot.Name, snapshot.EmbyId, snapshot.Describe());

                foreach (var change in receipt.Changes)
                {
                    var source = Label(change.SourceEmbyId, before, after);
                    var target = Label(change.TargetEmbyId, before, after, change.OutcomeId == null ? null : outcomeNames.TryGetValue(change.OutcomeId, out var name) ? name : null);
                    if (string.Equals(change.Kind, "person-provider-id", StringComparison.Ordinal))
                    {
                        logger.Info("PersonCleaner Apply case {0}: Emby {1}: provider-ID change {2} {3} -> {4}.", plan.CaseId, target, (change.Provider ?? "unknown").ToUpperInvariant(), Value(change.OldValue), Value(change.NewValue));
                    }
                    else if (string.Equals(change.Kind, "move-credit", StringComparison.Ordinal))
                    {
                        var credit = plan.Credits.FirstOrDefault(x => x.MediaEmbyId == change.MediaEmbyId && x.SourcePersonEmbyId == change.SourceEmbyId && string.Equals(x.Role, change.Role, StringComparison.Ordinal));
                        logger.Info("PersonCleaner Apply case {0}: media-attribution change {1}|ID={2}; role={3}; Emby source {4} -> target {5}.", plan.CaseId, credit?.MediaName ?? "(unknown media)", change.MediaEmbyId, change.Role ?? "(none)", source, target);
                    }
                    else if (string.Equals(change.Kind, "create-person", StringComparison.Ordinal))
                    {
                        logger.Info("PersonCleaner Apply case {0}: Emby {1}: created provider-identified person.", plan.CaseId, target);
                    }
                    else
                    {
                        logger.Info("PersonCleaner Apply case {0}: Emby source {1}; target {2}: {3}", plan.CaseId, source, target, change.Summary ?? change.Kind ?? "change");
                    }
                }

                foreach (var snapshot in after.Values.OrderBy(x => x.EmbyId))
                    logger.Info("PersonCleaner Apply case {0}: Emby {1}|ID={2}: after metadata: {3}", plan.CaseId, snapshot.Name, snapshot.EmbyId, snapshot.Describe());
            }
            catch (Exception ex)
            {
                logger.ErrorException("PersonCleaner committed identity case " + plan.CaseId + " but could not complete its metadata-folder audit log", ex);
            }
        }

        private static Dictionary<long, PersonMetadataSnapshot> Capture(IEnumerable<long> ids, ILibraryManager library)
        {
            var result = new Dictionary<long, PersonMetadataSnapshot>();
            foreach (var id in (ids ?? Enumerable.Empty<long>()).Distinct().OrderBy(x => x))
            {
                var person = library.GetItemById(id) as Person;
                if (person != null) result[id] = PersonMetadataSnapshot.Capture(person);
            }
            return result;
        }

        private static string Label(long? id, IDictionary<long, PersonMetadataSnapshot> before, IDictionary<long, PersonMetadataSnapshot> after, string fallbackName = null)
        {
            if (!id.HasValue) return (fallbackName ?? "(unknown)") + "|ID=New";
            PersonMetadataSnapshot snapshot;
            if (after != null && after.TryGetValue(id.Value, out snapshot)) return snapshot.Name + "|ID=" + id.Value;
            if (before != null && before.TryGetValue(id.Value, out snapshot)) return snapshot.Name + "|ID=" + id.Value;
            return (fallbackName ?? "(unknown)") + "|ID=" + id.Value;
        }

        private static string Value(string value) => string.IsNullOrWhiteSpace(value) ? "(blank)" : value;
    }

    internal sealed class PersonMetadataSnapshot
    {
        private static readonly HashSet<string> ImageExtensions = new HashSet<string>(new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp" }, StringComparer.OrdinalIgnoreCase);

        public long EmbyId { get; private set; }
        public string Name { get; private set; }
        public string ItemPath { get; private set; }
        public string InternalMetadataPath { get; private set; }
        public string MetadataFolder { get; private set; }
        public string NfoState { get; private set; }
        public string NfoProviderIds { get; private set; }
        public string FolderImages { get; private set; }
        public string EmbyPrimaryImage { get; private set; }
        public string ImageRelationship { get; private set; }
        public string Error { get; private set; }

        public static PersonMetadataSnapshot Capture(Person person)
        {
            var snapshot = new PersonMetadataSnapshot { EmbyId = person.InternalId, Name = person.Name ?? "(unnamed)", ItemPath = person.Path };
            try { snapshot.InternalMetadataPath = person.GetInternalMetadataPath(); }
            catch (Exception ex) { snapshot.Error = "GetInternalMetadataPath failed: " + ex.Message; }
            snapshot.MetadataFolder = ResolveFolder(snapshot.InternalMetadataPath, person.ContainingFolderPath, snapshot.ItemPath);

            string primaryPath = null;
            try
            {
                var primary = person.HasImage(ImageType.Primary, 0) ? person.GetImageInfo(ImageType.Primary, 0) : null;
                primaryPath = primary?.Path;
            }
            catch (Exception ex) { snapshot.Error = Append(snapshot.Error, "primary image lookup failed: " + ex.Message); }
            snapshot.EmbyPrimaryImage = primaryPath;

            if (string.IsNullOrWhiteSpace(snapshot.MetadataFolder) || !Directory.Exists(snapshot.MetadataFolder))
            {
                snapshot.NfoState = "absent (metadata folder unavailable)";
                snapshot.NfoProviderIds = "none";
                snapshot.FolderImages = "none";
                snapshot.ImageRelationship = "unavailable";
                return snapshot;
            }

            var nfo = Path.Combine(snapshot.MetadataFolder, "person.nfo");
            snapshot.NfoState = File.Exists(nfo) ? "present" : "absent";
            string nfoError = null;
            snapshot.NfoProviderIds = File.Exists(nfo) ? ReadProviderIds(nfo, out nfoError) : "none";
            if (!string.IsNullOrWhiteSpace(nfoError)) snapshot.Error = Append(snapshot.Error, nfoError);

            string[] images;
            try
            {
                images = Directory.EnumerateFiles(snapshot.MetadataFolder)
                    .Where(x => string.Equals(Path.GetFileNameWithoutExtension(x), "folder", StringComparison.OrdinalIgnoreCase) && ImageExtensions.Contains(Path.GetExtension(x)))
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch (Exception ex)
            {
                images = new string[0];
                snapshot.Error = Append(snapshot.Error, "folder image scan failed: " + ex.Message);
            }
            snapshot.FolderImages = images.Length == 0 ? "none" : string.Join(", ", images.Select(Path.GetFileName));
            snapshot.ImageRelationship = CompareImage(primaryPath, images);
            return snapshot;
        }

        public string Describe()
        {
            return "metadata folder=" + Value(MetadataFolder) + "; item path=" + Value(ItemPath) + "; internal metadata path=" + Value(InternalMetadataPath) + "; person.nfo=" + (NfoState ?? "unknown") + "; NFO IDs=" + (NfoProviderIds ?? "none") + "; folder image=" + (FolderImages ?? "none") + "; Emby primary image=" + Value(EmbyPrimaryImage) + "; image relationship=" + (ImageRelationship ?? "unknown") + (string.IsNullOrWhiteSpace(Error) ? string.Empty : "; audit warning=" + Error) + ".";
        }

        private static string ResolveFolder(params string[] candidates)
        {
            foreach (var candidate in candidates.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                try
                {
                    if (Directory.Exists(candidate)) return candidate;
                    if (File.Exists(candidate)) return Path.GetDirectoryName(candidate);
                }
                catch { }
            }
            return candidates.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        }

        private static string ReadProviderIds(string nfo, out string error)
        {
            error = null;
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                XDocument document;
                using (var reader = XmlReader.Create(nfo, settings)) document = XDocument.Load(reader, LoadOptions.None);
                var ids = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var element in document.Descendants())
                {
                    var local = element.Name.LocalName;
                    string provider = null;
                    if (string.Equals(local, "tmdbid", StringComparison.OrdinalIgnoreCase)) provider = "TMDB";
                    else if (string.Equals(local, "tvdbid", StringComparison.OrdinalIgnoreCase)) provider = "TVDB";
                    else if (string.Equals(local, "imdbid", StringComparison.OrdinalIgnoreCase)) provider = "IMDB";
                    else if (string.Equals(local, "uniqueid", StringComparison.OrdinalIgnoreCase)) provider = (string)element.Attribute("type");
                    var value = (element.Value ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(value)) continue;
                    if (!ids.TryGetValue(provider.ToUpperInvariant(), out var values)) ids[provider.ToUpperInvariant()] = values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    values.Add(value);
                }
                return ids.Count == 0 ? "none" : string.Join(", ", ids.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + "=" + string.Join("|", x.Value.OrderBy(y => y, StringComparer.OrdinalIgnoreCase))));
            }
            catch (Exception ex)
            {
                error = "person.nfo parse failed: " + ex.Message;
                return "unreadable";
            }
        }

        private static string CompareImage(string primaryPath, string[] folderImages)
        {
            if (string.IsNullOrWhiteSpace(primaryPath)) return folderImages.Length == 0 ? "neither image exists" : "folder image exists; Emby primary is absent";
            if (folderImages.Length == 0) return "Emby primary exists; folder image is absent";
            foreach (var image in folderImages)
                if (SamePath(primaryPath, image)) return "same file";
            if (!File.Exists(primaryPath)) return "Emby primary path is not a readable local file";
            try
            {
                var primaryHash = Hash(primaryPath);
                foreach (var image in folderImages) if (primaryHash.SequenceEqual(Hash(image))) return "different paths; file content matches";
                return "different image content";
            }
            catch (Exception ex) { return "comparison unavailable: " + ex.Message; }
        }

        private static byte[] Hash(string path)
        {
            using (var algorithm = SHA256.Create())
            using (var stream = File.OpenRead(path)) return algorithm.ComputeHash(stream);
        }

        private static bool SamePath(string left, string right)
        {
            try { return string.Equals(Path.GetFullPath(left).TrimEnd('\\', '/'), Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(left, right, StringComparison.OrdinalIgnoreCase); }
        }

        private static string Append(string existing, string value) => string.IsNullOrWhiteSpace(existing) ? value : existing + "; " + value;
        private static string Value(string value) => string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }
}
