using System;
using Xunit;
using AiAssistant.Core.Services;
using AiAssistant.Llm.Services;
using AiAssistant.Llm.Models;

namespace AiAssistant.Tests
{
    public class Phase1ContractTests
    {
        [Fact]
        public void EditIntentParser_ParsesValidJson_WithSnakeCase()
        {
            // Arrange
            var parser = new EditIntentParser();
            var json = @"
            {
                ""edits"": [
                    {
                        ""file_path"": ""src/Main.cs"",
                        ""search"": ""public void Old() {}"",
                        ""replace"": ""public void New() {}""
                    }
                ],
                ""explanation"": ""Renamed method""
            }";

            // Act
            var result = parser.Parse(json);

            // Assert
            Assert.True(result.Success);
            Assert.NotNull(result.Intent);
            Assert.Equal("Renamed method", result.Intent.Explanation);
            Assert.Single(result.Intent.Edits);
            
            var edit = result.Intent.Edits[0];
            Assert.Equal("src/Main.cs", edit.FilePath); // PascalCase property from snake_case json
            Assert.Equal("public void Old() {}", edit.Search);
            Assert.Equal("public void New() {}", edit.Replace);
        }

        [Fact]
        public void EditIntentParser_ExtractsJsonFromMarkdownFences()
        {
            // Arrange
            var parser = new EditIntentParser();
            var markdown = @"
Here is your requested edit:

```json
{
    ""edits"": [
        {
            ""file_path"": ""test.cs"",
            ""search"": ""foo"",
            ""replace"": ""bar""
        }
    ],
    ""explanation"": ""Fixed""
}
```
Hope this helps!
            ";

            // Act
            var result = parser.Parse(markdown);

            // Assert
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Single(result.Intent.Edits);
            Assert.Equal("test.cs", result.Intent.Edits[0].FilePath);
        }

        [Fact]
        public void EditIntentParser_RejectsEmptySearch()
        {
            // Arrange
            var parser = new EditIntentParser();
            var json = @"
            {
                ""edits"": [
                    {
                        ""file_path"": ""test.cs"",
                        ""search"": """",
                        ""replace"": ""bar""
                    }
                ],
                ""explanation"": ""empty search""
            }";

            // Act
            var result = parser.Parse(json);

            // Assert
            Assert.False(result.Success);
            Assert.Contains("Search string must not be empty", result.ErrorMessage);
        }

        [Fact]
        public void EditIntentParser_TrimsWhitespaceAndNormalizesPath()
        {
            // Arrange
            var parser = new EditIntentParser();
            var json = @"
            {
                ""edits"": [
                    {
                        ""file_path"": ""folder\\test.cs"",
                        ""search"": ""foo \n\t"",
                        ""replace"": ""bar \r\n""
                    }
                ],
                ""explanation"": ""test""
            }";

            // Act
            var result = parser.Parse(json);

            // Assert
            Assert.True(result.Success);
            var edit = result.Intent.Edits[0];
            Assert.Equal("folder/test.cs", edit.FilePath); // Normalized slashes
            Assert.Equal("foo", edit.Search); // Trimmed
            Assert.Equal("bar", edit.Replace); // Trimmed
        }

        [Fact]
        public void SystemPromptBuilder_BuildsCorrectly()
        {
            // Arrange
            var builder = new SystemPromptBuilder();
            var context = new PromptContext
            {
                TargetFramework = "net472",
                ActiveFilePath = "src/Main.cs",
                ActiveFileContent = "public class Main { }",
                UserRequest = "Add a constructor",
                CursorLine = 10
            };
            context.NuGetPackages.Add("Newtonsoft.Json");

            // Act
            var prompt = builder.Build(context);

            // Assert
            Assert.Contains("You are an elite AI coding assistant", prompt);
            Assert.Contains("```json", prompt); // JSON Schema
            Assert.Contains("Target Framework: net472", prompt);
            Assert.Contains("Available NuGet Packages", prompt);
            Assert.Contains("- Newtonsoft.Json", prompt);
            Assert.Contains("Cursor is currently near line 10", prompt);
            Assert.Contains("File: src/Main.cs", prompt);
            Assert.Contains("public class Main { }", prompt);
            Assert.Contains("Add a constructor", prompt);
        }
    }
}
