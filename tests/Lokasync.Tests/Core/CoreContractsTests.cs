using System.Reflection;
using Lokasync.Core.Interfaces;
using Lokasync.Core.Models;
using Xunit;

namespace Lokasync.Tests.Core;

public sealed class CoreContractsTests
{
    [Fact]
    public void EntryStatus_EnumDefinition_MatchesLockedPrdSection4()
    {
        // Arrange
        var expectedNames = new[] { "Pending", "Translated", "FailedValidation", "ManuallyEdited" };

        // Act
        var actualNames = Enum.GetNames<EntryStatus>();

        // Assert
        Assert.Equal(expectedNames, actualNames);
    }

    [Fact]
    public void SubtitleEntry_AndRouterProfile_MatchLockedPrdSection4Signatures()
    {
        // Arrange
        var subtitleCtor = typeof(SubtitleEntry).GetConstructors().Single();
        var routerCtor = typeof(RouterProfile).GetConstructors().Single();

        // Act
        var subtitleParams = subtitleCtor.GetParameters().Select(p => (p.Name, p.ParameterType)).ToArray();
        var routerParams = routerCtor.GetParameters().Select(p => (p.Name, p.ParameterType)).ToArray();

        // Assert
        Assert.Equal(
        [
            ("Id", typeof(long)),
            ("EntryKey", typeof(string)),
            ("SourceFile", typeof(string)),
            ("AssetPath", typeof(string)),
            ("RawText", typeof(string)),
            ("MaskedText", typeof(string)),
            ("Placeholders", typeof(Dictionary<string, string>)),
            ("TranslatedText", typeof(string)),
            ("FinalPatchedText", typeof(string)),
            ("Status", typeof(EntryStatus)),
            ("HasOverflowWarning", typeof(bool))
        ], subtitleParams);

        Assert.Equal(
        [
            ("BaseUrl", typeof(string)),
            ("ApiKey", typeof(string)),
            ("Model", typeof(string)),
            ("MaxConcurrency", typeof(int)),
            ("DelayBetweenBatchesMs", typeof(int)),
            ("JitterMs", typeof(int)),
            ("BatchSize", typeof(int))
        ], routerParams);

        Assert.NotNull(typeof(IEngineAdapter).GetProperty(nameof(IEngineAdapter.EngineName)));
        Assert.NotNull(typeof(IEngineAdapter).GetMethod(nameof(IEngineAdapter.CanHandle), [typeof(string)]));
        Assert.NotNull(typeof(IEngineAdapter).GetMethod(nameof(IEngineAdapter.ExtractAsync), [typeof(string), typeof(CancellationToken)]));
        Assert.NotNull(typeof(IEngineAdapter).GetMethod(nameof(IEngineAdapter.PatchAsync), [typeof(string), typeof(IReadOnlyList<SubtitleEntry>), typeof(CancellationToken)]));

        Assert.NotNull(typeof(ITextSanitizer).GetMethod(nameof(ITextSanitizer.Mask), [typeof(string)]));
        var tryUnmask = typeof(ITextSanitizer).GetMethod(nameof(ITextSanitizer.TryUnmask));
        Assert.NotNull(tryUnmask);
        Assert.Equal(typeof(bool), tryUnmask!.ReturnType);
        Assert.True(tryUnmask.GetParameters()[2].IsOut);

        Assert.NotNull(typeof(ITranslatorProvider).GetMethod(
            nameof(ITranslatorProvider.TranslateBatchAsync),
            [typeof(Dictionary<string, string>), typeof(string), typeof(RouterProfile), typeof(CancellationToken)]));

        Assert.NotNull(typeof(IBackupService).GetMethod(
            nameof(IBackupService.CreateBackupAsync),
            [typeof(string), typeof(string), typeof(IEnumerable<string>), typeof(CancellationToken)]));
        Assert.NotNull(typeof(IBackupService).GetMethod(
            nameof(IBackupService.RestoreBackupAsync),
            [typeof(string), typeof(string), typeof(CancellationToken)]));
    }
}
