using SwarmRT.Org;
using SwarmRT.Safety;

namespace SwarmRT.Tests;

/// <summary>
/// Design §8.1 — the synthetic-only check. These are the tests that matter most: they are
/// what stands between the tool and contact with a real mailbox.
/// </summary>
public class SyntheticDataGuardTests
{
    [Theory]
    [InlineData("northwind-traders.example")]
    [InlineData("anything.invalid")]
    [InlineData("host.test")]
    [InlineData("localhost")]
    [InlineData("example.com")]
    [InlineData("sub.example.org")]
    public void AcceptsReservedDomains(string domain) =>
        Assert.True(SyntheticDataGuard.IsReservedDomain(domain));

    [Theory]
    [InlineData("northwind-traders.com")]
    [InlineData("contoso.co.uk")]
    [InlineData("gmail.com")]
    [InlineData("evil.example.com.attacker.net")]
    [InlineData("exampled.com")]
    public void RejectsRoutableDomains(string domain) =>
        Assert.False(SyntheticDataGuard.IsReservedDomain(domain));

    [Fact]
    public void AcceptsAWellFormedSyntheticRoster() =>
        SyntheticDataGuard.EnsureSynthetic(TestSupport.Org());

    [Fact]
    public void RejectsARosterWithARoutableMailbox()
    {
        SyntheticOrg org = TestSupport.Org(
            TestSupport.Employee(email: "real.person@contoso.com"));

        NonSyntheticTargetException error =
            Assert.Throws<NonSyntheticTargetException>(() => SyntheticDataGuard.EnsureSynthetic(org));

        Assert.Contains("contoso.com", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsARosterWhoseDomainIsRoutable()
    {
        SyntheticOrg org = TestSupport.Org() with { Domain = "northwind-traders.com" };

        Assert.Throws<NonSyntheticTargetException>(() => SyntheticDataGuard.EnsureSynthetic(org));
    }

    [Fact]
    public void RejectsARosterNotMarkedSynthetic()
    {
        SyntheticOrg org = TestSupport.Org() with { Synthetic = false };

        Assert.Throws<NonSyntheticTargetException>(() => SyntheticDataGuard.EnsureSynthetic(org));
    }

    [Fact]
    public void RejectsDuplicateEmployeeIds()
    {
        SyntheticOrg org = TestSupport.Org(
            TestSupport.Employee(id: "emp-001"),
            TestSupport.Employee(id: "emp-001", name: "Clone"));

        NonSyntheticTargetException error =
            Assert.Throws<NonSyntheticTargetException>(() => SyntheticDataGuard.EnsureSynthetic(org));

        Assert.Contains("duplicate employee ids", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsOutOfRangeTraits()
    {
        SyntheticOrg org = TestSupport.Org(
            TestSupport.Employee(traits: TestSupport.Traits(authority: 1.7)));

        NonSyntheticTargetException error =
            Assert.Throws<NonSyntheticTargetException>(() => SyntheticDataGuard.EnsureSynthetic(org));

        Assert.Contains("AuthorityDeference", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAnEmptyRoster()
    {
        SyntheticOrg org = TestSupport.Org() with { Employees = [] };

        Assert.Throws<NonSyntheticTargetException>(() => SyntheticDataGuard.EnsureSynthetic(org));
    }

    [Fact]
    public void TheBundledRosterPassesTheGuard()
    {
        string path = OrgLoader.ResolveDefaultPath(null);
        Assert.True(File.Exists(path), $"bundled roster not found at '{path}'");

        SyntheticOrg org = OrgLoader.Load(path);

        Assert.NotEmpty(org.Employees);
        Assert.All(org.Employees, e =>
            Assert.True(
                SyntheticDataGuard.IsReservedDomain(e.Email.Split('@')[1]),
                $"{e.Id} has a routable mailbox"));
    }
}

/// <summary>Design §3.5 and §8.2 — the deterministic layer of the content-safety gate.</summary>
public class HeuristicSafetyScreenTests
{
    private readonly HeuristicSafetyScreen _screen = new();

    private const string Watermark = HeuristicSafetyScreen.RequiredWatermark;

    [Fact]
    public void ClearsAnAbstractLabelledStub()
    {
        SafetyVerdict verdict = _screen.Screen(
            $"{Watermark} Poses as the internal service desk asking the recipient to re-confirm " +
            "access before the end of the day.");

        Assert.False(verdict.Flagged);
    }

    [Fact]
    public void BlocksAnUnlabelledLure()
    {
        SafetyVerdict verdict = _screen.Screen("Please confirm your access before end of day.");

        Assert.True(verdict.Flagged);
        Assert.Equal("missing_simulation_label", verdict.Category);
    }

    [Fact]
    public void BlocksAnOverlongLure()
    {
        string longText = $"{Watermark} " + new string('a', HeuristicSafetyScreen.MaxMessageLength);

        SafetyVerdict verdict = _screen.Screen(longText);

        Assert.True(verdict.Flagged);
        Assert.Equal("deployable_specificity", verdict.Category);
    }

    [Theory]
    [InlineData("https://nw-login.com/reset")]
    [InlineData("http://192.168.10.44/portal")]
    [InlineData("visit portal.contoso.net today")]
    [InlineData("write to helpdesk@contoso.com")]
    [InlineData("call +1 415 555 0132 now")]
    public void BlocksRealWorldIdentifiers(string fragment)
    {
        SafetyVerdict verdict = _screen.Screen($"{Watermark} Access review pending. {fragment}");

        Assert.True(verdict.Flagged);
        Assert.Equal("real_world_identifier", verdict.Category);
    }

    [Fact]
    public void AllowsAReservedDomainUrl()
    {
        SafetyVerdict verdict = _screen.Screen(
            $"{Watermark} Access review pending at https://portal.northwind-traders.example/review.");

        Assert.False(verdict.Flagged);
    }

    [Theory]
    [InlineData("Your Microsoft 365 mailbox is over quota.")]
    [InlineData("A DocuSign envelope is waiting.")]
    [InlineData("Your PayPal account needs attention.")]
    public void BlocksBrandImpersonation(string fragment)
    {
        SafetyVerdict verdict = _screen.Screen($"{Watermark} {fragment}");

        Assert.True(verdict.Flagged);
        Assert.Equal("brand_impersonation", verdict.Category);
    }

    /// <summary>
    /// Regression: brand markers were once matched as bare substrings, so "first" tripped the
    /// "irs" marker and "groups" tripped "ups". Short brand tokens must be word-bounded.
    /// </summary>
    [Theory]
    [InlineData("A gift is promised to the first respondents.")]
    [InlineData("Several groups have already completed the acknowledgement.")]
    [InlineData("The upstream team was notified separately.")]
    [InlineData("Boxes of documents are awaiting collection.")]
    public void DoesNotMistakeOrdinaryWordsForBrands(string fragment)
    {
        SafetyVerdict verdict = _screen.Screen($"{Watermark} {fragment}");

        Assert.False(verdict.Flagged, $"'{fragment}' was wrongly flagged as {verdict.Category}");
    }

    [Theory]
    [InlineData("Open the attached totals.xlsm and enable macros so the figures calculate.")]
    [InlineData("Run powershell -EncodedCommand to finish the install.")]
    [InlineData("Disable your antivirus before extracting the archive.")]
    public void BlocksTradecraft(string fragment)
    {
        SafetyVerdict verdict = _screen.Screen($"{Watermark} {fragment}");

        Assert.True(verdict.Flagged);
        Assert.Equal("harmful_instruction", verdict.Category);
    }

    [Theory]
    [InlineData("Reply with your one-time code to finish enrolling.")]
    [InlineData("Enter your password to continue the review.")]
    [InlineData("Send me the 6-digit code from your authenticator.")]
    public void BlocksCaptureMechanics(string fragment)
    {
        SafetyVerdict verdict = _screen.Screen($"{Watermark} {fragment}");

        Assert.True(verdict.Flagged);
        Assert.Equal("credential_capture_mechanic", verdict.Category);
    }

    /// <summary>
    /// Describing an approach abstractly is the whole point of the exercise, so a pretext
    /// that references credentials without soliciting one must still pass.
    /// </summary>
    [Theory]
    [InlineData("Asks the recipient to re-confirm account access before a stated deadline.")]
    [InlineData("Claims a credential review is outstanding for this department.")]
    public void AllowsAbstractReferencesToCredentials(string fragment)
    {
        SafetyVerdict verdict = _screen.Screen($"{Watermark} {fragment}");

        Assert.False(verdict.Flagged, $"'{fragment}' was wrongly flagged as {verdict.Category}");
    }

    [Fact]
    public void FragmentScreeningIgnoresWatermarkAndStubLength()
    {
        SafetyVerdict verdict = _screen.ScreenFragment(
            "Target acted on the request without verifying the sender through a known channel.");

        Assert.False(verdict.Flagged);
    }

    [Fact]
    public void FragmentScreeningStillBlocksIdentifiers()
    {
        SafetyVerdict verdict = _screen.ScreenFragment("Target visited nw-login.com and signed in.");

        Assert.True(verdict.Flagged);
        Assert.Equal("real_world_identifier", verdict.Category);
    }

    [Fact]
    public void FragmentScreeningEnforcesItsOwnLengthLimit()
    {
        SafetyVerdict verdict = _screen.ScreenFragment(
            new string('a', HeuristicSafetyScreen.MaxFragmentLength + 1));

        Assert.True(verdict.Flagged);
        Assert.Equal("oversized_log_fragment", verdict.Category);
    }

    /// <summary>
    /// Each rule's own rationale is written into the log, so a rationale must not trip the
    /// rule that produced it. This caught "payload-delivery" in the tradecraft rationale.
    /// </summary>
    [Fact]
    public void NoRuleRationaleTripsTheFragmentScreen()
    {
        string[] samples =
        [
            "[SIMULATED] Access review pending.",
            "[SIMULATED] Your Microsoft 365 mailbox is over quota.",
            "[SIMULATED] Open the attached totals.xlsm and enable macros.",
            "[SIMULATED] Reply with your one-time code to continue.",
            "unlabelled content",
            new string('a', HeuristicSafetyScreen.MaxMessageLength + 1),
        ];

        foreach (string sample in samples)
        {
            SafetyVerdict verdict = _screen.Screen(sample);
            if (!verdict.Flagged)
            {
                continue;
            }

            SafetyVerdict rationaleCheck = _screen.ScreenFragment(verdict.Rationale);
            Assert.False(
                rationaleCheck.Flagged,
                $"rationale for '{verdict.Category}' trips '{rationaleCheck.Category}': {verdict.Rationale}");
        }
    }
}

/// <summary>Design §8.2 — the in-band control tests must actually be caught by the gate.</summary>
public class SafetyProbeTests
{
    [Fact]
    public void EveryControlCaseTripsItsExpectedRule()
    {
        HeuristicSafetyScreen screen = new();

        foreach (SafetyProbeCase probe in SafetyProbe.Cases)
        {
            SafetyVerdict verdict = screen.Screen(probe.CannedMessage);

            Assert.True(verdict.Flagged, $"control case '{probe.Id}' was not blocked");
            Assert.Equal(probe.ExpectedCategory, verdict.Category);
        }
    }

    [Fact]
    public void ControlPretextIsNotInTheAttackCatalog() =>
        Assert.Null(PretextCatalog.Find(SafetyProbe.PretextId));

    [Fact]
    public void ControlRowsAreIdentifiable()
    {
        Assert.True(SafetyProbe.IsControlRow(SafetyProbe.PretextId));
        Assert.False(SafetyProbe.IsControlRow("it_helpdesk_impersonation"));
    }
}

/// <summary>The checkpoint that keeps model-authored text out of the log unscreened.</summary>
public class LogTextSanitizerTests
{
    private readonly LogTextSanitizer _sanitizer = new(new HeuristicSafetyScreen());

    [Fact]
    public void PassesCleanTextThrough()
    {
        string result = _sanitizer.Sanitize("Target acted without verifying the sender.", "fallback");

        Assert.Equal("Target acted without verifying the sender.", result);
        Assert.Equal(0, _sanitizer.RedactionCount);
    }

    [Fact]
    public void ReplacesTextCarryingAnIdentifier()
    {
        string result = _sanitizer.Sanitize("Target signed in at nw-login.com.", "fallback");

        Assert.Equal("fallback", result);
        Assert.Equal(1, _sanitizer.RedactionCount);
        Assert.Contains("real_world_identifier", _sanitizer.Redactions);
    }

    [Fact]
    public void UsesTheFallbackForBlankInput() =>
        Assert.Equal("fallback", _sanitizer.Sanitize("   ", "fallback"));

    [Fact]
    public void CollapsesNewlinesSoALogLineStaysOneLine()
    {
        string result = _sanitizer.Sanitize("first line\nsecond   line", "fallback");

        Assert.Equal("first line second line", result);
        Assert.DoesNotContain('\n', result);
    }

    [Fact]
    public void TrySanitizeReportsFailureWithoutAFallback()
    {
        Assert.False(_sanitizer.TrySanitize("Visit nw-login.com now.", out string clean));
        Assert.Equal(string.Empty, clean);
    }
}
