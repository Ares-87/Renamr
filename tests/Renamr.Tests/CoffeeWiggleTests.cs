using Renamr.Presentation.Animation;
using Renamr.Presentation.Services;

namespace Renamr.Tests;

public class CoffeeWiggleTests
{
    [Fact]
    public void The_cup_rests_before_and_after_the_wiggle()
    {
        Assert.Equal((0d, 0d, CoffeeWiggle.Rest), CoffeeWiggle.At(TimeSpan.Zero));
        Assert.Equal((0d, 0d, CoffeeWiggle.Rest), CoffeeWiggle.At(CoffeeWiggle.Duration));
        Assert.Equal((0d, 0d, CoffeeWiggle.Rest), CoffeeWiggle.At(CoffeeWiggle.Duration * 2));
    }

    [Fact]
    public void The_cup_moves_gently_while_it_wiggles()
    {
        var poses = Enumerable.Range(1, 99).Select(i => CoffeeWiggle.At(CoffeeWiggle.Duration * (i / 100.0))).ToList();
        Assert.Contains(poses, p => Math.Abs(p.Angle) > 5);
        Assert.All(poses, p =>
        {
            Assert.InRange(p.Angle, -14, 14);
            Assert.InRange(p.SteamRise, 0, 3);
            Assert.InRange(p.SteamOpacity, CoffeeWiggle.Rest, 1);
        });
    }

    [Fact]
    public void The_donation_link_is_the_paypal_form() =>
        Assert.StartsWith("https://www.paypal.com/donate/", AppLinks.Donate, StringComparison.Ordinal);
}
