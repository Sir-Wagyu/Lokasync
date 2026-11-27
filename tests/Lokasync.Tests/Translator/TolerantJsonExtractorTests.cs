using Lokasync.Translator.Exceptions;
using Lokasync.Translator.Providers;
using Xunit;

namespace Lokasync.Tests.Translator;

public sealed class TolerantJsonExtractorTests
{
    [Fact]
    public void ExtractStringMap_PlainJsonObject_ReturnsExpectedDictionary()
    {
        // Arrange
        string raw = """{"k1":"Halo dunia","k2":"Selamat pagi [VAR_0]"}""";

        // Act
        Dictionary<string, string> result = TolerantJsonExtractor.ExtractStringMap(raw);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal("Halo dunia", result["k1"]);
        Assert.Equal("Selamat pagi [VAR_0]", result["k2"]);
    }

    [Fact]
    public void ExtractStringMap_MarkdownJsonFenceAndProse_ReturnsParsedDictionary()
    {
        // Arrange
        string raw = """
            Sure, here is the Indonesian localization:
            ```json
            {
              "line_1": "[TAG_0]Selamat datang![TAG_1]",
              "line_2": "Ayo pergi[NL_0]Sekarang!"
            }
            ```
            Let me know if you need adjustments.
            """;

        // Act
        Dictionary<string, string> result = TolerantJsonExtractor.ExtractStringMap(raw);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal("[TAG_0]Selamat datang![TAG_1]", result["line_1"]);
        Assert.Equal("Ayo pergi[NL_0]Sekarang!", result["line_2"]);
    }

    [Fact]
    public void ExtractStringMap_BareFence_ReturnsParsedDictionary()
    {
        // Arrange
        string raw = """
            ```
            {
              "k1": "Satu"
            }
            ```
            """;

        // Act
        Dictionary<string, string> result = TolerantJsonExtractor.ExtractStringMap(raw);

        // Assert
        Assert.Single(result);
        Assert.Equal("Satu", result["k1"]);
    }

    [Fact]
    public void ExtractStringMap_ProseContainingBracesBeforeAndAfterJson_ExtractsValidObject()
    {
        // Arrange
        string raw = """
            I preserved {player_name} and {0} as requested:
            {"k1": "Halo {player_name}", "k2": "Skor {0}"}
            Hope {this_note} helps!
            """;

        // Act
        Dictionary<string, string> result = TolerantJsonExtractor.ExtractStringMap(raw);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal("Halo {player_name}", result["k1"]);
        Assert.Equal("Skor {0}", result["k2"]);
    }

    [Fact]
    public void ExtractStringMap_UnclosedStrayBraceInLeadingProse_ExtractsValidObject()
    {
        // Arrange
        string raw = """
            Note: stray { in prose before JSON:
            {"k1": "Berhasil"}
            """;

        // Act
        Dictionary<string, string> result = TolerantJsonExtractor.ExtractStringMap(raw);

        // Assert
        Assert.Single(result);
        Assert.Equal("Berhasil", result["k1"]);
    }

    [Fact]
    public void ExtractStringMap_NestedBracesInsideStringValues_PreservesValues()
    {
        // Arrange
        string raw = """{"k1": "Gunakan {skill} \"segera\" [TAG_0]"}""";

        // Act
        Dictionary<string, string> result = TolerantJsonExtractor.ExtractStringMap(raw);

        // Assert
        Assert.Single(result);
        Assert.Equal("Gunakan {skill} \"segera\" [TAG_0]", result["k1"]);
    }

    [Fact]
    public void ExtractStringMap_TrailingComma_ReturnsParsedDictionary()
    {
        // Arrange
        string raw = """
            {
              "k1": "Halo",
            }
            """;

        // Act
        Dictionary<string, string> result = TolerantJsonExtractor.ExtractStringMap(raw);

        // Assert
        Assert.Single(result);
        Assert.Equal("Halo", result["k1"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("No braces at all")]
    [InlineData("{ not valid json }")]
    [InlineData("} reversed {")]
    public void ExtractStringMap_MissingBracesOrInvalidJson_ThrowsTranslatorResponseFormatException(string raw)
    {
        // Arrange & Act
        Dictionary<string, string> Act() => TolerantJsonExtractor.ExtractStringMap(raw);

        // Assert
        Assert.Throws<TranslatorResponseFormatException>(Act);
    }

    [Theory]
    [InlineData("""{"k1": 123}""")]
    [InlineData("""{"k1": null}""")]
    [InlineData("""{"k1": true}""")]
    [InlineData("""{"k1": ["a"]}""")]
    [InlineData("""{"k1": {"nested": "x"}}""")]
    public void ExtractStringMap_NonStringOrNullValue_ThrowsTranslatorResponseFormatException(string raw)
    {
        // Arrange & Act
        Dictionary<string, string> Act() => TolerantJsonExtractor.ExtractStringMap(raw);

        // Assert
        Assert.Throws<TranslatorResponseFormatException>(Act);
    }

    [Fact]
    public void ExtractStringMap_DuplicateKeys_ThrowsTranslatorResponseFormatException()
    {
        // Arrange
        string raw = """{"k1": "Satu", "k1": "Dua"}""";

        // Act
        Dictionary<string, string> Act() => TolerantJsonExtractor.ExtractStringMap(raw);

        // Assert
        Assert.Throws<TranslatorResponseFormatException>(Act);
    }
}
