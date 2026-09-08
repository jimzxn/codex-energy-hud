using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

[assembly: InternalsVisibleTo("CodexHud.Tests")]

namespace CodexHud.Core;

internal readonly record struct GpuEngineIdentity(int Pid, string AdapterToken, int PhysicalAdapter, int Engine);

internal static partial class HardwareAlgorithms
{
    [GeneratedRegex(@"^pid_(\d+)_(luid_0x[0-9a-f]+_0x[0-9a-f]+)_phys_(\d+)_eng_(\d+)(?:_|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GpuInstancePattern();

    internal static bool TryParseGpuInstance(string instance, out GpuEngineIdentity identity)
    {
        var match = GpuInstancePattern().Match(instance);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var pid)
            && int.TryParse(match.Groups[3].Value, out var physical) && int.TryParse(match.Groups[4].Value, out var engine))
        {
            identity = new GpuEngineIdentity(pid, match.Groups[2].Value.ToLowerInvariant(), physical, engine);
            return true;
        }
        identity = default;
        return false;
    }

    internal static (double System, double Codex, int Matched) AggregateGpu(
        IEnumerable<(GpuEngineIdentity Engine, double Percent)> values, string adapterToken, ISet<int> codexPids)
    {
        var system = new Dictionary<(int Physical, int Engine), double>();
        var codex = new Dictionary<(int Physical, int Engine), double>();
        var matched = 0;
        foreach (var (engine, value) in values)
        {
            if (!engine.AdapterToken.Equals(adapterToken, StringComparison.OrdinalIgnoreCase) || !double.IsFinite(value) || value < 0) continue;
            var key = (engine.PhysicalAdapter, engine.Engine);
            system[key] = system.GetValueOrDefault(key) + value;
            if (codexPids.Contains(engine.Pid)) codex[key] = codex.GetValueOrDefault(key) + value;
            matched++;
        }
        return (Math.Clamp(system.Values.DefaultIfEmpty(0).Max(), 0, 100),
            Math.Clamp(codex.Values.DefaultIfEmpty(0).Max(), 0, 100), matched);
    }

    internal static double CpuPercent(long busyTicks, long totalTicks) =>
        totalTicks > 0 ? Math.Clamp(100d * busyTicks / totalTicks, 0, 100) : double.NaN;

    internal static MetricSample RetainFailure(MetricSample? previous, string message, bool loading = false) =>
        previous?.Value is not null
            ? previous with { Health = SampleHealth.Stale, Detail = message }
            : new MetricSample(null, null, loading ? SampleHealth.Loading : SampleHealth.Unavailable, message);

    internal static bool IsApplicationImage(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return false;
        var normalized = imagePath.Replace('/', '\\');
        var fileName = Path.GetFileName(normalized);
        if (!fileName.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase)
            && !fileName.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase)) return false;
        // The shipped GUI can be called ChatGPT.exe. A similarly named CLI/bridge is not an app root.
        if (normalized.Contains(@"\WindowsApps\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(Path.GetDirectoryName(normalized)), "app", StringComparison.OrdinalIgnoreCase)) return true;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).TrimEnd('\\');
        var installationRoot = local + @"\OpenAI\Codex\";
        if (normalized.StartsWith(installationRoot, StringComparison.OrdinalIgnoreCase))
        {
            var relative = normalized[installationRoot.Length..];
            return fileName.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase)
                && (relative.Equals(fileName, StringComparison.OrdinalIgnoreCase) || relative.Equals(@"app\" + fileName, StringComparison.OrdinalIgnoreCase));
        }
        return normalized.Equals(local + @"\Programs\Codex\" + fileName, StringComparison.OrdinalIgnoreCase);
    }

    internal static HashSet<int> ExcludedTree(IReadOnlyDictionary<int, int> parents, IEnumerable<int> roots)
    {
        var result = roots.Where(pid => pid > 0).ToHashSet();
        var children = parents.GroupBy(pair => pair.Value).ToDictionary(group => group.Key, group => group.Select(pair => pair.Key).ToArray());
        var queue = new Queue<int>(result);
        while (queue.TryDequeue(out var current))
            if (children.TryGetValue(current, out var descendants))
                foreach (var child in descendants)
                    if (result.Add(child)) queue.Enqueue(child);
        return result;
    }
}
