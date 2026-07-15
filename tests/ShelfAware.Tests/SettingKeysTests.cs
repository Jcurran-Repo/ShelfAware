using System.Reflection;
using ShelfAware.Core.Settings;

namespace ShelfAware.Tests;

/// <summary>
/// The settings table holds two different things — how the app is set up, and content derived from the
/// household's own pantry — and only one of them may survive "delete my data". These pin that every key
/// has been sorted into one of the two, so the next key added can't quietly default to surviving.
/// </summary>
public class SettingKeysTests
{
    private static IReadOnlyList<string> DeclaredKeys() =>
        [.. typeof(SettingKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)];

    [Fact]
    public void Every_declared_key_is_classified_exactly_once()
    {
        var classified = SettingKeys.Config.Concat(SettingKeys.UserContent).ToList();

        var unclassified = DeclaredKeys().Except(classified).ToList();
        Assert.True(unclassified.Count == 0,
            $"Unclassified setting key(s): {string.Join(", ", unclassified)}. Add each to SettingKeys.Config " +
            "(how the app is set up — survives a delete) or SettingKeys.UserContent (derived from the " +
            "household's own data — removed by 'delete my data').");

        Assert.Equal(classified.Count, classified.Distinct().Count());
    }

    [Fact]
    public void Nothing_is_classified_that_isnt_a_real_key()
    {
        var declared = DeclaredKeys();
        Assert.Empty(SettingKeys.Config.Concat(SettingKeys.UserContent).Except(declared));
    }

    [Fact]
    public void The_keys_that_hold_pantry_derived_content_are_user_content()
    {
        // Named explicitly, not just counted: these are the two that outlived a "delete my data" once.
        Assert.Contains(SettingKeys.LastRecipeSuggestions, SettingKeys.UserContent);
        Assert.Contains(SettingKeys.SelfEvalResults, SettingKeys.UserContent);
    }
}
