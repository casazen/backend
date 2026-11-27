namespace Casazen.Infrastructure.Identity;

/// <summary>How the comparison of the portrait of the document with the frames the server chose came out (E72/F01/US02/T02, NF-193).</summary>
public enum MatchState
{
    /// <summary>The portrait and the frames were compared: the numbers of the report are the comparison.</summary>
    Ok,

    /// <summary>No face was found in the document, in any orientation: there is nothing to compare the guest with.</summary>
    NoPortrait,

    /// <summary>The comparison could not be made (no policy, the models are not ready, one failed or the time was over): never a match, and a person decides.</summary>
    Unavailable,
}

/// <summary>
/// The result of the comparison of the portrait of the document with the frames chosen by the server (E72/F01/US02/T02, NF-193, decision D7): the similarity of every chosen
/// frame in the scale of the check-in (the cosine of the two face vectors, 0 to 1), combined by the aggregation rule of the policy, and what the thresholds of the policy say of
/// it. <b>Numbers of a person:</b> they go to the decision and to the log of the decision (E73), never to a log of the application. The report has no presence input and takes none:
/// the comparison and the presence are independent conditions and no value of one compensates a failure of the other; the AND is applied by the decision engine.
/// </summary>
/// <param name="State">How it came out; the numbers below mean something only when it is <see cref="MatchState.Ok"/> (they are 0 and false otherwise).</param>
/// <param name="Aggregate">The similarities of the chosen frames combined by <c>match.aggregation</c> (<c>median</c>, <c>min</c> or <c>mean</c>).</param>
/// <param name="Min">The lowest similarity of a single chosen frame (a frame in which no face was found counts as 0).</param>
/// <param name="FrameCount">How many frames were compared.</param>
/// <param name="AcceptPassed">True when <paramref name="Aggregate"/> reaches <c>match.accept</c> AND <paramref name="Min"/> reaches <c>match.minEachFrame</c>: one weak frame fails it however high the aggregate is.</param>
/// <param name="InReviewBand">
/// True when <paramref name="Aggregate"/> is from <c>match.reviewFloor</c> up to, not including, <c>match.accept</c>: close enough that a person should look. Reported apart from
/// <paramref name="AcceptPassed"/>; an aggregate that reaches <c>match.accept</c> with a weak frame is neither (it is not accepted, and it is not in the band).
/// </param>
public sealed record MatchReport(MatchState State, double Aggregate, double Min, int FrameCount, bool AcceptPassed, bool InReviewBand)
{
    /// <summary>The report of a document with no face in it: nothing compared, never a match.</summary>
    public static MatchReport NoPortrait()
    {
        return new MatchReport(MatchState.NoPortrait, 0, 0, 0, false, false);
    }

    /// <summary>The report of a comparison that could not be made: nothing compared, never a match.</summary>
    public static MatchReport NotAvailable()
    {
        return new MatchReport(MatchState.Unavailable, 0, 0, 0, false, false);
    }
}
