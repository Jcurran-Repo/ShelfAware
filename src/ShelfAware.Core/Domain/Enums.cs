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
    Restocked
}
