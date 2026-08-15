using Microsoft.AspNetCore.Components;
using ShelfAware.Web.Components.Layout;

namespace ShelfAware.Web.UI.Tests;

/// <summary>SiteNav ties the header nav and the bug form's "where" dropdown to one page list — but
/// nothing ties the LIST to the app's real routes, and no harness renders MainLayout's half. This
/// pins the side that can rot silently: a renamed route would leave both consumers confidently
/// agreeing on a 404.</summary>
public class SiteNavTests
{
    [Fact]
    public void Every_nav_href_is_a_declared_page_route()
    {
        var routes = typeof(SiteNav).Assembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributes(typeof(RouteAttribute), inherit: false)
                .Cast<RouteAttribute>())
            .Select(r => r.Template)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in SiteNav.Pages)
        {
            Assert.Contains(entry.Href, routes);
        }
    }
}
