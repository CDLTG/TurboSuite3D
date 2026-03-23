using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace TurboSuite.Cuts.Services;

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
            { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:128.0) Gecko/20100101 Firefox/128.0" }
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
            var bytes = await File.ReadAllBytesAsync(filePath);
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
