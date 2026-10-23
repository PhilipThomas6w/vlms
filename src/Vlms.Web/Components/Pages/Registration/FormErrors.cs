using System.Globalization;
using Vlms.Domain;

namespace Vlms.Web.Components.Pages.Registration;

/// <summary>
/// Every problem found on a parent registration form, keyed by the id of the field it belongs to, so the
/// page can show an error summary at the top and a message next to each field (WCAG 3.3.1, 3.3.3).
/// The service still enforces its own rules; this only reports the simple ones all at once.
/// </summary>
public sealed class FormErrors
{
    private readonly List<(string FieldId, string Message)> _errors = new();

    public IReadOnlyList<(string FieldId, string Message)> All => _errors;

    public bool Any => _errors.Count > 0;

    public void Add(string fieldId, string message) => _errors.Add((fieldId, message));

    /// <summary>The first message for the field, or null.</summary>
    public string? For(string fieldId) => _errors.FirstOrDefault(e => e.FieldId == fieldId).Message;

    /// <summary>The id of the message element to point <c>aria-describedby</c> at, or null when the field is fine.</summary>
    public string? DescribedBy(string fieldId) => For(fieldId) is null ? null : fieldId + "-error";

    /// <summary>"true" when the field has an error, otherwise null (so the attribute is left out).</summary>
    public string? Invalid(string fieldId) => For(fieldId) is null ? null : "true";

    public void RequireText(string fieldId, string? value, int maxLength, string message, string? tooLongMessage = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Add(fieldId, message);
        }
        else if (value.Trim().Length > maxLength)
        {
            Add(fieldId, tooLongMessage ?? $"Use {maxLength} characters or fewer.");
        }
    }

    public void CheckOptionalText(string fieldId, string? value, string field)
    {
        if (value is not null && value.Trim().Length > ConsentSensitiveDetails.MaxTextLength)
        {
            Add(fieldId, $"{field} must be {ConsentSensitiveDetails.MaxTextLength} characters or fewer.");
        }
    }

    public void CheckApplicant(string? phone, string? postalAddress)
    {
        RequireText("phone", phone, StudentApplication.MaxShortTextLength, "Enter your phone number.");
        RequireText("postalAddress", postalAddress, StudentApplication.MaxAddressLength, "Enter your postal address.");
    }

    /// <summary>
    /// Checks one child's fields. <paramref name="suffix"/> is the number added to each field id on the apply
    /// form ("0", "1") and empty on the single-child resubmit form; <paramref name="who"/> names the child in
    /// the message ("Child 1") when there is more than one.
    /// </summary>
    public DateOnly? CheckChild(
        string suffix, string who, string? name, string? dateOfBirth, string? relationship, string? emergencyContact,
        string? medical, string? dietary, string? sen)
    {
        var forWho = who.Length == 0 ? "" : $" for {who}";
        RequireText("childName" + suffix, name, StudentApplication.MaxShortTextLength, $"Enter the child's full name{forWho}.");
        RequireText("relationship" + suffix, relationship, StudentApplication.MaxShortTextLength,
            $"Enter your relationship to the child{(who.Length == 0 ? "" : $" ({who})")}.");
        RequireText("emergencyContact" + suffix, emergencyContact, StudentApplication.MaxAddressLength,
            $"Enter an emergency contact (name and phone number){forWho}.");
        CheckOptionalText("medical" + suffix, medical, "Medical information");
        CheckOptionalText("dietary" + suffix, dietary, "Dietary requirements");
        CheckOptionalText("sen" + suffix, sen, "Special educational needs");

        var dobId = "childDateOfBirth" + suffix;
        if (!DateOnly.TryParseExact(dateOfBirth, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            Add(dobId, $"Enter the child's date of birth{forWho}.");
            return null;
        }

        if (parsed > DateOnly.FromDateTime(DateTime.UtcNow))
        {
            Add(dobId, $"The date of birth{forWho} cannot be in the future.");
            return null;
        }

        return parsed;
    }
}
