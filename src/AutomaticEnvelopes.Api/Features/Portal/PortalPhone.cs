namespace AutomaticEnvelopes.Api.Features.Portal;

public static class PortalPhone
{
    public static string Mask(string e164)
    {
        var digits = new string(e164.Where(char.IsDigit).ToArray());
        if (digits.Length == 0)
        {
            return "Número eliminado";
        }

        if (digits.StartsWith("34", StringComparison.Ordinal) && digits.Length == 11)
        {
            var local = digits[2..];
            return $"+34 {local[..3]} ** ** {local[^2..]}";
        }

        var cc = CountryCode(digits);
        var rest = digits[cc.Length..];
        if (rest.Length < 4)
        {
            return $"+{cc} ***";
        }

        return $"+{cc} {rest[..2]}** *** *{rest[^2..]}";
    }

    private static string CountryCode(string digits)
    {
        if (digits.StartsWith("44", StringComparison.Ordinal))
        {
            return "44";
        }

        if (digits.StartsWith("33", StringComparison.Ordinal))
        {
            return "33";
        }

        return digits.Length >= 2 ? digits[..2] : digits;
    }
}
