using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PersonCleaner.V2.Storage
{
    internal sealed class RawPayloadCache
    {
        private readonly string root;
        public RawPayloadCache(string root) { this.root = root; Directory.CreateDirectory(root); }

        public string RelativePath(QueueItem item)
        {
            var safeId = new string((item.ProviderId ?? string.Empty).Select(x => char.IsLetterOrDigit(x) || x == '-' || x == '_' ? x : '_').ToArray());
            return Path.Combine(item.Provider, item.EntityType, string.IsNullOrWhiteSpace(item.MediaType) ? "person" : item.MediaType, safeId + ".json");
        }

        public bool Exists(string relativePath) => !string.IsNullOrWhiteSpace(relativePath) && File.Exists(Path.Combine(root, relativePath));
        public string Read(string relativePath) => File.ReadAllText(Path.Combine(root, relativePath), Encoding.UTF8);

        public void Write(string relativePath, string payload)
        {
            var path = Path.Combine(root, relativePath); var directory = Path.GetDirectoryName(path); Directory.CreateDirectory(directory);
            var temporary = path + ".new"; File.WriteAllText(temporary, payload, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }

        public static string Hash(string payload)
        {
            using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(payload ?? string.Empty)).Select(x => x.ToString("x2")));
        }
    }
}
