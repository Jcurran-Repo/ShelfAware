using Microsoft.AspNetCore.Components.Forms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ShelfAware.Core.Recipes;
using ShelfAware.Web.Components.Pages;
using ShelfAware.Web.Data;
using ShelfAware.Web.Services;
using ShelfAware.Web.Tests;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The recipe-import page (/cookbook/import) — extraction is ALWAYS reviewed before it's saved, never
/// trusted silently. The rules under test: a successful import lands on an editable review; the edits the
/// human makes are what gets written; a "no recipe found" result says so and saves nothing; the tags come
/// through the shared vocabulary; and a photo import keeps that photo as the recipe's picture.
/// </summary>
public class RecipeImportPageTests : PageTestContext
{
    private readonly FakeRecipeImporter _importer = new();
    private readonly StubPhotoLoader _photoLoader = new();
    private readonly string _imageDir =
        Path.Combine(Path.GetTempPath(), "shelfaware-import-test", Guid.NewGuid().ToString("N"));

    protected override void RegisterAdditionalServices()
    {
        Services.AddSingleton<IRecipeImporter>(_importer);
        Services.AddSingleton<IShelfPhotoLoader>(_photoLoader);
        Services.AddSingleton(new RecipeImageStorage(
            new AppPaths(_imageDir, Path.Combine(_imageDir, "receipts")),
            new FakeCurrentHousehold("hh-test"),
            NullLogger<RecipeImageStorage>.Instance));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        try { Directory.Delete(_imageDir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }
    }

    private static RecipeImportResult SoupRecipe() => RecipeImportResult.Ok(new ImportedRecipe(
        "Test Soup", "Yum", 300,
        [new ImportedIngredient("Carrots", "2", true), new ImportedIngredient("Salt", null, false)],
        ["Chop it.", "Boil it."],
        ["Dinner", "Soup"]));

    private IRenderedComponent<RecipeImport> ImportFromText(RecipeImportResult result, string text = "paste this")
    {
        _importer.NextText = result;
        var cut = Render<RecipeImport>();
        cut.Find(".import-paste").Input(text); // @bind:event=oninput
        cut.Find(".import-actions button").Click(); // "Import recipe"
        return cut;
    }

    private static void ClickSave(IRenderedComponent<RecipeImport> cut) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Save recipe").Click();

    [Fact]
    public void Importing_from_text_lands_on_the_editable_review()
    {
        var cut = ImportFromText(SoupRecipe());

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Check it over", cut.Markup);
            Assert.Equal("Test Soup", cut.Find("input[aria-label='Recipe name']").GetAttribute("value"));
        });
        Assert.Equal(2, cut.FindAll(".import-ingredient-row").Count);
        Assert.Equal(2, cut.FindAll(".import-step-row").Count);
    }

    [Fact]
    public void Saving_writes_the_recipe_with_its_ingredients_steps_and_tags()
    {
        var cut = ImportFromText(SoupRecipe());
        cut.WaitForAssertion(() => Assert.Contains("Check it over", cut.Markup));
        ClickSave(cut);

        cut.WaitForAssertion(() => Assert.Contains("Saved", cut.Markup));
        using var db = Db.CreateDbContext();
        var recipe = db.Recipes.Include(r => r.Ingredients).Include(r => r.Steps).Include(r => r.Tags).Single();
        Assert.Equal("Test Soup", recipe.Name);
        Assert.Equal(2, recipe.Ingredients.Count);
        Assert.Equal(2, recipe.Steps.Count);
        Assert.Contains("Dinner", recipe.Tags.Select(t => t.Value));
    }

    [Fact]
    public void A_found_false_import_says_no_recipe_and_saves_nothing()
    {
        var cut = ImportFromText(RecipeImportResult.Fail("No recipe found — try a clearer photo."));

        cut.WaitForAssertion(() => Assert.Contains("No recipe found", cut.Markup));
        using var db = Db.CreateDbContext();
        Assert.Empty(db.Recipes.ToList());
    }

    [Fact]
    public void The_humans_edit_to_the_draft_is_what_gets_written()
    {
        var cut = ImportFromText(SoupRecipe());
        cut.WaitForAssertion(() => Assert.Contains("Check it over", cut.Markup));

        cut.Find("input[aria-label='Recipe name']").Change("My Edited Soup"); // @bind default = onchange
        ClickSave(cut);

        cut.WaitForAssertion(() => Assert.Contains("Saved", cut.Markup));
        using var db = Db.CreateDbContext();
        Assert.Equal("My Edited Soup", db.Recipes.Single().Name);
    }

    [Fact]
    public void Importing_from_a_photo_keeps_it_as_the_recipe_image()
    {
        _importer.NextImage = SoupRecipe();

        var cut = Render<RecipeImport>();
        cut.FindComponent<InputFile>().UploadFiles(
            InputFileContent.CreateFromBinary([1, 2, 3], "recipe.jpg", contentType: "image/jpeg"));
        cut.WaitForAssertion(() => Assert.Contains("Photo ready", cut.Markup));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Import recipe")).Click();
        cut.WaitForAssertion(() => Assert.Contains("Check it over", cut.Markup));
        ClickSave(cut);

        cut.WaitForAssertion(() => Assert.Contains("Saved", cut.Markup));
        using var db = Db.CreateDbContext();
        Assert.NotNull(db.Recipes.Single().ImagePath); // the imported photo became the recipe's picture
        Assert.Equal(1, _importer.ImageCalls);
    }
}

/// <summary>A scriptable recipe importer for the import-page tests — returns whatever result was set for
/// the image or text path, and counts calls.</summary>
internal sealed class FakeRecipeImporter : IRecipeImporter
{
    public RecipeImportResult NextImage { get; set; } = RecipeImportResult.Fail("no image result set");
    public RecipeImportResult NextText { get; set; } = RecipeImportResult.Fail("no text result set");
    public int ImageCalls { get; private set; }
    public int TextCalls { get; private set; }

    public Task<RecipeImportResult> ImportFromImageAsync(RecipePhoto image, CancellationToken cancellationToken = default)
    {
        ImageCalls++;
        return Task.FromResult(NextImage);
    }

    public Task<RecipeImportResult> ImportFromTextAsync(string text, CancellationToken cancellationToken = default)
    {
        TextCalls++;
        return Task.FromResult(NextText);
    }
}
