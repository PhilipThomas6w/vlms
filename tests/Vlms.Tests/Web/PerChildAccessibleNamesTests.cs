using System.IO;
using Xunit;

namespace Vlms.Tests.Web;

/// <summary>
/// Regression coverage for the WCAG 2.4.6 "Headings and labels" and 2.5.3 "Label in name" fixes
/// recorded in STATE.md's "Distinct button and field names" browser-walk item:
/// - repeated per-row buttons carry an accessible name that includes the child, student, teacher or
///   submission they act on, while the aria-label still starts with the visible text;
/// - the parent's child sign-in error is shown beside that child's form, and the inputs of that form
///   reference it with aria-describedby, instead of appearing once at the top of the page;
/// - page introductions no longer show internal entity names, requirement IDs or file names.
///
/// Same lightweight markup-content-assertion convention as ErrorIdentificationAccessibilityTests.cs
/// and StatusMessageAccessibilityTests.cs: reads Razor files directly off disk.
/// </summary>
public class PerChildAccessibleNamesTests
{
    [Fact]
    public void ParentConsent_CompleteAndUpdateButtons_NameTheChild()
    {
        var content = ReadPage("Parent", "ParentConsent.razor");

        Assert.Contains("aria-label=\"@(child.LatestSubmission is null ? $\"Complete the consent form for {child.StudentName}\" : $\"Update and resubmit the consent form for {child.StudentName}\")\"", content);
        Assert.Contains(">@(child.LatestSubmission is null ? \"Complete the consent form\" : \"Update and resubmit\")</button>", content);
    }

    [Fact]
    public void ParentStudentSignIns_ErrorsSitBesideEachChildsForm()
    {
        var content = ReadPage("Parent", "StudentSignIns.razor");

        // The per-child error is rendered inside the loop over children, after that child's form.
        var loopStart = content.IndexOf("@foreach (var student in _students)", StringComparison.Ordinal);
        var errorPlacement = content.IndexOf("@if (_childErrors.TryGetValue(student.StudentId, out var childError))", StringComparison.Ordinal);
        Assert.True(loopStart >= 0, "Expected the per-child loop in StudentSignIns.razor");
        Assert.True(errorPlacement > loopStart, "The child error must be rendered inside the per-child loop");
        Assert.Contains("<p class=\"error\" role=\"alert\" id=\"@ErrorIdFor(student.StudentId)\">@childError</p>", content);

        // Every input of the per-child forms (username, password, confirm; both branches) points at it.
        Assert.Equal(5, CountOccurrences(content, "aria-describedby=\"@ErrorIdFor(student.StudentId)\""));

        // Each input still has a label whose for matches its id (WCAG static check).
        Assert.Contains("<label for=\"username@(student.StudentId)\">", content);
        Assert.Contains("<input type=\"text\" id=\"username@(student.StudentId)\"", content);
    }

    [Fact]
    public void ParentStudentSignIns_CreateAndResetButtons_NameTheChild()
    {
        var content = ReadPage("Parent", "StudentSignIns.razor");

        Assert.Contains("aria-label=\"@($\"Create sign-in for {student.StudentName}\")\"", content);
        Assert.Contains("aria-label=\"@($\"Reset password for {student.StudentName}\")\"", content);
    }

    [Fact]
    public void ConsentRecords_ReviewButton_NamesTheStudent()
    {
        var content = ReadPage("Safeguarding", "ConsentRecords.razor");

        Assert.Contains("aria-label=\"@($\"Review submission for {_studentNames.GetValueOrDefault(record.StudentId)}\")\" @onclick=\"() => StartReview(record.Id)\">Review</button>", content);
    }

    [Fact]
    public void DbsChecks_MarkButtons_NameTheTeacher()
    {
        var content = ReadPage("Safeguarding", "DbsChecks.razor");

        Assert.Contains("aria-label=\"@($\"Mark {status} for {_teacherNames.GetValueOrDefault(check.TeacherUserId)}\")\"", content);
    }

    [Fact]
    public void MarkAsLeft_ButtonsInBothTables_NameThePersonTheyAct_On()
    {
        var content = ReadPage("Retention", "MarkAsLeft.razor");

        Assert.Contains("aria-label=\"@($\"Mark {student.Name} as left\")\"", content);
        Assert.Contains("aria-label=\"@($\"Mark {member.User.DisplayName} as left\")\"", content);
    }

    [Theory]
    [InlineData("Parent", "ParentDashboard.razor")]
    [InlineData("Parent", "ParentConsent.razor")]
    [InlineData("Guardianship", "GuardianLinks.razor")]
    [InlineData("Registration", "RegisterStudent.razor")]
    [InlineData("Administration", "Students.razor")]
    [InlineData("Administration", "StudentSignIns.razor")]
    [InlineData("Administration", "UserRoles.razor")]
    [InlineData("Administration", "RankBadges.razor")]
    [InlineData("Safeguarding", "ConsentRecords.razor")]
    [InlineData("Safeguarding", "DbsChecks.razor")]
    [InlineData("Reporting", "ProgressReporting.razor")]
    [InlineData("Reporting", "RecordsDueForDeletion.razor")]
    [InlineData("Retention", "MarkAsLeft.razor")]
    [InlineData("Progress", "CompletionReversals.razor")]
    [InlineData("Progress", "RecordCompletions.razor")]
    [InlineData("Attendance", "TakeRegister.razor")]
    public void PageText_DoesNotShowInternalReferences(string folder, string fileName)
    {
        // Only rendered markup matters here; C# line comments may keep the references.
        var content = string.Join(
            "\n",
            ReadPage(folder, fileName).Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        Assert.DoesNotContain("functional.md", content);
        Assert.DoesNotContain("STATE.md", content);
        Assert.DoesNotContain("FR-0", content);
        Assert.DoesNotContain("<code>StudentGuardianLink</code>", content);
        Assert.DoesNotContain("<code>ConsentRecord</code>", content);
        Assert.DoesNotContain("<code>DbsCheck</code>", content);
        Assert.DoesNotContain("<code>StudentRankProgress</code>", content);
        Assert.DoesNotContain("<code>ConsentExpiryJob</code>", content);
    }

    private static int CountOccurrences(string content, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = content.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string ReadPage(string folder, string fileName)
    {
        var path = Path.Combine(RepoRoot(), "src", "Vlms.Web", "Components", "Pages", folder, fileName);
        Assert.True(File.Exists(path), $"Expected a page at {path}");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// Walks up from the test assembly's output directory to find the repository root
    /// (identified by Vlms.slnx), as the other Web/ markup-assertion suites do.
    /// </summary>
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Vlms.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "Could not locate repository root (Vlms.slnx) from test output directory");
        return directory!.FullName;
    }
}
