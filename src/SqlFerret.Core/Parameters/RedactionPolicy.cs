using SqlFerret.Core.Normalization;

namespace SqlFerret.Core.Parameters;

public enum RedactionMode { Off, Hash, Masked, Full }

public class RedactionPolicy(RedactionMode mode, IReadOnlyList<string>? sensitiveNameSubstrings = null)
{
    private static readonly string[] DefaultSensitive = ["password", "token", "secret", "email"];
    private readonly IReadOnlyList<string> _sensitive = sensitiveNameSubstrings ?? DefaultSensitive;

    public (string storedValue, bool redacted) Apply(string? paramName, string valueText)
    {
        bool forced = paramName is not null &&
            _sensitive.Any(s => paramName.Contains(s, StringComparison.OrdinalIgnoreCase));

        var effective = forced ? RedactionMode.Hash : mode;
        return effective switch
        {
            RedactionMode.Off    => ("", true),
            RedactionMode.Full   => (valueText, false),
            RedactionMode.Hash   => (Fingerprint.Hash(valueText), true),
            RedactionMode.Masked => (new string('*', Math.Clamp(valueText.Length, 1, 8)), true),
            _ => (valueText, false)
        };
    }
}
