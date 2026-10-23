using Vlms.Domain;

namespace Vlms.Infrastructure.Safeguarding;

/// <summary>One row of the consent review comparison: the value in force and the submitted value.</summary>
public sealed record ConsentReviewField(string Label, string Current, string Submitted, bool Changed);

/// <summary>
/// Pure comparison used by the consent review page (functional.md FR-015) so a typo in the
/// name or date of birth, or any change against the consent in force, is marked "Changed".
/// Approving copies the form's name and date of birth onto the Student record, so the
/// difference must be visible before the reviewer decides.
/// </summary>
public static class ConsentReviewComparison
{
    private const string NotGiven = "Not given";

    /// <summary>Name and date of birth on the form against the Student record. A form that gives neither is not a change.</summary>
    public static IReadOnlyList<ConsentReviewField> CompareStudentDetails(ConsentRecord submission, Student? student)
    {
        var formName = string.IsNullOrWhiteSpace(submission.StudentName) ? null : submission.StudentName.Trim();
        var recordName = student?.Name ?? "";
        var nameChanged = formName is not null && !string.Equals(formName, recordName.Trim(), StringComparison.Ordinal);

        var dobChanged = submission.StudentDateOfBirth is { } dob && student is not null && dob != student.DateOfBirth;

        return
        [
            new("Name", recordName, formName ?? NotGiven, nameChanged),
            new("Date of birth", student is null ? "" : FormatDate(student.DateOfBirth),
                submission.StudentDateOfBirth is { } d ? FormatDate(d) : NotGiven, dobChanged),
        ];
    }

    /// <summary>
    /// The submission against the consent currently in force. Empty when there is none, as there
    /// is nothing to compare with.
    /// </summary>
    public static IReadOnlyList<ConsentReviewField> CompareWithInForce(
        ConsentRecord submission,
        ConsentSensitiveDetails? submissionDetails,
        ConsentRecord? inForce,
        ConsentSensitiveDetails? inForceDetails)
    {
        if (inForce is null)
        {
            return [];
        }

        static string YesNo(bool value) => value ? "Yes" : "No";
        static string Text(string? value) => string.IsNullOrWhiteSpace(value) ? "None given" : value.Trim();

        static ConsentReviewField Row(string label, string current, string submitted) =>
            new(label, current, submitted, !string.Equals(current, submitted, StringComparison.Ordinal));

        return
        [
            Row("Period", $"{FormatDate(inForce.PeriodStart)} to {FormatDate(inForce.PeriodEnd)}",
                $"{FormatDate(submission.PeriodStart)} to {FormatDate(submission.PeriodEnd)}"),
            Row("Expiry", FormatDate(inForce.ExpiryDate), FormatDate(submission.ExpiryDate)),
            Row("Photo/media consent", YesNo(inForce.PhotoMediaConsent), YesNo(submission.PhotoMediaConsent)),
            Row("Transport between sites", YesNo(inForce.TransportConsent), YesNo(submission.TransportConsent)),
            Row("Off-site trips and outings", YesNo(inForce.OffsiteTripConsent), YesNo(submission.OffsiteTripConsent)),
            Row("Data-sharing consent", YesNo(inForce.DataSharingConsent), YesNo(submission.DataSharingConsent)),
            Row("Emergency contact", Text(inForceDetails?.EmergencyContact), Text(submissionDetails?.EmergencyContact)),
            Row("Emergency medical info", Text(inForceDetails?.EmergencyMedicalInfo), Text(submissionDetails?.EmergencyMedicalInfo)),
            Row("Dietary requirements", Text(inForceDetails?.DietaryRequirements), Text(submissionDetails?.DietaryRequirements)),
            Row("Special educational needs", Text(inForceDetails?.SpecialEducationalNeeds), Text(submissionDetails?.SpecialEducationalNeeds)),
        ];
    }

    /// <summary>Latest Approved record for the student that has not expired on <paramref name="today"/>.</summary>
    public static ConsentRecord? FindInForce(IEnumerable<ConsentRecord> records, int studentId, DateOnly today) =>
        records.Where(r => r.StudentId == studentId && r.Status == ConsentStatus.Approved && r.ExpiryDate >= today)
            .OrderByDescending(r => r.DecidedAt ?? DateTime.MinValue).ThenByDescending(r => r.Id)
            .FirstOrDefault();

    /// <summary>
    /// Status text for the all-records list. An approved record is "Approved (in force)" when it is the one
    /// <see cref="FindInForce"/> picks, "Approved (replaced)" when a later approved record is in force instead,
    /// and "Approved (expired)" when it has expired with nothing else in force. Other statuses print as they are.
    /// </summary>
    public static string StatusLabel(ConsentRecord record, IEnumerable<ConsentRecord> records, DateOnly today)
    {
        if (record.Status != ConsentStatus.Approved)
        {
            return record.Status.ToString();
        }

        var inForce = FindInForce(records, record.StudentId, today);
        if (inForce is not null)
        {
            return inForce.Id == record.Id ? "Approved (in force)" : "Approved (replaced)";
        }

        return "Approved (expired)";
    }

    private static string FormatDate(DateOnly date) => date.ToString("d MMMM yyyy");
}
