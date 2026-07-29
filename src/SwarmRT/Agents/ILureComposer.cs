using SwarmRT.Contracts;
using SwarmRT.Org;

namespace SwarmRT.Agents;

/// <summary>
/// Step 1 of the agent's single attempt (design §3.2): compose one lure for the
/// assigned pretext. Implementations must return a short simulation stub carrying
/// the <c>[SIMULATED]</c> label — the safety gate rejects anything that reads as
/// ready-to-send copy.
/// </summary>
public interface ILureComposer
{
    /// <summary>How composition was performed, recorded in the report's method section.</summary>
    string Description { get; }

    Task<ComposedLure> ComposeAsync(
        AgentAssignment assignment,
        Employee target,
        PretextType pretext,
        CancellationToken cancellationToken = default);
}
