using System.IO;

namespace SlangGraph.Core;

public sealed record SlangGraphPackSet(
    IReadOnlyList<SlangGraphPack> Packs,
    SlangGraphPack CombinedGraph)
{
    public SlangGraphPack GetPack(string packId)
    {
        return Packs.First(pack =>
            string.Equals(pack.Header.PackId, packId, StringComparison.OrdinalIgnoreCase));
    }
}

public static class SlangGraphPackSetLoader
{
    public static SlangGraphPackSet Load(IEnumerable<string> packPaths)
    {
        ArgumentNullException.ThrowIfNull(packPaths);

        string[] paths = packPaths.ToArray();

        if (paths.Length == 0)
        {
            throw new ArgumentException("At least one pack path is required.", nameof(packPaths));
        }

        SlangGraphPack[] packs = paths
            .Select(path => SlangGraphJsonl.Load(path, validate: false))
            .ToArray();
        var errors = new List<string>();

        foreach (string duplicate in packs
                     .GroupBy(pack => pack.Header.PackId, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1)
                     .Select(group => group.Key))
        {
            errors.Add($"duplicate pack_id '{duplicate}'.");
        }

        var byId = packs.ToDictionary(
            pack => pack.Header.PackId,
            StringComparer.OrdinalIgnoreCase);

        foreach (SlangGraphPack pack in packs)
        {
            foreach (PackDependency dependency in pack.Header.Dependencies)
            {
                if (!byId.TryGetValue(dependency.PackId, out SlangGraphPack? dependencyPack))
                {
                    errors.Add($"pack {pack.Header.PackId} requires missing pack '{dependency.PackId}'.");
                    continue;
                }

                if (!VersionMatches(dependencyPack.Header.PackVersion, dependency.VersionRange))
                {
                    errors.Add(
                        $"pack {pack.Header.PackId} requires {dependency.PackId} " +
                        $"version '{dependency.VersionRange}', but found '{dependencyPack.Header.PackVersion}'.");
                }
            }
        }

        DetectDependencyCycles(packs, byId, errors);

        SlangGraphPack combined = Combine(packs);
        errors.AddRange(SlangGraphValidator.Validate(combined));

        if (errors.Count > 0)
        {
            throw new InvalidDataException(
                "Slang graph pack set validation failed:" +
                Environment.NewLine +
                string.Join(Environment.NewLine, errors.Distinct().Select(error => $"- {error}")));
        }

        return new SlangGraphPackSet(packs, combined);
    }

    private static SlangGraphPack Combine(IReadOnlyList<SlangGraphPack> packs)
    {
        return new SlangGraphPack(
            new PackHeader(
                "slang-graph/0.2",
                "packset.combined",
                "0.0.0",
                Array.Empty<PackDependency>(),
                "draft"),
            packs.SelectMany(pack => pack.Domains).ToArray(),
            packs.SelectMany(pack => pack.Concepts).ToArray(),
            packs.SelectMany(pack => pack.Expressions).ToArray(),
            packs.SelectMany(pack => pack.Patterns).ToArray(),
            packs.SelectMany(pack => pack.Entities).ToArray(),
            packs.SelectMany(pack => pack.Relations).ToArray(),
            packs.SelectMany(pack => pack.Renderings).ToArray(),
            packs.SelectMany(pack => pack.Sources).ToArray(),
            packs.SelectMany(pack => pack.Evidence).ToArray(),
            packs.SelectMany(pack => pack.Reviews).ToArray(),
            packs.SelectMany(pack => pack.Observations).ToArray());
    }

    private static bool VersionMatches(string actual, string versionRange)
    {
        if (versionRange == "*")
        {
            return true;
        }

        if (versionRange.EndsWith(".*", StringComparison.Ordinal))
        {
            return actual.StartsWith(
                versionRange[..^1],
                StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(actual, versionRange, StringComparison.OrdinalIgnoreCase);
    }

    private static void DetectDependencyCycles(
        IEnumerable<SlangGraphPack> packs,
        IReadOnlyDictionary<string, SlangGraphPack> byId,
        ICollection<string> errors)
    {
        var states = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var path = new Stack<string>();

        foreach (SlangGraphPack pack in packs)
        {
            Visit(pack.Header.PackId);
        }

        void Visit(string packId)
        {
            if (states.TryGetValue(packId, out int state))
            {
                if (state == 1)
                {
                    errors.Add($"dependency cycle detected at pack '{packId}' ({string.Join(" -> ", path.Reverse())}).");
                }

                return;
            }

            states[packId] = 1;
            path.Push(packId);

            if (byId.TryGetValue(packId, out SlangGraphPack? pack))
            {
                foreach (PackDependency dependency in pack.Header.Dependencies)
                {
                    if (byId.ContainsKey(dependency.PackId))
                    {
                        Visit(dependency.PackId);
                    }
                }
            }

            path.Pop();
            states[packId] = 2;
        }
    }
}
