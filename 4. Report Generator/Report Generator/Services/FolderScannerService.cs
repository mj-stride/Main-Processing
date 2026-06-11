using System;
using System.Collections.Generic;
using System.Linq;
using Report_Generator.Models;

namespace Report_Generator.Services
{
    public class FolderScannerService
    {
        public List<FolderInfo> IdentifyVehicleFolders (IEnumerable<string> allFilePaths)
        {
            var vehicleFolders = new List<FolderInfo>();
            var processedPaths = new HashSet<string>();

            foreach (var path in allFilePaths)
            {
                if (path.Contains("SegmentAnalysis"))
                {
                    int segmentIndex = path.IndexOf("SegmentAnalysis") + "SegmentAnalysis".Length;
                    string segmentPath = path.Substring(0, segmentIndex);

                    if (!processedPaths.Contains(segmentPath))
                    {
                        processedPaths.Add(segmentPath);

                        string[] parts = segmentPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

                        int idx = Array.IndexOf(parts, "SegmentAnalysis");

                        if (idx >= 4)
                        {
                            var folderInfo = new FolderInfo
                            {
                                VehicleType = parts[idx - 1], 
                                SurveyDate = parts[idx - 2],  
                                RoadName = parts[idx - 3],    
                                Region = parts[idx - 4],      
                                SegmentAnalysisPath = segmentPath
                            };

                            vehicleFolders.Add(folderInfo);
                        }
                        else
                        {
                            Console.WriteLine($"⚠️ Warning: Path too short to extract Region/Road/Date: {segmentPath}");
                        }
                    }
                }
            }

            return vehicleFolders;
        }
    }
}