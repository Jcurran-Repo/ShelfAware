using System.Xml.Linq;

namespace ShelfAware.Core.Evaluation;

/// <summary>Reads the &lt;Counters&gt; totals out of a VSTest <c>.trx</c> file — the per-project numbers CI
/// folds into a <see cref="TestStatusReport"/>. Defensive: a file with no summary, or a non-numeric
/// counter, reads as zero rather than throwing, so one odd file can't sink the generator.</summary>
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

        // TRX reports skipped tests as notExecuted; total is the authoritative count.
        return new TestProjectResult(projectName, Count("total"), Count("passed"), Count("failed"), Count("notExecuted"));
    }
}
