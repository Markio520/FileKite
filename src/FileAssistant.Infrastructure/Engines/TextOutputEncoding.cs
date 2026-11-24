using System.Text;
using FileAssistant.Core.Engines;

namespace FileAssistant.Infrastructure.Engines;

internal static class TextOutputEncoding
{
    public static Encoding Get(DelimitedEncoding encoding) => encoding switch
    {
        DelimitedEncoding.Utf8 => new UTF8Encoding(false),
        DelimitedEncoding.Gb18030 => CodePagesEncodingProvider.Instance.GetEncoding(54936)!,
        _ => throw new FileProcessingException("文本输出编码不支持。")
    };
}
