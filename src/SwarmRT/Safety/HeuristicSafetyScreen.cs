using System.Text.RegularExpressions;
using SwarmRT.Agents;
using SwarmRT.Org;

namespace SwarmRT.Safety;

/// <summary>
/// The deterministic first layer of the safety gate. It enforces the properties
/// design §8 states as guarantees rather than aspirations: simulation labelling
/// (§8.4), non-deployability (§8.3), and the absence of any real-world identifier
/// that could point at a live system (§8.1).
/// <para>
/// This layer is intentionally mechanical. It cannot be talked out of a decision by
/// a persuasive prompt, it runs with no network dependency, and it always runs
/// first — so the gate keeps working even when the model self-check is disabled or
/// the backend is unreachable. Semantic harm categories are the self-check layer's
/// job; structure and deployability are this layer's.
/// </para>
/// </summary>
public sealed class HeuristicSafetyScreen
{
    /// <summary>
    /// Lures longer than this are rejected as approaching ready-to-send copy. A
    /// simulation stub needs a couple of sentences to convey a pretext; anything
    /// materially longer is drifting toward deployable content.
    /// </summary>
    public const int MaxMessageLength = 520;

    /// <summary>The label every simulated lure must carry (design §8.4).</summary>
    public const string RequiredWatermark = "[SIMULATED]";

    private static readonly RegexOptions Opts =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static readonly Regex SchemeUrl =
        new(@"\b[a-z][a-z0-9+.\-]*://(?<host>[^\s/?#""'>)]+)", Opts);

    private static readonly Regex BareHost =
        new(@"\b(?<host>(?:[a-z0-9](?:[a-z0-9\-]{0,61}[a-z0-9])?\.)+(?<tld>[a-z]{2,24}))\b", Opts);

    private static readonly Regex EmailAddress =
        new(@"\b[a-z0-9._%+\-]+@(?<host>[a-z0-9.\-]+\.[a-z]{2,24})\b", Opts);

    private static readonly Regex IpAddress =
        new(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", Opts);

    private static readonly Regex PhoneLike =
        new(@"(?:\+|\b)\d[\d\s().\-]{7,}\d\b", Opts);

    /// <summary>
    /// A transmit verb aimed at a secret. Abstract references such as "verify your
    /// account" stay clear; an instruction to send or type a secret is a capture
    /// mechanic and therefore deployable.
    /// </summary>
    private static readonly Regex SecretSolicitation = new(
        @"\b(?:reply\s+with|send(?:\s+me)?|enter|type|paste|confirm\s+by\s+entering|provide)\b" +
        @"[^.!?\n]{0,40}\b(?:password|passphrase|pin|one[-\s]?time\s+code|otp|" +
        @"\d\s*-?\s*digit\s+code|verification\s+code|security\s+code|seed\s+phrase|private\s+key)\b",
        Opts);

    /// <summary>Real-world TLDs. A bare host on one of these points at the live internet.</summary>
    private static readonly HashSet<string> RoutableTlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "com", "net", "org", "edu", "gov", "mil", "int", "info", "biz", "io", "co", "ai", "app",
        "dev", "cloud", "me", "tv", "xyz", "top", "online", "site", "shop", "store", "tech",
        "space", "website", "fun", "vip", "pro", "name", "mobi", "asia", "eu", "link", "click",
        "live", "email", "zone", "world", "life", "today", "news", "blog", "page", "cc", "gg",
        "uk", "de", "fr", "ru", "cn", "jp", "br", "it", "es", "nl", "se", "no", "pl", "ch", "at",
        "be", "dk", "fi", "cz", "gr", "pt", "ie", "nz", "za", "mx", "ar", "cl", "tr", "kr", "hk",
        "sg", "ae", "sa", "il", "ua", "ro", "hu", "bg", "hr", "sk", "si", "lt", "lv", "ee", "is",
        "lu", "mt", "cy", "ca", "au", "in", "us",
    };

    /// <summary>
    /// Brands whose lookalikes make up most real phishing. A simulation must describe
    /// "the collaboration platform", not clone a named vendor, so any of these in lure
    /// text is a block.
    /// </summary>
    private static readonly string[] ImpersonatedBrands =
    [
        "microsoft", "office 365", "office365", "microsoft 365", "outlook", "onedrive",
        "sharepoint", "microsoft teams", "azure", "windows defender", "okta", "duo security",
        "docusign", "dropbox", "box.com", "google", "gmail", "google workspace", "google drive",
        "paypal", "stripe", "amazon", "aws", "apple", "icloud", "netflix", "linkedin",
        "facebook", "instagram", "whatsapp", "zoom", "slack", "adobe", "salesforce",
        "servicenow", "atlassian", "confluence", "workday", "quickbooks", "xero",
        "dhl", "fedex", "ups", "usps", "royal mail", "an post",
        "hmrc", "irs", "dvla", "social security administration",
        "chase", "wells fargo", "hsbc", "barclays", "santander", "citibank",
        "bank of america", "revolut", "monzo", "coinbase", "binance", "metamask",
        "crowdstrike", "sentinelone", "norton", "mcafee",
    ];

    /// <summary>Markers of actual tradecraft — execution, payload delivery, or defence evasion.</summary>
    private static readonly string[] HarmfulInstructionMarkers =
    [
        "powershell", "cmd.exe", "mshta", "certutil", "rundll32", "regsvr32", "wscript",
        "cscript", "invoke-webrequest", "invoke-expression", "iex(", "iex (", "curl |",
        "| sh", "| bash", "base64 -d", "frombase64string", "-encodedcommand",
        ".exe", ".hta", ".scr", ".vbs", ".jar", ".iso", ".lnk", ".xlsm", ".docm", ".ps1",
        "enable macros", "enable editing and macros", "macro-enabled",
        "disable your antivirus", "disable antivirus", "turn off defender",
        "turn off your antivirus", "add an exclusion", "allowlist this file",
        "reverse shell", "keylogger", "mimikatz", "lsass", "payload", "c2 server",
        "remote access tool", "teamviewer", "anydesk", "screen sharing session",
    ];

    /// <summary>Categories no simulated lure may carry regardless of pretext.</summary>
    private static readonly string[] ProhibitedContentMarkers =
    [
        "kill you", "kill your", "hurt you", "hurt your", "we know where you live",
        "your family will", "i will find you", "acid attack", "pipe bomb",
    ];

    /// <summary>
    /// A rule. <paramref name="AppliesToFragments"/> marks the rules that also apply to
    /// short model-authored strings bound for the log, where the whole-lure conventions
    /// (watermark, stub length) do not make sense.
    /// </summary>
    private sealed record Rule(
        string Category,
        string Rationale,
        Func<string, bool> Matches,
        bool AppliesToFragments = true);

    /// <summary>
    /// Compiles a marker list into one alternation regex.
    /// <para>
    /// Word boundaries are added at whichever ends of a marker are word characters, which
    /// matters more than it looks: short brand tokens like "ups" and "irs" match inside
    /// ordinary words as bare substrings, so "the first respondents" would otherwise be
    /// blocked as impersonating the IRS. Internal whitespace is relaxed to <c>\s+</c> so
    /// "office 365" also catches "office  365" across a line break.
    /// </para>
    /// </summary>
    private static Regex MarkerRegex(IEnumerable<string> markers)
    {
        IEnumerable<string> patterns = markers.Select(marker =>
        {
            string escaped = string.Join(
                @"\s+",
                marker.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape));

            string prefix = char.IsLetterOrDigit(marker[0]) ? @"\b" : string.Empty;
            string suffix = char.IsLetterOrDigit(marker[^1]) ? @"\b" : string.Empty;
            return $"{prefix}(?:{escaped}){suffix}";
        });

        return new Regex(string.Join('|', patterns), Opts);
    }

    private static readonly Regex BrandPattern = MarkerRegex(ImpersonatedBrands);

    private static readonly Regex HarmfulPattern = MarkerRegex(HarmfulInstructionMarkers);

    private static readonly Regex ProhibitedPattern = MarkerRegex(ProhibitedContentMarkers);

    /// <summary>Length ceiling for a single logged sentence such as a reason or summary.</summary>
    public const int MaxFragmentLength = 400;

    private readonly IReadOnlyList<Rule> _rules;

    public HeuristicSafetyScreen()
    {
        _rules =
        [
            new Rule(
                "missing_simulation_label",
                $"Lure omitted the mandatory {RequiredWatermark} label required by the simulation-watermark control.",
                text => !text.Contains(RequiredWatermark, StringComparison.OrdinalIgnoreCase),
                AppliesToFragments: false),

            new Rule(
                "deployable_specificity",
                $"Lure exceeded the {MaxMessageLength}-character simulation-stub limit and approaches ready-to-send copy.",
                text => text.Length > MaxMessageLength,
                AppliesToFragments: false),

            new Rule(
                "real_world_identifier",
                "Lure referenced a routable host, address, or number outside the reserved simulation domain.",
                ContainsRealWorldIdentifier),

            new Rule(
                "brand_impersonation",
                "Lure impersonated a named real-world brand instead of describing a generic internal service.",
                BrandPattern.IsMatch),

            new Rule(
                "harmful_instruction",
                // Worded to avoid the marker words this rule itself matches on, so the
                // rationale does not trip the fragment screen on its way into the log.
                "Lure contained instructions for running code, delivering a malicious attachment, " +
                "or evading security controls.",
                HarmfulPattern.IsMatch),

            new Rule(
                "credential_capture_mechanic",
                "Lure instructed the target to transmit a secret verbatim, which is a working capture mechanic rather than a pretext.",
                text => SecretSolicitation.IsMatch(text)),

            new Rule(
                "prohibited_content",
                "Lure contained threatening content that is out of scope for any awareness simulation.",
                ProhibitedPattern.IsMatch),
        ];
    }

    public string Description => $"deterministic heuristics ({_rules.Count} rules)";

    /// <summary>Screens raw lure text. Returns the first rule that fires.</summary>
    public SafetyVerdict Screen(string messageText)
    {
        ArgumentNullException.ThrowIfNull(messageText);

        foreach (Rule rule in _rules)
        {
            if (rule.Matches(messageText))
            {
                return SafetyVerdict.Block(rule.Category, rule.Rationale, "heuristic");
            }
        }

        return SafetyVerdict.Cleared("heuristic");
    }

    /// <summary>
    /// Screens a short model-authored string that is about to be written to the log —
    /// an attempt summary, a success or failure reason, a gate rationale.
    /// <para>
    /// The log file is the engagement's deliverable and its audit trail, so no
    /// model-authored text reaches it unscreened. Rules about whole-lure structure
    /// (the watermark, the stub length limit) are skipped; the rules about real-world
    /// identifiers, brand impersonation, tradecraft, capture mechanics, and prohibited
    /// content all still apply.
    /// </para>
    /// </summary>
    public SafetyVerdict ScreenFragment(string text, int? maxLength = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        int limit = maxLength ?? MaxFragmentLength;
        if (text.Length > limit)
        {
            return SafetyVerdict.Block(
                "oversized_log_fragment",
                $"Text exceeded the {limit}-character limit for this field.",
                "heuristic");
        }

        foreach (Rule rule in _rules.Where(r => r.AppliesToFragments))
        {
            if (rule.Matches(text))
            {
                return SafetyVerdict.Block(rule.Category, rule.Rationale, "heuristic");
            }
        }

        return SafetyVerdict.Cleared("heuristic");
    }

    private static bool ContainsRealWorldIdentifier(string text)
    {
        foreach (Match match in SchemeUrl.Matches(text))
        {
            string host = match.Groups["host"].Value.Split(':')[0];
            if (!SyntheticDataGuard.IsReservedDomain(host))
            {
                return true;
            }
        }

        foreach (Match match in EmailAddress.Matches(text))
        {
            if (!SyntheticDataGuard.IsReservedDomain(match.Groups["host"].Value))
            {
                return true;
            }
        }

        foreach (Match match in BareHost.Matches(text))
        {
            string host = match.Groups["host"].Value;
            if (SyntheticDataGuard.IsReservedDomain(host))
            {
                continue;
            }

            if (RoutableTlds.Contains(match.Groups["tld"].Value))
            {
                return true;
            }
        }

        if (IpAddress.IsMatch(text))
        {
            return true;
        }

        foreach (Match match in PhoneLike.Matches(text))
        {
            if (match.Value.Count(char.IsAsciiDigit) >= 9)
            {
                return true;
            }
        }

        return false;
    }
}
