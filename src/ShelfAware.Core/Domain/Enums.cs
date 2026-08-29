namespace ShelfAware.Core.Domain;

public enum Category
{
    Dairy,
    Meat,
    Produce,
    Pantry,
    Frozen,
    Beverage,
    Household,
    PetCare,
    PersonalCare,
    Other
}

public enum PurchaseSource
{
    Receipt,
    Manual,
    Chat
}

public enum ReceiptStatus
{
    PendingReview,
    Confirmed,
    Discarded
}

public enum SignalKind
{
    OutNow,
    RunningLow,
    Restocked,
    /// The honest cousin of Restocked: "I never ran out — you were early, and I still have the OLD
    /// stock, not a fresh supply." Clears an out/low it postdates but does NOT re-anchor a full cadence
    /// (that would go silent while the leftovers run out); the predictor snoozes the nag a modest slice
    /// of the cadence instead. Status-only, like Restocked — it never feeds either learned rhythm.
    StillInStock
}
