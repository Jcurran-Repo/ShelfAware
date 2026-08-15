using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace ShelfAware.Web.Services;

/// <summary>
/// Reads a file the visitor just picked into memory, classifying every way that can go wrong — ONE
/// reading of "what happened to this file" for every page that stages photos at selection time
/// (the receipt upload and the shelf census).
/// <para>Why selection time at all: appending across camera snaps requires it. Re-activating a file
/// input replaces the element's JS-side file map, so an <see cref="IBrowserFile"/> kept from the
/// PREVIOUS change event is dead by the time a second snap arrives — the only way "snap, snap, snap"
/// can work is to turn each file into bytes while its handle is still alive, inside its own change
/// event. Once staged, extraction and the census read touch no browser handle at all.</para>
/// <para>The teardown-vs-alive discipline is the census page's, hoisted so the receipt page can't
/// drift from it: only the PAGE's own token means "the visitor left" (swallow silently); the same
/// exception while the page is alive is a failure the visitor must hear about, because an event
/// handler that rethrows gets swallowed by ComponentBase and renders nothing. This method therefore
/// never throws — every outcome is a <see cref="Result"/>.</para>
/// </summary>
internal static class PickedFileReader
{
    internal enum Kind
    {
        Loaded,
        /// <summary>Refused by type before any read — the message is the loader's own sentence,
        /// which names the file and what to use instead.</summary>
        Refused,
        /// <summary>The read failed while the page was alive (an undecodable image timing out, a
        /// stream error). Logged; the message names the file.</summary>
        Failed,
        /// <summary>The page's own token cancelled the read — the visitor left. Callers bail
        /// silently: there is nobody to tell and nothing to clean up.</summary>
        TornDown,
        /// <summary>The browser connection dropped mid-read while the page is still alive. Callers
        /// should stop the whole selection — the remaining files' handles ride the same dead
        /// connection, and looping on would log one error per file for one event.</summary>
        ConnectionLost,
    }

    internal readonly record struct Result(Kind Kind, byte[]? Bytes, string? MediaType, string? Message);

    /// <summary>A photo, downscaled in the browser via the shared <see cref="IShelfPhotoLoader"/> —
    /// the one definition of picked-photo-to-bytes (prefix content-type guard, HEIC handling, the
    /// bounded resize) for both pages.</summary>
    internal static Task<Result> ReadPhotoAsync(
        IShelfPhotoLoader loader, IBrowserFile file, CancellationToken pageToken, ILogger logger) =>
        GuardedAsync(async () =>
        {
            var photo = await loader.LoadAsync(file, pageToken);
            return (photo.Data, photo.MediaType);
        }, file, pageToken, logger);

    /// <summary>A file taken as-is (the receipt page's PDFs) — no resize, just a bounded copy into
    /// memory under the same failure classification as the photo path.</summary>
    internal static Task<Result> ReadRawAsync(
        IBrowserFile file, long maxBytes, string mediaType, CancellationToken pageToken, ILogger logger) =>
        GuardedAsync(async () =>
        {
            // `await using` on the stream: disposing a browser file stream is what releases the
            // JS-side reference, and the class has no finalizer (same note as the photo loader).
            using var buffer = new MemoryStream();
            await using (var source = file.OpenReadStream(maxBytes, pageToken))
            {
                await source.CopyToAsync(buffer, pageToken);
            }
            return (buffer.ToArray(), mediaType);
        }, file, pageToken, logger);

    private static async Task<Result> GuardedAsync(
        Func<Task<(byte[] Bytes, string MediaType)>> read,
        IBrowserFile file, CancellationToken pageToken, ILogger logger)
    {
        try
        {
            var (bytes, mediaType) = await read();
            return new(Kind.Loaded, bytes, mediaType, null);
        }
        // ⚠️ Only the PAGE's cancellation is teardown. Nothing else on a selection-time read cancels
        // on purpose (the loader's 30s bound raises TimeoutException, not this), so a cancellation
        // that arrives while the page is alive falls through to the generic clause as the failure
        // it is, instead of being mistaken for the visitor leaving.
        catch (OperationCanceledException) when (pageToken.IsCancellationRequested)
        {
            return new(Kind.TornDown, null, null, null);
        }
        // JSDisconnectedException does NOT derive from JSException — it needs its own clauses, split
        // teardown-vs-alive exactly like cancellation (the census page's hard-won shape).
        catch (JSDisconnectedException) when (pageToken.IsCancellationRequested)
        {
            return new(Kind.TornDown, null, null, null);
        }
        catch (JSDisconnectedException ex)
        {
            logger.LogError(ex, "Reading picked file {FileName} lost the browser connection.", file.Name);
            return new(Kind.ConnectionLost, null, null,
                "Sorry — the connection dropped while reading that. Please try again.");
        }
        catch (NotSupportedException ex)
        {
            // The loader refused the file by type. Its message names the file and what to use
            // instead, which is the whole reason it checks rather than letting the file time out.
            logger.LogInformation("A picked file was refused: {Reason}", ex.Message);
            return new(Kind.Refused, null, null, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Reading picked file {FileName} failed.", file.Name);
            return new(Kind.Failed, null, null,
                $"“{file.Name}” couldn't be read — try taking or picking it again.");
        }
    }
}
