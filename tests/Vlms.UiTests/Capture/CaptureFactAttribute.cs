namespace Vlms.UiTests.Capture;

/// <summary>
/// A test that runs only when <c>VLMS_CAPTURE=1</c> is set, so the gate never runs it. Used for the
/// redesign's visual QA (docs/design/redesign-brief.md, phase 7), not as a pass/fail check.
/// </summary>
public sealed class CaptureFactAttribute : FactAttribute
{
    public CaptureFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("VLMS_CAPTURE") != "1")
        {
            Skip = "Design capture: set VLMS_CAPTURE=1 to run.";
        }
    }
}
