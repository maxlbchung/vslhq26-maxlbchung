using System.Text.Json.Serialization;

namespace SwarmRT.Org;

/// <summary>
/// Behavioural dial settings for a synthetic persona. Every value is 0.0-1.0.
/// These are the inputs the rule-weighted responder scores against; they are
/// fabricated character traits, not measurements of any real person.
/// </summary>
public sealed record PersonaTraits
{
    /// <summary>Willingness to comply with an apparent authority figure without challenge.</summary>
    [JsonPropertyName("authority_deference")]
    public double AuthorityDeference { get; init; }

    /// <summary>Tendency to act quickly when a deadline or consequence is asserted.</summary>
    [JsonPropertyName("urgency_susceptibility")]
    public double UrgencySusceptibility { get; init; }

    /// <summary>Pull toward opening unexpected content out of interest.</summary>
    [JsonPropertyName("curiosity")]
    public double Curiosity { get; init; }

    /// <summary>Desire to be helpful, and to return an unsolicited favour.</summary>
    [JsonPropertyName("helpfulness")]
    public double Helpfulness { get; init; }

    /// <summary>Comfort with technical detail; raises suspicion of technical pretexts.</summary>
    [JsonPropertyName("technical_literacy")]
    public double TechnicalLiteracy { get; init; }

    /// <summary>Habit of independently confirming a request through a known channel.</summary>
    [JsonPropertyName("verification_habit")]
    public double VerificationHabit { get; init; }

    /// <summary>How fresh security-awareness training is: 1.0 recent, 0.0 long overdue.</summary>
    [JsonPropertyName("training_recency")]
    public double TrainingRecency { get; init; }

    /// <summary>All traits, keyed by the name used in pretext weighting tables.</summary>
    public double this[TraitKey key] => key switch
    {
        TraitKey.AuthorityDeference => AuthorityDeference,
        TraitKey.UrgencySusceptibility => UrgencySusceptibility,
        TraitKey.Curiosity => Curiosity,
        TraitKey.Helpfulness => Helpfulness,
        TraitKey.TechnicalLiteracy => TechnicalLiteracy,
        TraitKey.VerificationHabit => VerificationHabit,
        TraitKey.TrainingRecency => TrainingRecency,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown trait."),
    };

    /// <summary>Traits outside 0.0-1.0 indicate a malformed roster.</summary>
    public IEnumerable<string> Validate()
    {
        foreach (TraitKey key in Enum.GetValues<TraitKey>())
        {
            double value = this[key];
            if (value is < 0.0 or > 1.0 || double.IsNaN(value))
            {
                yield return $"trait '{key}' is {value:0.###}; must be between 0.0 and 1.0";
            }
        }
    }
}

public enum TraitKey
{
    AuthorityDeference,
    UrgencySusceptibility,
    Curiosity,
    Helpfulness,
    TechnicalLiteracy,
    VerificationHabit,
    TrainingRecency,
}

/// <summary>
/// A fabricated employee record. <see cref="Exposure"/> holds synthetic
/// attributes that make certain pretexts more plausible against this persona
/// (design §3.3), e.g. "listed_on_public_contact_page".
/// </summary>
public sealed record Employee
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("department")]
    public required string Department { get; init; }

    /// <summary>Synthetic mailbox. Must sit on a reserved non-routable domain.</summary>
    [JsonPropertyName("email")]
    public required string Email { get; init; }

    [JsonPropertyName("exposure")]
    public IReadOnlyList<string> Exposure { get; init; } = [];

    [JsonPropertyName("traits")]
    public required PersonaTraits Traits { get; init; }

    /// <summary>Short voice note used to shape simulated replies.</summary>
    [JsonPropertyName("voice")]
    public string Voice { get; init; } = "neutral, brief";

    public bool HasExposure(string attribute) =>
        Exposure.Any(e => string.Equals(e, attribute, StringComparison.OrdinalIgnoreCase));

    /// <summary>Filesystem-safe stem for this employee's individual report.</summary>
    public string ReportSlug()
    {
        string name = string.Concat(Name.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-'));
        while (name.Contains("--", StringComparison.Ordinal))
        {
            name = name.Replace("--", "-", StringComparison.Ordinal);
        }

        return $"{Id}-{name.Trim('-')}";
    }
}

/// <summary>The fabricated company under simulated test (design §3.3).</summary>
public sealed record SyntheticOrg
{
    [JsonPropertyName("org_name")]
    public required string OrgName { get; init; }

    /// <summary>Reserved domain the roster's mailboxes must belong to.</summary>
    [JsonPropertyName("domain")]
    public required string Domain { get; init; }

    /// <summary>Short code used to build default engagement ids, e.g. "NWT" -> "NWT-2026-07".</summary>
    [JsonPropertyName("short_code")]
    public string? ShortCode { get; init; }

    [JsonPropertyName("synthetic")]
    public bool Synthetic { get; init; } = true;

    [JsonPropertyName("notes")]
    public string Notes { get; init; } = string.Empty;

    [JsonPropertyName("employees")]
    public required IReadOnlyList<Employee> Employees { get; init; }

    /// <summary>The configured short code, or initials derived from the org name.</summary>
    public string ResolveShortCode()
    {
        if (!string.IsNullOrWhiteSpace(ShortCode))
        {
            return ShortCode.Trim().ToUpperInvariant();
        }

        string initials = string.Concat(OrgName
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(word => char.IsLetter(word[0]))
            .Select(word => char.ToUpperInvariant(word[0])));

        return initials.Length > 0 ? initials : "ORG";
    }

    public Employee? Find(string employeeId) =>
        Employees.FirstOrDefault(e => e.Id == employeeId);

    public Employee Require(string employeeId) =>
        Find(employeeId) ?? throw new KeyNotFoundException($"No employee '{employeeId}' in roster.");
}
