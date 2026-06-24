using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Microsoft.AspNetCore.Http;

namespace PrivateTransportCleaning.Services
{
    public class ZipProcessingService
    {
        public List<string> ExtractZips(List<IFormFile> zipFiles, string extractFolder)
        {
            var extractedPaths = new List<string>();

            Directory.CreateDirectory(extractFolder);

            foreach (var file in zipFiles)
            {
                var zipPath = Path.Combine(extractFolder, Guid.NewGuid() + "_" + file.FileName);

                using (var stream = new FileStream(zipPath, FileMode.Create))
                {
                    file.CopyTo(stream);
                }

                var tempExtract = Path.Combine(extractFolder, Guid.NewGuid().ToString());
                Directory.CreateDirectory(tempExtract);

                ZipFile.ExtractToDirectory(zipPath, tempExtract);

                foreach (var gpx in Directory.GetFiles(tempExtract, "*.gpx", SearchOption.AllDirectories))
                {
                    extractedPaths.Add(gpx);
                }
            }

            return extractedPaths;
        }
    }
}