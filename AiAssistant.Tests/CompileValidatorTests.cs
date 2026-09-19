using AiAssistant.Engine.Services;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace AiAssistant.Tests;

public class CompileValidatorTests
{
    private readonly CompileValidator _validator;

    public CompileValidatorTests()
    {
        _validator = new CompileValidator();
    }

    [Fact]
    public async Task ValidateAsync_WithNoCsproj_ReturnsFalse()
    {
        var tempDir = Path.GetTempPath(); // Typically won't have a .csproj at the root
        
        var result = await _validator.ValidateAsync(tempDir);
        
        Assert.False(result);
    }

    [Fact]
    public async Task CompileAsync_WithNoCsproj_ReturnsErrorWithMessage()
    {
        var tempDir = Path.GetTempPath(); 
        
        var result = await _validator.CompileAsync(tempDir);
        
        Assert.False(result.Success);
        Assert.Contains("No .csproj found in directory.", result.Errors.First());
    }

    [Fact]
    public async Task CompileAsync_WithInvalidPath_ReturnsError()
    {
        var invalidPath = Path.Combine(Path.GetTempPath(), "does_not_exist_xyz123");
        
        var result = await _validator.CompileAsync(invalidPath);
        
        Assert.False(result.Success);
    }
}
