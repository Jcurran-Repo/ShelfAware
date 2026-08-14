using Microsoft.JSInterop;

namespace ShelfAware.Web.Services;

/// <summary>THE clipboard-write posture, shared by every copy control (the dashboard cards, the
/// grocery list, the invite code). A refused clipboard — denied permission, or no clipboard API on
/// a plain-HTTP origin — is an expected outcome the caller reports on screen, never an unhandled
/// exception tearing down the whole circuit over a copy click. A circuit already gone
/// (JSDisconnectedException, which is NOT a JSException) returns false quietly: there is no page
/// left to notify.</summary>
public static class ClipboardCopy
{
    public static async Task<bool> TryWriteAsync(IJSRuntime js, ILogger logger, string text)
    {
        try
        {
            await js.InvokeVoidAsync("navigator.clipboard.writeText", text);
            return true;
        }
        catch (JSDisconnectedException)
        {
            return false;
        }
        catch (JSException ex)
        {
            logger.LogWarning(ex, "Copying to the clipboard failed.");
            return false;
        }
    }
}
