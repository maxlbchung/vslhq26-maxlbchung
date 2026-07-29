using System.Text.Json;
using SwarmRT.Contracts;

namespace SwarmRT.Org;

/// <summary>
/// Reads the fabricated roster from disk and refuses to hand back anything that
/// fails the synthetic-only check.
/// </summary>
public static class OrgLoader
{
    public const string DefaultFileName = "synthetic-org.json";

    /// <summary>Loads and validates a roster. Throws rather than returning a suspect org.</summary>
    public static SyntheticOrg Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Synthetic roster not found at '{path}'.", path);
        }

        string json = File.ReadAllText(path);
        SyntheticOrg org;
        try
        {
            org = JsonSerializer.Deserialize<SyntheticOrg>(json, SwarmJson.Reading)
                  ?? throw new InvalidDataException($"Roster at '{path}' deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Roster at '{path}' is not valid JSON: {ex.Message}", ex);
        }

        SyntheticDataGuard.EnsureSynthetic(org);
        return org;
    }

    /// <summary>
    /// Finds the bundled roster: an explicit path, else the copy beside the
    /// executable, else a <c>data/</c> folder walking up from the working directory
    /// (so <c>dotnet run</c> from anywhere in the tree still works).
    /// </summary>
    public static string ResolveDefaultPath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        string beside = Path.Combine(AppContext.BaseDirectory, "data", DefaultFileName);
        if (File.Exists(beside))
        {
            return beside;
        }

        DirectoryInfo? dir = new(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "src", "SwarmRT", "data", DefaultFileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            candidate = Path.Combine(dir.FullName, "data", DefaultFileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return beside;
    }
}
