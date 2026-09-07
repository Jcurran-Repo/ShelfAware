using ShelfAware.Core.Domain;

namespace ShelfAware.Tests;

public class LookalikeClusterTests
{
    [Fact]
    public void A_new_row_starts_with_an_empty_head_never_null()
    {
        // The head is the row's identity (the unique (household, head) index keys on it); the default must be
        // the empty string, never null, so an unset row can't slip a NULL past a NOT NULL column.
        var row = new LookalikeCluster();

        Assert.Equal("", row.Head);
        Assert.Null(row.DismissedAt);
    }
}
