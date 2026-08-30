using ShelfAware.Web.Wishlist;

namespace ShelfAware.Web.Tests;

/// <summary>The Founder pre-order gate: the button (and its whole block) exists ONLY when a valid https
/// payment link is configured — a button that can't charge is worse than none. This is the one piece of
/// "hide it until payments work" that's pure logic; the page's use of it is verified live.</summary>
public class WishlistOptionsTests
{
    [Theory]
    [InlineData(null)]                        // unset — the normal state
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("/relative/pay")]             // not absolute
    [InlineData("ftp://example.com/pay")]     // wrong scheme
    [InlineData("http://example.com/pay")]    // not https — a payment link must be secure
    public void The_founder_preorder_stays_hidden_without_a_valid_https_link(string? url)
    {
        Assert.False(new WishlistOptions { FounderPaymentUrl = url }.FounderPreorderConfigured);
    }

    [Fact]
    public void A_valid_https_link_reveals_the_founder_preorder()
    {
        Assert.True(new WishlistOptions { FounderPaymentUrl = "https://buy.stripe.com/test_abc123" }.FounderPreorderConfigured);
    }
}
