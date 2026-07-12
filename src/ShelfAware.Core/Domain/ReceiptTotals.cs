namespace ShelfAware.Core.Domain;

/// <summary>What a receipt cost, summed from its line items. <see cref="UnpricedLines"/> counts the
/// lines extraction couldn't price — surfaced so a displayed total is honest about what it omits.</summary>
public record ReceiptTotal(decimal Total, int PricedLines, int UnpricedLines)
{
    public int LineCount => PricedLines + UnpricedLines;
}

/// <summary>
/// Money math over receipt lines, shared by anything that shows what a receipt cost. A line's total
/// is unit price × quantity — for weight-priced items the quantity IS the weight (e.g. 2.31 lb at
/// $1.99/lb), so the same formula holds. Extraction stores unit prices rounded to cents, so a
/// multi-quantity line can differ from the printed line total by a cent or two; that's inherent to
/// per-unit storage, not an extraction error.
/// </summary>
public static class ReceiptTotals
{
    /// <summary>Unit price × quantity, or null when the line carries no price.</summary>
    public static decimal? LineTotal(ReceiptLine line) =>
        line.UnitPrice is { } unitPrice ? unitPrice * line.Quantity : null;

    public static ReceiptTotal Summarize(IEnumerable<ReceiptLine> lines)
    {
        decimal total = 0;
        int priced = 0, unpriced = 0;
        foreach (var line in lines)
        {
            if (LineTotal(line) is { } lineTotal) { total += lineTotal; priced++; }
            else unpriced++;
        }
        return new ReceiptTotal(total, priced, unpriced);
    }
}
