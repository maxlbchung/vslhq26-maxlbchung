namespace SwarmRT.Org;

/// <summary>
/// Thrown when a roster fails the synthetic-only check. The run aborts rather
/// than degrading to a warning: design §8.1 makes "no real targets" a hard
/// property of the tool, not a convention the operator is trusted to follow.
/// </summary>
public sealed class NonSyntheticTargetException(string message) : Exception(message);

/// <summary>
/// Enforces design §8.1 (synthetic-only targets). A roster may only address
/// mailboxes on domains that the IETF has reserved as permanently
/// unresolvable, so a configuration mistake cannot turn into contact with a
/// real mailbox.
/// </summary>
public static class SyntheticDataGuard
{
    /// <summary>Reserved names from RFC 2606 / RFC 6761 — guaranteed never to resolve.</summary>
    public static readonly IReadOnlyList<string> ReservedSuffixes =
    [
        ".example", ".invalid", ".test", ".localhost",
        "example.com", "example.net", "example.org",
    ];

    public static bool IsReservedDomain(string domain)
    {
        string normalized = domain.Trim().TrimEnd('.').ToLowerInvariant();
        return ReservedSuffixes.Any(suffix =>
            normalized.EndsWith(suffix, StringComparison.Ordinal) ||
            normalized == suffix.TrimStart('.'));
    }

    /// <summary>
    /// Validates the roster and throws on the first class of problem found.
    /// Checks: the synthetic flag, the org domain, every mailbox domain,
    /// duplicate ids, and trait ranges.
    /// </summary>
    public static void EnsureSynthetic(SyntheticOrg org)
    {
        ArgumentNullException.ThrowIfNull(org);

        List<string> problems = [];

        if (!org.Synthetic)
        {
            problems.Add("roster is not marked 'synthetic': true");
        }

        if (!IsReservedDomain(org.Domain))
        {
            problems.Add($"org domain '{org.Domain}' is not a reserved test domain");
        }

        if (org.Employees.Count == 0)
        {
            problems.Add("roster contains no employees");
        }

        foreach (Employee employee in org.Employees)
        {
            int at = employee.Email.LastIndexOf('@');
            if (at <= 0 || at == employee.Email.Length - 1)
            {
                problems.Add($"{employee.Id}: '{employee.Email}' is not a well-formed address");
                continue;
            }

            string domain = employee.Email[(at + 1)..];
            if (!IsReservedDomain(domain))
            {
                problems.Add($"{employee.Id}: mailbox domain '{domain}' is not a reserved test domain");
            }

            foreach (string traitProblem in employee.Traits.Validate())
            {
                problems.Add($"{employee.Id}: {traitProblem}");
            }
        }

        string[] duplicates = org.Employees
            .GroupBy(e => e.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();
        if (duplicates.Length > 0)
        {
            problems.Add($"duplicate employee ids: {string.Join(", ", duplicates)}");
        }

        if (problems.Count > 0)
        {
            throw new NonSyntheticTargetException(
                "Refusing to run: the roster did not pass the synthetic-only check." +
                Environment.NewLine +
                string.Join(Environment.NewLine, problems.Select(p => "  - " + p)) +
                Environment.NewLine +
                $"Allowed mailbox suffixes: {string.Join(", ", ReservedSuffixes)}");
        }
    }
}
