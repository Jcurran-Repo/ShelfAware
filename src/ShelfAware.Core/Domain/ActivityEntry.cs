namespace ShelfAware.Core.Domain;

/// <summary>One thing a household did that the activity log can show and — for most kinds — reverse.
/// Household-owned like any pantry row: the household makes it, sees it on /history, exports it, and
/// "delete all my data" takes it (it names products, dates, merchants, so it IS their content).
///
/// The row is deliberately self-contained. <see cref="Summary"/> is the human line, computed by the
/// kind's handler AT RECORD TIME and then STORED, so no surface re-derives it and the wording lives in
/// exactly one place. <see cref="PayloadJson"/> is the kind-specific data its handler needs both to
/// REVERSE the action and to notice the world has moved on since (the precondition check — see
/// <c>IUndoHandler</c>). An undo never re-reads or re-computes the summary; the one place the wording
/// lives is the handler.</summary>
public class ActivityEntry : IHouseholdOwned
{
    public int Id { get; set; }
    public string? HouseholdId { get; set; }

    /// <summary>When the action happened. Also the reference an undo's precondition orders against: a
    /// count re-attested AFTER this is a change since, so the count-reversal stands down (mirroring how
    /// <c>ReceiptRemovalService</c> orders a subtract against a receipt's confirm time).</summary>
    public DateTimeOffset OccurredAt { get; set; }

    public ActivityKind Kind { get; set; }

    /// <summary>The human-readable line shown on /history and in the inline "↩ Undo" notice. Produced by
    /// the kind's handler when the entry is recorded, then rendered verbatim — the wording exists once.</summary>
    public required string Summary { get; set; }

    /// <summary>Whether this KIND can ever be reversed (merge / receipt-removal / census are history-only).
    /// Comes from the handler; drives the page's greying. Distinct from whether THIS entry is currently
    /// undoable — that also depends on <see cref="UndoneAt"/> and the live precondition the handler checks.</summary>
    public Reversibility Reversibility { get; set; }

    /// <summary>When this entry was reversed; null until then. Stamped by <c>ActivityLogService</c> on a
    /// successful undo, in the SAME transaction as the reversal, so a refused undo leaves it null.</summary>
    public DateTimeOffset? UndoneAt { get; set; }

    /// <summary>The kind-specific reverse-and-check data, serialized by the handler. Opaque to everything
    /// but that handler — the log backbone never looks inside it.</summary>
    public required string PayloadJson { get; set; }

    /// <summary>Which surface the action came from ("Manual" from the dashboard, "Chat", a page name…),
    /// for the summary's context. Free-form and optional — a label, not a key.</summary>
    public string? Source { get; set; }
}

/// <summary>Every kind of action the activity log records, grouped by how it reverses. Handlers are
/// added per build phase; a kind with no handler yet simply has no recording call site, and recording
/// one before its handler exists fails fast (see <c>ActivityLogService.RecordAsync</c>).</summary>
public enum ActivityKind
{
    // Reversible — recorded in the IPantryStore data layer, so chat/voice actions are logged for free.
    PurchaseAdded,
    SignalRecorded,
    PurchaseQuantityEdited,
    PurchaseBrandEdited,
    CountSet,
    ExpirationSet,
    DefaultUnitSet,
    TrackingChanged,
    ProductCreated,
    TagsAdded,
    SubstitutesAdded,
    SubstituteRemoved,
    GroceryExtrasAdded,
    GroceryExtraRemoved,

    // Reversible — recorded in the confirm/edit services, reusing their existing inverses.
    MealLogged,
    ReceiptConfirmed,
    ProductRenamed,

    // Soft actions (household-approved for v1): a recipe saved or adapted, a won't-eat item changed.
    RecipeSaved,
    RecipeAdapted,
    ExcludedFoodChanged,

    // History-only — logged so the record is complete, never a clean inverse (plan §10).
    // Removing a receipt is deliberately NOT its own kind: a confirmed receipt already carries a
    // ReceiptConfirmed entry, which greys as Gone the moment it's removed, so a ReceiptRemoved entry
    // would only double-log the same narrative.
    CensusConfirmed,
    ProductsMerged,
}

/// <summary>Whether an <see cref="ActivityKind"/> can ever be undone. A history-only action (a lossy
/// merge or a census confirm) is still recorded so the log is complete, but shown greyed with no Undo
/// button.</summary>
public enum Reversibility
{
    Reversible,
    NotReversible,
}
