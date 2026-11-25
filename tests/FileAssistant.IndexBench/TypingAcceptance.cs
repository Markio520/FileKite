using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileAssistant.Core.Features.Search;
using FileAssistant.Core.Settings;
using FileAssistant.Indexing;
using FileAssistant.Infrastructure.Windows;

namespace FileAssistant.IndexBench;

internal static class TypingAcceptance
{
    public static async Task RunAsync(string[] args)
    {
        bool reference = args[0] == "typing-everything";
        if (args.Length < (reference ? 2 : 3)) throw new ArgumentException("typing[-common] <cache> <volumes...>, or typing-everything <test instance>");
        using var limit = new BenchmarkMemoryLimit(256);
        var settings = new AppSettings();
        var client = reference ? new EverythingSearchClient(args[1]) : null;
        IndexStore[] stores = reference ? [] : args.Skip(2).Select(new IndexCatalog(args[1]).ForRoot).ToArray();
        bool preferences = args[0] == "typing";
        string[] terms = ["n", "no", "node", "report", "报告", "ext:txt", "ext:dll", "filekite-no-such-name-7e92af21"];
        for (var pass = 0; pass < 4; pass++)
        {
            foreach (var text in terms)
            {
                var request = new SearchRequest
                {
                    Text = text, PageSize = 200, Sort = SearchSort.Name,
                    PinyinSearch = preferences && settings.PinyinSearch,
                    ShowHidden = !preferences || settings.ShowHidden,
                    ExcludedDirectories = preferences ? settings.ExcludedDirectories : [],
                    IncludeDirectoryUsage = preferences
                };
                var sw = Stopwatch.StartNew();
                var page = client is null ? IndexedFileSearch.Query(stores, request, default) :
                    await client.SearchAsync(request, default);
                var ms = sw.Elapsed.TotalMilliseconds;
                if (page.IsPartialIndex || page.Items.Count != Math.Min(page.Total, request.PageSize))
                    throw new InvalidDataException("A complete, ordered page is required.");
                var signature = string.Join('\n', page.Items.Select(row => row.FullPath));
                Console.WriteLine(JsonSerializer.Serialize(new { pass, text, ms, page.Total, rows = page.Items.Count,
                    signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature))),
                    peakPrivateMiB = limit.PeakBytes() / 1048576.0 }));
            }
        }
    }
}
