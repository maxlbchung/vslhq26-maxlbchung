using SwarmRT.Agents;
using SwarmRT.Contracts;
using SwarmRT.Model;
using SwarmRT.Org;
using SwarmRT.Responders;
using SwarmRT.Safety;

namespace SwarmRT.Tests;

/// <summary>Fabricated rosters and stub collaborators shared across the test suite.</summary>
internal static class TestSupport
{
    public const string ReservedDomain = "testco.example";

    public static PersonaTraits Traits(
        double authority = 0.5,
        double urgency = 0.5,
        double curiosity = 0.5,
        double helpfulness = 0.5,
        double technical = 0.5,
        double verification = 0.5,
        double training = 0.5) => new()
    {
        AuthorityDeference = authority,
        UrgencySusceptibility = urgency,
        Curiosity = curiosity,
        Helpfulness = helpfulness,
        TechnicalLiteracy = technical,
        VerificationHabit = verification,
        TrainingRecency = training,
    };

    public static Employee Employee(
        string id = "emp-001",
        string name = "Test Persona",
        string role = "Analyst",
        string department = "Operations",
        string? email = null,
        IReadOnlyList<string>? exposure = null,
        PersonaTraits? traits = null) => new()
    {
        Id = id,
        Name = name,
        Role = role,
        Department = department,
        Email = email ?? $"{id}@{ReservedDomain}",
        Exposure = exposure ?? [],
        Traits = traits ?? Traits(),
    };

    /// <summary>A persona built to comply: every susceptibility high, no resistance.</summary>
    public static Employee Compliant(string id = "emp-soft") => Employee(
        id: id,
        name: "Soft Target",
        exposure: ["new_hire", "recent_device_change"],
        traits: Traits(
            authority: 0.95, urgency: 0.95, curiosity: 0.9, helpfulness: 0.95,
            technical: 0.05, verification: 0.02, training: 0.02));

    /// <summary>A persona built to resist: low susceptibility, high verification and training.</summary>
    public static Employee Resistant(string id = "emp-hard") => Employee(
        id: id,
        name: "Hard Target",
        traits: Traits(
            authority: 0.05, urgency: 0.05, curiosity: 0.1, helpfulness: 0.1,
            technical: 0.98, verification: 0.98, training: 0.98));

    public static SyntheticOrg Org(params Employee[] employees) => new()
    {
        OrgName = "Test Co",
        Domain = ReservedDomain,
        ShortCode = "TCO",
        Synthetic = true,
        Employees = employees.Length > 0 ? employees : [Compliant(), Resistant()],
    };

    public static AgentAssignment Assignment(
        string attemptId = "att-0001",
        string employeeId = "emp-001",
        string pretext = "it_helpdesk_impersonation",
        string tactic = "urgency + authority",
        int seed = 42) => new()
    {
        EngagementId = "TST-2026-07",
        AttemptId = attemptId,
        TargetEmployeeId = employeeId,
        PretextType = pretext,
        Tactic = tactic,
        Seed = seed,
    };

    public static PretextType Pretext(string id = "it_helpdesk_impersonation") =>
        PretextCatalog.Require(id);

    /// <summary>A definition wired entirely from deterministic parts.</summary>
    public static AgentDefinition Definition(
        ILureComposer? composer = null,
        IContentSafetyGate? gate = null,
        IEmployeeResponder? responder = null,
        IReplyJudge? judge = null,
        LogTextSanitizer? sanitizer = null,
        TimeProvider? time = null)
    {
        HeuristicSafetyScreen screen = new();

        return new AgentDefinition
        {
            Composer = composer ?? new TemplateLureComposer(),
            Gate = gate ?? new LayeredContentSafetyGate(screen),
            Responder = responder ?? new RuleWeightedResponder(),
            Judge = judge ?? new HeuristicReplyJudge(),
            Sanitizer = sanitizer ?? new LogTextSanitizer(screen),
            Time = time ?? new FixedTime(),
        };
    }

    /// <summary>Creates a scratch directory that the caller is responsible for deleting.</summary>
    public static string TempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(), "swarmrt-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(path);
        return path;
    }
}

/// <summary>A clock that does not move, so timestamps in assertions are stable.</summary>
internal sealed class FixedTime(DateTimeOffset? now = null) : TimeProvider
{
    private readonly DateTimeOffset _now =
        now ?? new DateTimeOffset(2026, 7, 29, 18, 42, 11, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;
}

/// <summary>A composer that emits a fixed stub, used to trace text through the pipeline.</summary>
internal sealed class FixedComposer(string message, string summary) : ILureComposer
{
    public string Description => "fixed test composer";

    public int Calls { get; private set; }

    public Task<ComposedLure> ComposeAsync(
        AgentAssignment assignment,
        Employee target,
        PretextType pretext,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(new ComposedLure(message, summary, pretext.Channel));
    }
}

/// <summary>A responder that always returns the same reply text.</summary>
internal sealed class FixedResponder(string reply, ReplyBehavior? behavior = null) : IEmployeeResponder
{
    public string Description => "fixed test responder";

    public int Calls { get; private set; }

    public Task<SimulatedReply> RespondAsync(
        ComposedLure lure,
        Employee target,
        PretextType pretext,
        AgentAssignment assignment,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(new SimulatedReply { Text = reply, Behavior = behavior });
    }
}

/// <summary>Records the reply text it was given, to prove what the judge can and cannot see.</summary>
internal sealed class RecordingJudge(bool favorable = false) : IReplyJudge
{
    public string Description => "recording test judge";

    public List<string> SeenReplies { get; } = [];

    public Task<ReplyJudgment> JudgeAsync(
        string replyText,
        AgentAssignment assignment,
        PretextType pretext,
        CancellationToken cancellationToken = default)
    {
        SeenReplies.Add(replyText);
        return Task.FromResult(new ReplyJudgment(
            favorable, favorable ? "Target acted on it." : "Target did not act on it."));
    }
}

/// <summary>A composer that throws, for exercising the orchestrator's failure path.</summary>
internal sealed class FailingComposer(string message = "backend unavailable") : ILureComposer
{
    public string Description => "failing test composer";

    public Task<ComposedLure> ComposeAsync(
        AgentAssignment assignment,
        Employee target,
        PretextType pretext,
        CancellationToken cancellationToken = default) =>
        throw new ModelCallException(message);
}
