using AlexisScreenPro.Core;

namespace AlexisScreenPro.Models;

public sealed record AutoStopOption(int Minutes, string Label)
{
    public string ShortLabel => Minutes == 0 ? "Off" : $"{Minutes} min";

    public override string ToString() => Label;

    public static IReadOnlyList<AutoStopOption> All { get; } = AppConstants.AutoStopChoicesMinutes
        .Select(minutes => new AutoStopOption(
            minutes,
            minutes switch
            {
                0 => "No limit",
                1 => "1 minute",
                60 => "1 hour",
                _ => $"{minutes} minutes"
            }))
        .ToArray();

    public static AutoStopOption FromMinutes(int minutes) =>
        All.FirstOrDefault(option => option.Minutes == minutes) ?? All[0];
}
