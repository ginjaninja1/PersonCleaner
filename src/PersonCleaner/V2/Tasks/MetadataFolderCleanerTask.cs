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
            // Keep task output under the same logger/category as the plugin.
            logger = Plugin.Instance?.Logger ?? logs.GetLogger("PersonCleaner");
            peopleMetadataRoot = Path.Combine(paths.ProgramDataPath, "metadata", "people");
        }

        public string Name => "Person Cleaner - Metadata folder cleaner";
        public string Key => "PersonCleanerMetadataFolderCleaner";
        public string Description => "Backup database and \\programdata\\metadata\\people folder until tested or use a test server first.";
        public string Category => "GinjaNinja Tools";
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var testMode = Plugin.Instance.Configuration.MetadataFolderCleanerTestMode;
            var root = Canonical(peopleMetadataRoot);
            Plugin.LogHeading(logger, "Metadata folder cleaner");
            logger.Info("Metadata Folder Cleaner starting: TestMode={0}; people metadata root={1}.", testMode, root);

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
            Plugin.LogHeading(logger, "Live Emby person-folder status");
            var ownedFolders = LivePersonFolders(people);
            var liveFolders = people.Select(person => new { Person = person, Folder = MetadataFolder(person) })
                .Where(x => !string.IsNullOrWhiteSpace(x.Folder))
                .GroupBy(x => Canonical(x.Folder), StringComparer.OrdinalIgnoreCase)
                .ToList();
            var nameTmdbFolders = liveFolders.Count(group => group.Any(x => string.Equals(FolderType(x.Person.Name, ProviderId(x.Person, MetadataProviders.Tmdb), x.Folder), "FolderTMDB", StringComparison.Ordinal)));
            var nameFolders = liveFolders.Count(group => !group.Any(x => string.Equals(FolderType(x.Person.Name, ProviderId(x.Person, MetadataProviders.Tmdb), x.Folder), "FolderTMDB", StringComparison.Ordinal)) && group.Any(x => string.Equals(FolderType(x.Person.Name, ProviderId(x.Person, MetadataProviders.Tmdb), x.Folder), "FolderVanilla", StringComparison.Ordinal)));
            logger.Info("Live Emby person folders: people={0}; name-tmdb-id folders={1}; name folders={2}; other folders={3}.", people.Count, nameTmdbFolders, nameFolders, ownedFolders.Count - nameTmdbFolders - nameFolders);
            foreach (var duplicate in liveFolders.Where(x => x.Count() > 1))
                logger.Warn("Multiple Emby persons share metadata folder: FolderPath={0}; EmbyPersonIDs={1}; Names={2}.", duplicate.Key, string.Join(",", duplicate.Select(x => x.Person.InternalId)), string.Join(" | ", duplicate.Select(x => Value(x.Person.Name))));

            Plugin.LogHeading(logger, "Orphan metadata folder removal");
            var folders = PersonMetadataFolders(root);
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
                logger.Debug("Orphan metadata folder: FolderName={0}; FolderType={1}; LocalImagePresent={2}; DateCreated={3:o}; DateModified={4:o}; DateAccessed={5:o}; Person.NFO Present={6}; NfoDateCreated={7}; NfoDateModified={8}; NfoDateAccessed={9}.",
                    info.Name, FolderTypeFromName(info.Name), Lower(imagePresent), info.CreationTimeUtc, info.LastWriteTimeUtc, info.LastAccessTimeUtc, Lower(nfo != null), Date(nfo, x => x.CreationTimeUtc), Date(nfo, x => x.LastWriteTimeUtc), Date(nfo, x => x.LastAccessTimeUtc));

                if (!testMode)
                {
                    RequirePersonFolderLayout(root, folder);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        logger.Warn("Metadata Folder Cleaner refused to delete orphan reparse-point folder: {0}.", folder);
                        continue;
                    }
                    Directory.Delete(folder, true);
                    deletedCount++;
                    logger.Info("Orphaned Folder Removed: {0}.", folder);
                }
                progress.Report(70 + (folders.Count == 0 ? 30 : 30.0 * (index + 1) / folders.Count));
            }

            progress.Report(100);
            logger.Info("Metadata Folder Cleaner finished: TestMode={0}; audited persons={1}; all protected Emby persons={2}; owned folders={3}; orphan folders={4}; deleted folders={5}.", testMode, auditedPeople.Count, people.Count, ownedFolders.Count, orphanCount, deletedCount);
            return Task.CompletedTask;
        }

        private HashSet<long> AuditedPersonIds(CancellationToken cancellationToken)
        {
            Plugin.LogHeading(logger, "Audited-person scope");
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

        /// <summary>Enumerates person folders in both Emby layouts: people\name and people\x\name.</summary>
        private static List<string> PersonMetadataFolders(string root)
        {
            var folders = new List<string>();
            foreach (var directChild in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
            {
                var isShard = IsAlphabetShard(directChild) && Directory.EnumerateDirectories(directChild, "*", SearchOption.TopDirectoryOnly).Any();
                if (!isShard) folders.Add(Canonical(directChild));
                if (IsAlphabetShard(directChild)) folders.AddRange(Directory.EnumerateDirectories(directChild, "*", SearchOption.TopDirectoryOnly).Select(Canonical));
            }
            return folders.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static HashSet<string> LivePersonFolders(IEnumerable<Person> people)
        {
            return new HashSet<string>(people.Select(MetadataFolder).Where(x => !string.IsNullOrWhiteSpace(x)).Select(Canonical), StringComparer.OrdinalIgnoreCase);
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

        private static void RequirePersonFolderLayout(string root, string folder)
        {
            var parent = Canonical(Path.GetDirectoryName(folder));
            var canonicalRoot = Canonical(root);
            if (string.Equals(parent, canonicalRoot, StringComparison.OrdinalIgnoreCase)) return;
            if (IsAlphabetShard(parent) && string.Equals(Canonical(Path.GetDirectoryName(parent)), canonicalRoot, StringComparison.OrdinalIgnoreCase)) return;
            throw new InvalidOperationException("Refusing to delete a folder outside a supported Emby people metadata layout: " + folder);
        }

        private static bool IsAlphabetShard(string path)
        {
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return name.Length == 1 && ((name[0] >= 'A' && name[0] <= 'Z') || (name[0] >= 'a' && name[0] <= 'z'));
        }

        private static string Canonical(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        private static string IdText(IDictionary<string, string> ids) => ids.Count == 0 ? "none" : string.Join(",", ids.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + "=" + x.Value));
        private static string Lower(bool value) => value ? "true" : "false";
        private static string Date(FileInfo info, Func<FileInfo, DateTime> selector) => info == null ? "blank" : selector(info).ToString("o");
        private static string Value(string value) => string.IsNullOrWhiteSpace(value) ? "blank" : value;
    }
}
