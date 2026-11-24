namespace AudioController.App.Services;

/// <summary>The slot color palette: imported slots cycle through it; the editor offers it as swatches.</summary>
public static class Palette
{
    public static IReadOnlyList<string> Colors { get; } = new[]
    {
        "#4F8EF7", // blue
        "#7C5CFF", // violet
        "#C45CE6", // purple
        "#E8559A", // pink
        "#F0605D", // coral
        "#F28C38", // orange
        "#F2C94C", // yellow
        "#5CCB7A", // green
        "#2EC4B6", // teal
        "#8D99AE", // slate
    };

    public static string At(int index)
    {
        int n = Colors.Count;
        return Colors[((index % n) + n) % n];
    }
}
