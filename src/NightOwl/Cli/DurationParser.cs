namespace NightOwl.Cli;

/// <summary>
/// Parses compact duration strings made of <c>&lt;number&gt;&lt;unit&gt;</c> segments,
/// e.g. <c>30s</c>, <c>90m</c>, <c>2h</c>, <c>1h30m</c>. Units are <c>s</c>/<c>m</c>/<c>h</c>
/// (case-insensitive). Hand-parsed — no regex, AOT-clean.
/// </summary>
internal static class DurationParser
{
    public static bool TryParse(string? input, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var span = input.AsSpan().Trim();
        long totalSeconds = 0;
        long current = 0;
        var sawDigit = false;
        var sawSegment = false;

        foreach (var c in span)
        {
            if (c is >= '0' and <= '9')
            {
                current = (current * 10) + (c - '0');
                sawDigit = true;

                // Guard against overflow on absurdly long inputs.
                if (current > TimeSpan.MaxValue.TotalSeconds)
                {
                    return false;
                }

                continue;
            }

            var unitSeconds = c switch
            {
                'h' or 'H' => 3600,
                'm' or 'M' => 60,
                's' or 'S' => 1,
                _ => 0,
            };

            if (unitSeconds == 0 || !sawDigit)
            {
                // Unknown unit, or a unit with no preceding number.
                return false;
            }

            totalSeconds += current * unitSeconds;
            current = 0;
            sawDigit = false;
            sawSegment = true;
        }

        // A trailing number with no unit (e.g. "1h30") is invalid.
        if (sawDigit || !sawSegment)
        {
            return false;
        }

        duration = TimeSpan.FromSeconds(totalSeconds);
        return true;
    }
}
