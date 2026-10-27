using System.IO;
using Xunit;

namespace Vlms.Tests.Web;

/// <summary>
/// Regression coverage for the double-submit fix on Progress/RecordCompletions.razor: a second click on
/// "Record completions" while the first batch is still running must return before it reaches
/// <c>CompletionService.MarkCompleteBatchAsync</c>, so only one batch runs on the DbContext.
///
/// Same markup-content-assertion convention as PerChildAccessibleNamesTests.cs: reads the Razor file
/// directly off disk. The page's handler needs a live circuit and a seeded database to run, so the
/// ordering of the guard, the busy render and the service call is asserted in the source.
/// </summary>
public class RecordCompletionsSubmitTests
{
    [Fact]
    public void SubmitAsync_ReturnsEarlyWhenABatchIsAlreadyRunning()
    {
        var handler = SubmitHandler();

        var guard = handler.IndexOf("if (_busy || _lessonId == 0 || _selected.Count == 0)", StringComparison.Ordinal);
        Assert.True(guard >= 0, "SubmitAsync must refuse to start while _busy is set");
        Assert.True(guard < handler.IndexOf("_busy = true;", StringComparison.Ordinal), "The busy guard must run before _busy is set");
    }

    [Fact]
    public void SubmitAsync_RendersBusyStateBeforeTheBatchStarts()
    {
        var handler = SubmitHandler();

        var busySet = handler.IndexOf("_busy = true;", StringComparison.Ordinal);
        var render = handler.IndexOf("StateHasChanged();", StringComparison.Ordinal);
        var yield = handler.IndexOf("await Task.Yield();", StringComparison.Ordinal);
        var batch = handler.IndexOf("CompletionService.MarkCompleteBatchAsync", StringComparison.Ordinal);

        Assert.True(busySet >= 0 && render > busySet, "_busy must be set, then the component re-rendered");
        Assert.True(yield > render, "The handler must yield after rendering so the disabled state reaches the browser");
        Assert.True(batch > yield, "The batch must start only after the busy state has been rendered");
    }

    [Fact]
    public void SubmitAsync_ResetsBusyInFinally()
    {
        var handler = SubmitHandler();

        var finallyBlock = handler.IndexOf("finally", StringComparison.Ordinal);
        Assert.True(finallyBlock >= 0, "SubmitAsync must reset _busy in a finally block");
        Assert.Contains("_busy = false;", handler.Substring(finallyBlock), StringComparison.Ordinal);
    }

    /// <summary>
    /// The text of SubmitAsync, from its signature to the end of the @code block.
    /// </summary>
    private static string SubmitHandler()
    {
        var content = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Vlms.Web", "Components", "Pages", "Progress", "RecordCompletions.razor"));
        var start = content.IndexOf("private async Task SubmitAsync()", StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected SubmitAsync in RecordCompletions.razor");
        return content.Substring(start);
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
