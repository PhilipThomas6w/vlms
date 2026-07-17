using Microsoft.AspNetCore.Authorization;

namespace Vlms.Infrastructure.Authorization;

/// <summary>
/// Resource-based requirement for acting on a specific <see cref="Vlms.Domain.Student"/> —
/// satisfied by any of: a Parent linked via <see cref="Vlms.Domain.StudentGuardianLink"/>, the
/// Student themselves, or any Teacher (unrestricted — `docs/design/low-level-design.md`
/// "Authorization model" confirms Teachers see all students). See
/// <see cref="ParentStudentAccessHandler"/>, <see cref="StudentSelfAccessHandler"/>,
/// <see cref="TeacherStudentAccessHandler"/> — ASP.NET Core authorization succeeds for a
/// requirement as soon as any one registered handler calls <c>Succeed</c>, so registering all
/// three against the same requirement expresses "any of these" directly.
/// </summary>
public sealed class StudentAccessRequirement : IAuthorizationRequirement
{
}
