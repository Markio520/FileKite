using System.Text;
using FileAssistant.Core.Engines;
using FileAssistant.Infrastructure.Engines;

namespace FileAssistant.Infrastructure.Tests;

[Trait("Category", "Office")]
[Collection("Office")]
public sealed class BatchOfficeTextTests
{
    [Theory]
    [InlineData(DelimitedEncoding.Utf8)]
    [InlineData(DelimitedEncoding.Gb18030)]
    public async Task Writer_text_extraction_uses_the_selected_encoding(DelimitedEncoding selection)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "FileAssistant.slnx"))) repository = repository.Parent;
        var office = Path.Combine(repository!.FullName, "artifacts", "dependencies", "libreoffice", EnginePackageCatalog.LibreOffice.Version, "extracted", "program", "soffice.com");
        Assert.True(File.Exists(office), "Prepare the Office package with scripts/test.ps1 -Office.");
        var fixture = Directory.CreateTempSubdirectory("FileAssistant-batch-doc-text-");
        try
        {
            var source = Path.Combine(fixture.FullName, "sample.rtf");
            await File.WriteAllTextAsync(source, @"{\rtf1\ansi\deff0\uc1 \u20013?\u25991?}", Encoding.ASCII);
            var service = new LocalFileProcessingService(new("unused", Path.Combine(fixture.FullName, "cache"), OfficeExecutableOverride: office));
            var request = new ConversionRequest([source], Path.Combine(fixture.FullName, "out"), ConversionTarget.Txt, Settings: new(Encoding: selection));
            var result = await service.ConvertAsync(await service.CreatePlanAsync(request, default), null, default);
            var bytes = await File.ReadAllBytesAsync(Assert.Single(result.Outputs).FullPath);
            var expected = selection == DelimitedEncoding.Utf8 ? new UTF8Encoding(false, true) : CodePagesEncodingProvider.Instance.GetEncoding(54936)!;
            Assert.Contains("中文", expected.GetString(bytes));
            Assert.True(bytes.AsSpan().IndexOf(expected.GetBytes("中文")) >= 0);
            Assert.True(File.Exists(source));
        }
        finally { fixture.Delete(recursive: true); }
    }
}
