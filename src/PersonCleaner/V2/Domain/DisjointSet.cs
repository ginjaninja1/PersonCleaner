using System;
using System.Collections.Generic;

namespace PersonCleaner.V2.Domain
{
    internal sealed class DisjointSet
    {
        private readonly Dictionary<string, string> parent =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> rank =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> members =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        public void Add(string value)
        {
            if (parent.ContainsKey(value)) return;
            parent[value] = value;
            rank[value] = 0;
            members[value] = new HashSet<string>(StringComparer.Ordinal) { value };
        }

        public string Find(string value)
        {
            Add(value);
            if (!string.Equals(parent[value], value, StringComparison.Ordinal))
                parent[value] = Find(parent[value]);
            return parent[value];
        }

        public IReadOnlyCollection<string> Component(string value)
        {
            return members[Find(value)];
        }

        public bool Union(string left, string right, Func<IReadOnlyCollection<string>, IReadOnlyCollection<string>, bool> canUnion = null)
        {
            var leftRoot = Find(left);
            var rightRoot = Find(right);
            if (leftRoot == rightRoot) return true;
            if (canUnion != null && !canUnion(members[leftRoot], members[rightRoot])) return false;
            if (rank[leftRoot] < rank[rightRoot]) MergeRoots(rightRoot, leftRoot);
            else if (rank[leftRoot] > rank[rightRoot]) MergeRoots(leftRoot, rightRoot);
            else { MergeRoots(leftRoot, rightRoot); rank[leftRoot]++; }
            return true;
        }

        private void MergeRoots(string winner, string loser)
        {
            parent[loser] = winner;
            members[winner].UnionWith(members[loser]);
            members.Remove(loser);
        }
    }
}
