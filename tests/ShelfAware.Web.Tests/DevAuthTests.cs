using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The security property behind the dev quick-login: it can turn on ONLY in the Development
/// environment, and ONLY with the explicit flag — so no production deployment (the family box, the
/// droplet, the tailnet publish — all Production) can ever activate it, whatever its config says. If a
/// Production row here ever goes green, a dev convenience has become a real backdoor.
/// </summary>
public class DevAuthTests
{
    [Theory]
    [InlineData("Development", true, true)]    // the one on-state
    [InlineData("Development", false, false)]  // opt-in is required even in dev
    [InlineData("Production", true, false)]    // THE guarantee: never in production, flag set or not
    [InlineData("Production", false, false)]
    [InlineData("Staging", true, false)]       // Development specifically, not just "not production"
    public void Enabled_only_in_development_with_the_flag(string environment, bool flag, bool expected)
    {
        var env = new FakeEnv(environment);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [DevAuth.ConfigKey] = flag ? "true" : "false" })
            .Build();

        Assert.Equal(expected, DevAuth.IsEnabled(env, config));
    }

    [Fact]
    public void An_absent_flag_is_off_even_in_development()
    {
        // The key not being present at all is the default deploy state; it must read as off.
        var config = new ConfigurationBuilder().Build();
        Assert.False(DevAuth.IsEnabled(new FakeEnv("Development"), config));
    }

    private sealed class FakeEnv(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
