using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using BlazorLore.Format.Core;
using Xunit;

namespace BlazorLore.Format.Core.Tests;

public class IntegratedFormatterTests
{
    private readonly IBlazorFormatter _formatter;
    private readonly string _testFilesPath;
    private readonly string _inputPath;
    private readonly string _expectedOutputPath;
    private readonly string _actualOutputPath;

    public IntegratedFormatterTests()
    {
        _formatter = new BlazorFormatter();
        
        // Get the path to the test files directory
        // Use compile-time constant to get the source directory
        var sourceDirectory = GetSourceDirectory();
        _testFilesPath = Path.Combine(sourceDirectory, "TestFiles");
        _inputPath = Path.Combine(_testFilesPath, "Input");
        _expectedOutputPath = Path.Combine(_testFilesPath, "ExpectedOutput");
        _actualOutputPath = Path.Combine(_testFilesPath, "ActualOutput");
        
        // Ensure the actual output directory exists
        Directory.CreateDirectory(_actualOutputPath);
    }

    [Theory]
    [InlineData("RazorDirectives.razor")]
    [InlineData("HtmlWithDoctype.html")]
    [InlineData("ComplexComponent.razor")]
    [InlineData("MixedContent.razor")]
    [InlineData("NestedQuotes.razor")]
    [InlineData("ForLoopTest.razor")]
    [InlineData("BracketSyntax.razor")]
    [InlineData("ParenthesizedExpressions.razor")]
    public void Format_ShouldMatchExpectedOutput(string fileName)
    {
        // Arrange
        var inputFile = Path.Combine(_inputPath, fileName);
        var expectedOutputFile = Path.Combine(_expectedOutputPath, fileName);
        var actualOutputFile = Path.Combine(_actualOutputPath, fileName);
        
        var input = File.ReadAllText(inputFile);
        var expectedOutput = File.ReadAllText(expectedOutputFile);
        var options = new BlazorFormatterOptions();

        // Act
        var actualOutput = _formatter.Format(input, options);
        
        // Save the actual output for manual inspection
        File.WriteAllText(actualOutputFile, actualOutput);

        // Assert
        Assert.Equal(NormalizeLineEndings(expectedOutput), NormalizeLineEndings(actualOutput));
    }

    [Fact]
    public void Format_AllTestFiles_ShouldProduceValidOutput()
    {
        // This test ensures all input files can be formatted without throwing exceptions
        var inputFiles = Directory.GetFiles(_inputPath, "*.*", SearchOption.AllDirectories);
        var options = new BlazorFormatterOptions();

        foreach (var inputFile in inputFiles)
        {
            var input = File.ReadAllText(inputFile);
            var fileName = Path.GetFileName(inputFile);
            var actualOutputFile = Path.Combine(_actualOutputPath, fileName);
            
            // Should not throw
            var output = _formatter.Format(input, options);
            
            // Save the actual output
            File.WriteAllText(actualOutputFile, output);
            
            // Output should not be empty
            Assert.NotEmpty(output);
        }
    }

    [Fact]
    public void Format_ShouldPreserveRazorDirectives()
    {
        // Arrange
        var input = @"@page ""/test""
@rendermode InteractiveServer
@attribute [StreamRendering]
<h1>Test</h1>";
        var options = new BlazorFormatterOptions();

        // Act
        var output = _formatter.Format(input, options);

        // Assert
        var lines = output.Split('\n').Select(l => l.TrimEnd()).ToArray();
        Assert.Contains("@page \"/test\"", lines);
        Assert.Contains("@rendermode InteractiveServer", lines);
        Assert.Contains("@attribute [StreamRendering]", lines);
    }

    [Fact]
    public void Format_ShouldPreserveDoctypeDeclaration()
    {
        // Arrange
        var input = "<!DOCTYPE html>\n<html>\n<body>Test</body>\n</html>";
        var options = new BlazorFormatterOptions();

        // Act
        var output = _formatter.Format(input, options);

        // Assert
        Assert.StartsWith("<!DOCTYPE html>", output);
        Assert.DoesNotContain("< !DOCTYPE", output);
        Assert.DoesNotContain("<\n!DOCTYPE", output);
    }

    private static string NormalizeLineEndings(string text)
    {
        // Normalize line endings to handle cross-platform differences
        return text.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd();
    }
    
    private static string GetSourceDirectory([CallerFilePath] string sourceFilePath = "")
    {
        // This will give us the directory where this source file is located
        return Path.GetDirectoryName(sourceFilePath)!;
    }
}