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

        public void Add(string value)
        {
            if (parent.ContainsKey(value)) return;
            parent[value] = value;
            rank[value] = 0;
        }

        public string Find(string value)
        {
            Add(value);
            if (!string.Equals(parent[value], value, StringComparison.Ordinal))
                parent[value] = Find(parent[value]);
            return parent[value];
        }

        public void Union(string left, string right)
        {
            var leftRoot = Find(left);
            var rightRoot = Find(right);
            if (leftRoot == rightRoot) return;
            if (rank[leftRoot] < rank[rightRoot]) parent[leftRoot] = rightRoot;
            else if (rank[leftRoot] > rank[rightRoot]) parent[rightRoot] = leftRoot;
            else { parent[rightRoot] = leftRoot; rank[leftRoot]++; }
        }
    }
}
