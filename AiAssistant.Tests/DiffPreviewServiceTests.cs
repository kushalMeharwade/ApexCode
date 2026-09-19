using AiAssistant.Engine.Models;
using AiAssistant.Engine.Services;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace AiAssistant.Tests;

public class DiffPreviewServiceTests
{
    private readonly DiffPreviewService _service;

    public DiffPreviewServiceTests()
    {
        _service = new DiffPreviewService();
    }

    [Fact]
    public async Task GeneratePreviewAsync_EmptyStrings_ReturnsEmptyResult()
    {
        var result = await _service.GeneratePreviewAsync("test.txt", "", "");

        Assert.True(result.IsValid);
        Assert.Empty(result.Changes);
        Assert.Empty(result.Blocks);
        Assert.Equal(0, result.LinesAdded);
        Assert.Equal(0, result.LinesRemoved);
    }

    [Fact]
    public async Task GeneratePreviewAsync_ComputesLcsDiffCorrectly()
    {
        var oldContent = "Line 1\nLine 2\nLine 3";
        var newContent = "Line 1\nNew Line\nLine 3";

        var result = await _service.GeneratePreviewAsync("test.txt", oldContent, newContent);

        Assert.True(result.IsValid);
        Assert.Equal(1, result.LinesAdded);
        Assert.Equal(1, result.LinesRemoved);
        
        var diffString = string.Join("\n", result.Changes);
        Assert.Contains("- Line 2", diffString);
        Assert.Contains("+ New Line", diffString);
    }

    [Fact]
    public async Task ApplyChangesAsync_WritesToDisk()
    {
        var file = Path.GetTempFileName();
        var preview = new DiffPreviewResult
        {
            FilePath = file,
            NewContent = "Applied Text"
        };

        var success = await _service.ApplyChangesAsync(preview);

        Assert.True(success);
        Assert.Equal("Applied Text", File.ReadAllText(file));
        File.Delete(file);
    }

    [Fact]
    public async Task WaitForUserApprovalAsync_RaisesEventAndReturnsResolution()
    {
        var preview = new DiffPreviewResult();
        bool eventRaised = false;

        _service.DiffProposed += (sender, args) =>
        {
            eventRaised = true;
            Assert.Same(preview, args);
        };

        var waitTask = _service.WaitForUserApprovalAsync(preview);
        
        Assert.True(eventRaised);

        _service.ResolveApproval(true);
        var isApproved = await waitTask;

        Assert.True(isApproved);
    }
}
