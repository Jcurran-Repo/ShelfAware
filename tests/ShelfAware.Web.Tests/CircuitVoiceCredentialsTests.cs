using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Speech;
using ShelfAware.Llm;
using ShelfAware.Web.Services;

namespace ShelfAware.Web.Tests;

/// <summary>Per-circuit voice keys, the ElevenLabs parallel of CircuitAiSettings: server config is the
/// fallback, the visitor's browser overrides it — except on a managed deployment, where the host's key
/// is authoritative and browser creds must be ignored (the same devtools-injection posture ByokTests
/// pins for the AI key).</summary>
public class CircuitVoiceCredentialsTests
{
    private static CircuitVoiceCredentials Creds(
        string serverVoiceKey = "server-el-key", string llmKeyMode = "Byok", string? ear = null) =>
        new(
            Options.Create(new ElevenLabsOptions { ApiKey = serverVoiceKey, AgentId = "server-agent" }),
            Options.Create(new LlmOptions { ApiKey = "host-key", KeyMode = llmKeyMode }),
            new ConfigurationBuilder()
                .AddInMemoryCollection(ear is null ? [] : new Dictionary<string, string?> { ["Speech:Ear"] = ear })
                .Build());

    // ---- Which ear this box runs, which is what decides whether a mic is offered at all ----------

    [Fact]
    public void An_unset_ear_is_the_cloud_one_so_no_deployment_changes_on_upgrade()
    {
        Assert.False(Creds().LocalEar);
        Assert.False(Creds(ear: "ElevenLabs").LocalEar);
    }

    [Fact]
    public void A_moonshine_box_reports_a_local_ear_and_so_hears_with_no_key_at_all()
    {
        // ⚠️ The demo box: managed, no ElevenLabs key, Speech:Ear=Moonshine. Read through the SAME
        // SpeechRegistration.EarOf that registration uses — two readings of one setting is how a box
        // ends up offering a microphone one of them then refuses to use.
        var creds = Creds(serverVoiceKey: "", llmKeyMode: "Managed", ear: "Moonshine");

        Assert.True(creds.LocalEar);
        Assert.Equal(EarAvailability.Ready, ((IVoiceCredentials)creds).Ear);
    }

    [Fact]
    public void A_managed_box_with_no_key_and_no_local_ear_cannot_hear()
    {
        var creds = Creds(serverVoiceKey: "", llmKeyMode: "Managed");

        Assert.False(creds.LocalEar);
        Assert.Equal(EarAvailability.NotOnThisBox, ((IVoiceCredentials)creds).Ear);
    }

    [Fact]
    public void Defaults_to_the_server_config()
    {
        var creds = Creds();

        Assert.Equal("server-el-key", creds.ApiKey);
        Assert.Equal("server-agent", creds.AgentId);
        Assert.False(creds.FromBrowser);
    }

    [Fact]
    public void A_visitor_key_overrides_on_a_byok_deployment_and_reset_reverts()
    {
        var creds = Creds();

        creds.Apply("visitor-el-key", "visitor-agent");
        Assert.Equal("visitor-el-key", creds.ApiKey);
        Assert.Equal("visitor-agent", creds.AgentId);
        Assert.True(creds.FromBrowser);

        creds.Reset(); // "forget my keys"
        Assert.Equal("server-el-key", creds.ApiKey);
        Assert.Equal("server-agent", creds.AgentId);
        Assert.False(creds.FromBrowser);
    }

    [Fact]
    public void A_managed_deployment_ignores_browser_credentials()
    {
        // Stale localStorage or a devtools injection must not swap the voice key out from under a
        // managed host — mint quotas and billing are scoped to the HOST's account there.
        var creds = Creds(llmKeyMode: "Managed");

        creds.Apply("sneaky-visitor-key", "sneaky-agent");

        Assert.Equal("server-el-key", creds.ApiKey);
        Assert.Equal("server-agent", creds.AgentId);
        Assert.False(creds.FromBrowser);
    }

    [Fact]
    public void Null_browser_values_clear_rather_than_keep_the_fallback()
    {
        // Applying browser creds is a STATEMENT of what the visitor has — null means "none". Keeping
        // the server fallback here would spend the host's voice key for a visitor who brought nothing.
        var creds = Creds();

        creds.Apply(null, null);

        Assert.Equal("", creds.ApiKey);
        Assert.Equal("", creds.AgentId);
        Assert.True(creds.FromBrowser);
    }
}
