namespace Loopstructor.AutoPlayer.Updater.Models;

public sealed class SemanticVersion : IComparable<SemanticVersion>
{
    private SemanticVersion(int major, int minor, int patch, IReadOnlyList<string> preRelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public IReadOnlyList<string> PreRelease { get; }

    public static bool TryParse(string? input, out SemanticVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(input)) return false;
        string value = input.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        int buildIndex = value.IndexOf('+');
        if (buildIndex >= 0) value = value[..buildIndex];
        string[] releaseParts = value.Split('-', 2);
        string[] core = releaseParts[0].Split('.');
        if (core.Length is < 2 or > 3
            || !TryNumeric(core.ElementAtOrDefault(0), out int major)
            || !TryNumeric(core.ElementAtOrDefault(1), out int minor)
            || !TryNumeric(core.ElementAtOrDefault(2) ?? "0", out int patch))
        {
            return false;
        }

        List<string> preRelease = new();
        if (releaseParts.Length == 2)
        {
            if (string.IsNullOrWhiteSpace(releaseParts[1])) return false;
            foreach (string identifier in releaseParts[1].Split('.'))
            {
                if (identifier.Length == 0 || identifier.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
                {
                    return false;
                }

                preRelease.Add(identifier);
            }
        }

        version = new SemanticVersion(major, minor, patch, preRelease);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other == null) return 1;
        int core = Major.CompareTo(other.Major);
        if (core != 0) return core;
        core = Minor.CompareTo(other.Minor);
        if (core != 0) return core;
        core = Patch.CompareTo(other.Patch);
        if (core != 0) return core;
        if (PreRelease.Count == 0 && other.PreRelease.Count == 0) return 0;
        if (PreRelease.Count == 0) return 1;
        if (other.PreRelease.Count == 0) return -1;
        int count = Math.Max(PreRelease.Count, other.PreRelease.Count);
        for (int index = 0; index < count; index++)
        {
            if (index >= PreRelease.Count) return -1;
            if (index >= other.PreRelease.Count) return 1;
            string left = PreRelease[index];
            string right = other.PreRelease[index];
            bool leftNumeric = int.TryParse(left, out int leftNumber);
            bool rightNumeric = int.TryParse(right, out int rightNumber);
            int comparison = leftNumeric && rightNumeric
                ? leftNumber.CompareTo(rightNumber)
                : leftNumeric ? -1
                : rightNumeric ? 1
                : string.Compare(left, right, StringComparison.Ordinal);
            if (comparison != 0) return comparison;
        }

        return 0;
    }

    private static bool TryNumeric(string? value, out int result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value)
            || (value.Length > 1 && value[0] == '0')
            || !int.TryParse(value, out result)
            || result < 0)
        {
            return false;
        }

        return true;
    }
}
