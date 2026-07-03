namespace ShelfAware.Core.Ingest;

/// <summary>
/// Source of incoming receipt files to auto-import. Behind an interface so the local-folder source
/// (now) can be swapped for a cloud one — Azure Blob, or a cloud drive — once the app is deployed,
/// WITHOUT touching the import logic. Same provider-seam pattern as the rest of the app.
/// </summary>
public interface IReceiptInbox
{
    /// <summary>True when a source is set up (e.g. a folder path is configured and exists).</summary>
    Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default);

    /// <summary>Every receipt file currently available in the inbox.</summary>
    Task<IReadOnlyList<InboxItem>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Read one item's bytes by its <see cref="InboxItem.Id"/>.</summary>
    Task<byte[]> ReadAsync(string id, CancellationToken cancellationToken = default);
}

/// <param name="Id">Stable identifier (e.g. the file name) — used both to detect already-imported items
/// and to read the bytes back.</param>
public record InboxItem(string Id, string Name, string MediaType);
