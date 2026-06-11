using CsvHelper;
using System.Globalization;
using Report_Generator.Models;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CsvHelper.Configuration;


namespace Report_Generator.Services
{
    public class CsvParserService
    {
        public List<TripData> ReadTripCsv (Stream fileStream)
        {
            var config = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true,
                MissingFieldFound = null,   // Ignore missing fields
                HeaderValidated = null,     // Ignore header validation
                BadDataFound = null         // Ignore bad data
            };

            using (var reader = new StreamReader(fileStream))
            using (var csv = new CsvReader(reader, config))
            {
                return csv.GetRecords<TripData>().ToList();
            }
        }
    }
}
