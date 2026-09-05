using System.Text.RegularExpressions;
using ImajinationAPI.Models;

namespace ImajinationAPI.Services;

public static class RegistrationValidation
{
    public static bool IsValidName(string? value, int maxLength = 50, bool optional = false)
    {
        var text = value?.Trim() ?? string.Empty;
        return (optional && text.Length == 0) || (text.Length >= 1 && text.Length <= maxLength
            && Regex.IsMatch(text, @"^[\p{L}][\p{L}\p{M} .'\u2019-]*$"));
    }

    public static bool IsValidEmail(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        var at = text.IndexOf('@');
        return text.Length <= 254 && at > 0 && at <= 64
            && Regex.IsMatch(text, @"^[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+(?:\.[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+)*@[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)+$");
    }

    public static string? Validate(RegisterDto req)
    {
        if (!IsValidName(req.firstName) || !IsValidName(req.lastName)
            || !IsValidName(req.middleName, optional: true) || !IsValidName(req.suffix, 10, true))
            return "Names must use letters, spaces, apostrophes, periods or hyphens (up to 50 characters; suffix up to 10). First and last names are required.";
        if (!Regex.IsMatch(req.username?.Trim() ?? "", @"^[A-Za-z0-9][A-Za-z0-9._-]{2,29}$"))
            return "Username must be 3-30 characters, start with a letter or number, and use only letters, numbers, dots, underscores or hyphens.";
        if (!IsValidEmail(req.email))
            return "Enter a complete email address, such as name@example.com.";
        if ((req.stageName?.Trim().Length ?? 0) > 120 || (req.productionName?.Trim().Length ?? 0) > 160)
            return "Public names must be at most 120 characters (production names: 160).";
        if ((string.Equals(req.role, "Artist", StringComparison.OrdinalIgnoreCase) || string.Equals(req.role, "Sessionist", StringComparison.OrdinalIgnoreCase)) && string.IsNullOrWhiteSpace(req.stageName))
            return "Enter your public artist or group name.";
        if (!string.IsNullOrWhiteSpace(req.memberNames))
        {
            if (req.memberNames.Length > 600 || req.memberNames.Split(',').Any(name => !IsValidName(name)))
                return "Each member name must use the name format and be no more than 50 characters. Keep the full member list within 600 characters.";
        }
        return null;
    }
}
