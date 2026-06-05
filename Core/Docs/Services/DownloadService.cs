using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace TurboSuite.Docs.Services;

public static class DownloadService
{
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = true
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders =
        {
            { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36" }
        }
    };

    public static async Task<byte[]?> DownloadPdfAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await Client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            return IsValidPdf(bytes) ? bytes : null;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<byte[]?> ReadLocalPdfAsync(string filePath)
    {
        try
        {
            // File.ReadAllBytesAsync is .NET 5+/Core only; on the net48 Revit2024 shim
            // fall back to a sync read offloaded to the thread pool (equivalent for a
            // local cut-sheet PDF). net8 keeps the true-async path unchanged.
#if NET5_0_OR_GREATER
            var bytes = await File.ReadAllBytesAsync(filePath);
#else
            var bytes = await Task.Run(() => File.ReadAllBytes(filePath));
#endif
            return IsValidPdf(bytes) ? bytes : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsValidPdf(byte[] bytes)
    {
        return bytes.Length >= 4
               && bytes[0] == 0x25 && bytes[1] == 0x50
               && bytes[2] == 0x44 && bytes[3] == 0x46; // %PDF
    }
}
