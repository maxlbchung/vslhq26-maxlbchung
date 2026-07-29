using System.Globalization;

namespace SwarmRT.Cli;

/// <summary>Raised for a malformed or unknown option; the CLI turns it into a usage message.</summary>
public sealed class UsageException(string message) : Exception(message);

/// <summary>
/// Minimal option parser. Hand-rolled rather than taken from a package so the tool has
/// no external dependencies at all and restores offline.
/// <para>
/// Accepts <c>--name value</c>, <c>--name=value</c>, and bare <c>--flag</c>. Unknown
/// options are rejected rather than ignored, so a typo fails loudly instead of silently
/// running with a default.
/// </para>
/// </summary>
public sealed class Arguments
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _consumed = new(StringComparer.OrdinalIgnoreCase);

    public Arguments(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string[] tokens = args.ToArray();
        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i];

            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                throw new UsageException($"Unexpected argument '{token}'. Options must start with '--'.");
            }

            string name = token[2..];
            string? value = null;

            int equals = name.IndexOf('=');
            if (equals >= 0)
            {
                value = name[(equals + 1)..];
                name = name[..equals];
            }
            else if (i + 1 < tokens.Length && !tokens[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = tokens[i + 1];
                i++;
            }

            if (name.Length == 0)
            {
                throw new UsageException("Encountered '--' with no option name.");
            }

            _values[name] = value;
        }
    }

    public bool Has(string name)
    {
        _consumed.Add(name);
        return _values.ContainsKey(name);
    }

    /// <summary>
    /// A boolean flag with a paired <c>--no-{name}</c> negation, so defaults can be
    /// overridden in both directions.
    /// </summary>
    public bool Flag(string name, bool defaultValue = false)
    {
        _consumed.Add(name);
        _consumed.Add($"no-{name}");

        if (_values.ContainsKey($"no-{name}"))
        {
            return false;
        }

        if (!_values.TryGetValue(name, out string? raw))
        {
            return defaultValue;
        }

        if (raw is null)
        {
            return true;
        }

        return bool.TryParse(raw, out bool parsed)
            ? parsed
            : throw new UsageException($"--{name} expects true or false, got '{raw}'.");
    }

    public string? String(string name, string? defaultValue = null)
    {
        _consumed.Add(name);

        if (!_values.TryGetValue(name, out string? raw))
        {
            return defaultValue;
        }

        return string.IsNullOrWhiteSpace(raw)
            ? throw new UsageException($"--{name} requires a value.")
            : raw;
    }

    public string Required(string name)
    {
        return String(name) ?? throw new UsageException($"--{name} is required.");
    }

    public int Int(string name, int defaultValue, int min = int.MinValue, int max = int.MaxValue)
    {
        string? raw = String(name);
        if (raw is null)
        {
            return defaultValue;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            throw new UsageException($"--{name} expects a whole number, got '{raw}'.");
        }

        if (parsed < min || parsed > max)
        {
            throw new UsageException($"--{name} must be between {min} and {max}, got {parsed}.");
        }

        return parsed;
    }

    /// <summary>Reads a value constrained to a fixed set of choices.</summary>
    public string Choice(string name, string defaultValue, params string[] allowed)
    {
        string raw = String(name, defaultValue)!;

        return allowed.FirstOrDefault(a => string.Equals(a, raw, StringComparison.OrdinalIgnoreCase))
               ?? throw new UsageException(
                   $"--{name} must be one of {string.Join(" | ", allowed)}, got '{raw}'.");
    }

    /// <summary>
    /// Throws if any supplied option was never read. Called after binding so an option
    /// that belongs to a different verb cannot be silently discarded.
    /// </summary>
    public void EnsureAllConsumed()
    {
        string[] unknown = _values.Keys
            .Where(key => !_consumed.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        if (unknown.Length > 0)
        {
            throw new UsageException(
                $"Unknown option(s): {string.Join(", ", unknown.Select(u => "--" + u))}.");
        }
    }
}
