namespace ShelfAware.Web.Undo;

/// <summary>Registers the activity log + undo. Kept as one extension so <c>Program.cs</c> wires it with
/// a single line and the registration test resolves the exact same handler set.</summary>
public static class UndoServiceCollectionExtensions
{
    public static IServiceCollection AddActivityLog(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ActivityLogOptions>(configuration.GetSection(ActivityLogOptions.SectionName));
        // ActivityLogService is the concrete type pages/tests use (UndoAsync, GetHistoryAsync); IActivityLog
        // is the recording seam the data layer sees. Same scoped instance behind both.
        services.AddScoped<ActivityLogService>();
        services.AddScoped<IActivityLog>(sp => sp.GetRequiredService<ActivityLogService>());
        AddHandlers(services);
        return services;
    }

    /// <summary>THE list of undo handlers — one per <c>ActivityKind</c> with a recording call site. Add a
    /// handler here when you add an action (the build order grows this list phase by phase). Recording a
    /// kind whose handler isn't here fails fast in <c>ActivityLogService.RecordAsync</c>.</summary>
    public static void AddHandlers(IServiceCollection services)
    {
        services.AddScoped<IUndoHandler, PurchaseAddedHandler>();
        services.AddScoped<IUndoHandler, SignalRecordedHandler>();
        services.AddScoped<IUndoHandler, PurchaseQuantityEditedHandler>();
        services.AddScoped<IUndoHandler, PurchaseBrandEditedHandler>();
        services.AddScoped<IUndoHandler, DefaultUnitSetHandler>();
        services.AddScoped<IUndoHandler, TrackingChangedHandler>();
        services.AddScoped<IUndoHandler, CountSetHandler>();
        services.AddScoped<IUndoHandler, ProductCreatedHandler>();
        services.AddScoped<IUndoHandler, ExpirationSetHandler>();
        services.AddScoped<IUndoHandler, TagsAddedHandler>();
        services.AddScoped<IUndoHandler, SubstitutesAddedHandler>();
        services.AddScoped<IUndoHandler, GroceryExtrasAddedHandler>();
        services.AddScoped<IUndoHandler, ProductRenamedHandler>();
        services.AddScoped<IUndoHandler, MealLoggedHandler>();
        services.AddScoped<IUndoHandler, ReceiptConfirmedHandler>();
    }
}
