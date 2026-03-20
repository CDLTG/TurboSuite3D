using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace TurboSuite.Cuts.Services;

public static class DownloadService
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public static async Task<byte[]?> DownloadPdfAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await Client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);

            // Validate PDF magic bytes
            if (bytes.Length < 4 || bytes[0] != 0x25 || bytes[1] != 0x50 ||
                bytes[2] != 0x44 || bytes[3] != 0x46) // %PDF
            {
                return null;
            }

            return bytes;
        }
        catch
        {
            return null;
        }
    }
}
