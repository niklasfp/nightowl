using NightOwl.Cli;
using Shouldly;

namespace NightOwl.Tests;

public class DurationParserTests
{
    [Test]
    [Arguments("30s", 0, 0, 30)]
    [Arguments("90m", 0, 90, 0)]
    [Arguments("2h", 2, 0, 0)]
    [Arguments("1h30m", 1, 30, 0)]
    [Arguments("1h30m15s", 1, 30, 15)]
    [Arguments("0s", 0, 0, 0)]
    [Arguments("  2h  ", 2, 0, 0)]
    [Arguments("2H", 2, 0, 0)]
    public async Task Parses_valid_durations(string input, int h, int m, int s)
    {
        DurationParser.TryParse(input, out var duration).ShouldBeTrue();
        await Assert.That(duration).IsEqualTo(new TimeSpan(h, m, s));
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("abc")]
    [Arguments("30")]      // missing unit
    [Arguments("30x")]     // unknown unit
    [Arguments("-5s")]     // negative
    [Arguments("1h30")]    // trailing number without unit
    [Arguments("s")]       // unit without number
    [Arguments("1.5h")]    // fractional not supported
    public void Rejects_invalid_durations(string input)
    {
        DurationParser.TryParse(input, out var duration).ShouldBeFalse();
        duration.ShouldBe(TimeSpan.Zero);
    }
}
