using System.Xml.Linq;

namespace ShelfAware.Core.Evaluation;

/// <summary>Reads the &lt;Counters&gt; totals out of a VSTest <c>.trx</c> file — the per-project numbers CI
/// folds into a <see cref="TestStatusReport"/>. Defensive about the counters themselves: a file with no
/// summary, or a non-numeric counter, reads as zero rather than throwing. "Failed" sums every non-passing
/// completed outcome (failed + error + timed-out + aborted), so a green card can't hide an errored test.</summary>
public static class TrxSummary
{
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    public static TestProjectResult Parse(string projectName, string trxXml)
    {
        var counters = XDocument.Parse(trxXml).Root?
            .Element(Ns + "ResultSummary")?
            .Element(Ns + "Counters");

        int Count(string attribute) =>
            int.TryParse(counters?.Attribute(attribute)?.Value, out var v) ? v : 0;

        // TRX reports skipped tests as notExecuted; "failed" is only assertion failures, so fold the other
        // non-passing completed outcomes in too — else a run with an errored/timed-out test reads green.
        var failed = Count("failed") + Count("error") + Count("timeout") + Count("aborted");
        return new TestProjectResult(projectName, Count("total"), Count("passed"), failed, Count("notExecuted"));
    }
}
