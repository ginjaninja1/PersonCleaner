using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace PersonCleaner.V2.Tasks
{
    /// <summary>Manual-only audit and cleanup of Emby's person metadata directory.</summary>
    public sealed class MetadataFolderCleanerTask : IScheduledTask
    {
        private static readonly PersonType[] AuditedRoles = { PersonType.Actor, PersonType.GuestStar, PersonType.Director, PersonType.Writer, PersonType.Producer };
        private static readonly HashSet<string> ImageExtensions = new HashSet<string>(new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp" }, StringComparer.OrdinalIgnoreCase);
        private readonly ILibraryManager library;
        private readonly ILogger logger;
        private readonly string peopleMetadataRoot;

        public MetadataFolderCleanerTask(ILibraryManager library, IApplicationPaths paths, ILogManager logs)
        {
            this.library = library;
            logger = logs.GetLogger("PersonCleaner metadata folder cleaner");
            peopleMetadataRoot = Path.Combine(paths.ProgramDataPath, "metadata", "people");
        }

        public string Name => "Person cleaner - Metadata folder cleaner";
        public string Key => "PersonCleanerMetadataFolderCleaner";
        public string Description => "Manual-only: audits Emby person IDs, NFOs, images and folder naming, and optionally removes unowned person metadata folders.";
        public string Category => "GinjaNinja Tools";
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var testMode = Plugin.Instance.Configuration.MetadataFolderCleanerTestMode;
            var root = Canonical(peopleMetadataRoot);
            logger.Info("PersonCleaner Metadata Folder Cleaner starting: TestMode={0}; people metadata root={1}.", testMode, root);

            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException("Emby people metadata directory does not exist: " + root);

            var people = library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { typeof(Person).Name },
                Recursive = true
            }, cancellationToken).OfType<Person>().OrderBy(x => x.InternalId).ToList();

            // Every Emby Person protects its folder from deletion, but only people
            // credited in the configured identity-resolution roles are audited.
            // This deliberately excludes Artist- and Composer-only people.
            var scopedPersonIds = AuditedPersonIds(cancellationToken);
            var auditedPeople = people.Where(x => scopedPersonIds.Contains(x.InternalId)).ToList();
            var ownedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var person in people)
            {
                var folder = MetadataFolder(person);
                if (!string.IsNullOrWhiteSpace(folder)) ownedFolders.Add(Canonical(folder));
            }
            logger.Info("PersonCleaner Metadata Folder Cleaner scope: all Emby persons={0}; audited Actor/GuestStar/Director/Writer/Producer persons={1}; non-scope persons protected but not logged={2}.", people.Count, auditedPeople.Count, people.Count - auditedPeople.Count);
            var wrongTmdbFolders = 0;
            for (var index = 0; index < auditedPeople.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var person = auditedPeople[index];
                var tmdb = ProviderId(person, MetadataProviders.Tmdb);
                var tvdb = ProviderId(person, MetadataProviders.Tvdb);
                var imdb = ProviderId(person, MetadataProviders.Imdb);
                var folder = MetadataFolder(person);
                var folderType = FolderType(person.Name, tmdb, folder);
                var imageType = ImageTypeFor(person);
                var nfoMatch = NfoMatch(folder, tmdb, tvdb, imdb, out var nfoDetail);

                logger.Info("PersonCleaner Metadata Folder Person: Name={0}; EmbyID={1}; TMDBID={2}; TVDBID={3}; IMDBID={4}; FolderPath={5}; FolderType={6}; Image={7}; NfoIdsMatch={8}; NfoDetail={9}.",
                    Value(person.Name), person.InternalId, Value(tmdb), Value(tvdb), Value(imdb), Value(folder), folderType, imageType, nfoMatch, nfoDetail);

                if (!string.IsNullOrWhiteSpace(tmdb) && !string.Equals(folderType, "FolderTMDB", StringComparison.Ordinal))
                {
                    wrongTmdbFolders++;
                    logger.Warn("PersonCleaner Metadata Folder TMDB mismatch: Name={0}; EmbyID={1}; FolderPath={2}; expected FolderTMDB={3}.", Value(person.Name), person.InternalId, Value(folder), (person.Name ?? string.Empty) + "-tmdb-" + tmdb);
                }
                progress.Report(auditedPeople.Count == 0 ? 50 : 70.0 * (index + 1) / auditedPeople.Count);
            }

            var folders = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            var orphanCount = 0;
            var deletedCount = 0;
            for (var index = 0; index < folders.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var folder = Canonical(folders[index]);
                if (ownedFolders.Contains(folder)) continue;
                orphanCount++;
                var info = new DirectoryInfo(folder);
                var nfoPath = Path.Combine(folder, "person.nfo");
                var nfo = File.Exists(nfoPath) ? new FileInfo(nfoPath) : null;
                var imagePresent = HasLocalImage(folder);
                logger.Debug("PersonCleaner Metadata Folder Orphan: FolderName={0}; FolderType={1}; LocalImagePresent={2}; DateCreated={3:o}; DateModified={4:o}; DateAccessed={5:o}; Person.NFO Present={6}; NfoDateCreated={7}; NfoDateModified={8}; NfoDateAccessed={9}.",
                    info.Name, FolderTypeFromName(info.Name), Lower(imagePresent), info.CreationTimeUtc, info.LastWriteTimeUtc, info.LastAccessTimeUtc, Lower(nfo != null), Date(nfo, x => x.CreationTimeUtc), Date(nfo, x => x.LastWriteTimeUtc), Date(nfo, x => x.LastAccessTimeUtc));

                if (!testMode)
                {
                    RequireDirectChild(root, folder);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        logger.Warn("PersonCleaner Metadata Folder Cleaner refused to delete orphan reparse-point folder: {0}.", folder);
                        continue;
                    }
                    Directory.Delete(folder, true);
                    deletedCount++;
                    logger.Info("PersonCleaner Metadata Folder Cleaner deleted confirmed orphan direct-child folder: {0}.", folder);
                }
                progress.Report(70 + (folders.Count == 0 ? 30 : 30.0 * (index + 1) / folders.Count));
            }

            progress.Report(100);
            logger.Info("PersonCleaner Metadata Folder Cleaner finished: TestMode={0}; audited persons={1}; all protected Emby persons={2}; TMDB folder mismatches={3}; owned folders={4}; orphan folders={5}; deleted folders={6}.", testMode, auditedPeople.Count, people.Count, wrongTmdbFolders, ownedFolders.Count, orphanCount, deletedCount);
            return Task.CompletedTask;
        }

        private HashSet<long> AuditedPersonIds(CancellationToken cancellationToken)
        {
            var result = new HashSet<long>();
            var itemIds = library.GetItemList(new InternalItemsQuery { Recursive = true }, cancellationToken)
                .Where(x => x.SupportsPeople).Select(x => x.InternalId).Distinct().ToArray();
            for (var offset = 0; offset < itemIds.Length; offset += 250)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rows = library.GetItemPeople(new InternalPeopleQuery
                {
                    ItemIds = itemIds.Skip(offset).Take(250).ToArray(),
                    PersonTypes = AuditedRoles,
                    EnableIds = true,
                    EnableProviderIds = false,
                    EnableGroupByName = false
                });
                foreach (var row in rows.Where(x => x.Id > 0)) result.Add(row.Id);
            }
            return result;
        }

        private static string MetadataFolder(Person person)
        {
            string path = null;
            try { path = person.GetInternalMetadataPath(); } catch { }
            if (string.IsNullOrWhiteSpace(path)) path = person.ContainingFolderPath;
            if (string.IsNullOrWhiteSpace(path)) path = person.Path;
            if (string.IsNullOrWhiteSpace(path)) return null;
            return File.Exists(path) ? Path.GetDirectoryName(path) : path;
        }

        private static string FolderType(string name, string tmdb, string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return "FolderBlank";
            var leaf = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(tmdb) && string.Equals(leaf, (name ?? string.Empty) + "-tmdb-" + tmdb, StringComparison.OrdinalIgnoreCase)) return "FolderTMDB";
            if (string.Equals(leaf, name ?? string.Empty, StringComparison.OrdinalIgnoreCase)) return "FolderVanilla";
            return "FolderOther";
        }

        private static string FolderTypeFromName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "FolderBlank";
            return name.IndexOf("-tmdb-", StringComparison.OrdinalIgnoreCase) >= 0 ? "FolderTMDB" : "FolderVanilla";
        }

        private static string ImageTypeFor(Person person)
        {
            try
            {
                var image = person.HasImage(MediaBrowser.Model.Entities.ImageType.Primary, 0) ? person.GetImageInfo(MediaBrowser.Model.Entities.ImageType.Primary, 0)?.Path : null;
                if (string.IsNullOrWhiteSpace(image)) return "blank";
                return Uri.TryCreate(image, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) ? "URL" : "Local file";
            }
            catch { return "blank"; }
        }

        private static bool NfoMatch(string folder, string tmdb, string tvdb, string imdb, out string detail)
        {
            var expected = Ids(tmdb, tvdb, imdb);
            var nfoPath = string.IsNullOrWhiteSpace(folder) ? null : Path.Combine(folder, "person.nfo");
            if (string.IsNullOrWhiteSpace(nfoPath) || !File.Exists(nfoPath)) { detail = "person.nfo absent"; return false; }
            try
            {
                var actual = ReadNfoIds(nfoPath);
                detail = "expected=" + IdText(expected) + "; actual=" + IdText(actual);
                return expected.Count == actual.Count && expected.All(x => actual.TryGetValue(x.Key, out var value) && string.Equals(value, x.Value, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) { detail = "person.nfo unreadable: " + ex.Message; return false; }
        }

        private static Dictionary<string, string> ReadNfoIds(string path)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            XDocument document;
            using (var reader = XmlReader.Create(path, settings)) document = XDocument.Load(reader, LoadOptions.None);
            foreach (var element in document.Descendants())
            {
                var local = element.Name.LocalName;
                var provider = string.Equals(local, "tmdbid", StringComparison.OrdinalIgnoreCase) ? "TMDB" : string.Equals(local, "tvdbid", StringComparison.OrdinalIgnoreCase) ? "TVDB" : string.Equals(local, "imdbid", StringComparison.OrdinalIgnoreCase) ? "IMDB" : string.Equals(local, "uniqueid", StringComparison.OrdinalIgnoreCase) ? (string)element.Attribute("type") : null;
                var value = (element.Value ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(provider) && !string.IsNullOrWhiteSpace(value) && (string.Equals(provider, "TMDB", StringComparison.OrdinalIgnoreCase) || string.Equals(provider, "TVDB", StringComparison.OrdinalIgnoreCase) || string.Equals(provider, "IMDB", StringComparison.OrdinalIgnoreCase))) result[provider.ToUpperInvariant()] = value;
            }
            return result;
        }

        private static Dictionary<string, string> Ids(string tmdb, string tvdb, string imdb)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(tmdb)) result["TMDB"] = tmdb;
            if (!string.IsNullOrWhiteSpace(tvdb)) result["TVDB"] = tvdb;
            if (!string.IsNullOrWhiteSpace(imdb)) result["IMDB"] = imdb;
            return result;
        }

        private static string ProviderId(BaseItem item, MetadataProviders provider)
        {
            var key = provider.ToString();
            var found = item.ProviderIds?.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
            return string.IsNullOrWhiteSpace(found?.Value) ? item.GetProviderId(provider) : found.Value.Value;
        }

        private static bool HasLocalImage(string folder)
        {
            try { return Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly).Any(x => ImageExtensions.Contains(Path.GetExtension(x))); }
            catch { return false; }
        }

        private static void RequireDirectChild(string root, string folder)
        {
            var parent = Canonical(Path.GetDirectoryName(folder));
            if (!string.Equals(parent, Canonical(root), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing to delete a folder that is not a direct child of the Emby people metadata root: " + folder);
        }

        private static string Canonical(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        private static string IdText(IDictionary<string, string> ids) => ids.Count == 0 ? "none" : string.Join(",", ids.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + "=" + x.Value));
        private static string Lower(bool value) => value ? "true" : "false";
        private static string Date(FileInfo info, Func<FileInfo, DateTime> selector) => info == null ? "blank" : selector(info).ToString("o");
        private static string Value(string value) => string.IsNullOrWhiteSpace(value) ? "blank" : value;
    }
}
