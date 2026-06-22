using System.IO.Compression;
using Microsoft.AspNetCore.Http;
using Report_Generator.Models;

namespace Report_Generator.Services
{
    public class ZipExtractService
    {
        public async Task<List<InMemoryFormFile>> ExpandUploadsAsync(
            IEnumerable<IFormFile> uploads)
        {
            var result = new List<InMemoryFormFile>();

            foreach (var file in uploads)
            {
                if (file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    using var zipStream = file.OpenReadStream();
                    using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrWhiteSpace(entry.Name))
                            continue; // folder

                        await using var entryStream = entry.Open();
                        using var ms = new MemoryStream();

                        await entryStream.CopyToAsync(ms);

                        result.Add(new InMemoryFormFile(
                            entry.FullName.Replace('\\', '/'),
                            ms.ToArray()));
                    }
                }
                else
                {
                    using var ms = new MemoryStream();
                    await file.CopyToAsync(ms);

                    result.Add(new InMemoryFormFile(
                        file.FileName.Replace('\\', '/'),
                        ms.ToArray()));
                }
            }

            return result;
        }
    }
}