using Microsoft.Data.Sqlite;
using PrivateTransportCleaning.Models;
using System;
using System.Collections.Generic;
using System.IO;

namespace PrivateTransportCleaning.Services
{
    public class KilometerPostService
    {
        private readonly GeoUtilityService _geo;

        public KilometerPostService(GeoUtilityService geo)
        {
            _geo = geo;
        }

        public List<KilometerPost> Load(string dbPath)
        {
            var list = new List<KilometerPost>();

            if (!File.Exists(dbPath))
                return list;

            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();

            var cmd = conn.CreateCommand();
            cmd.CommandText =
                @"SELECT kilometerPost, regionId, roadName, latitude, longitude
                  FROM tblKilometerPost
                  WHERE latitude IS NOT NULL
                    AND longitude IS NOT NULL
                    AND regionId IS NOT NULL
                    AND roadName IS NOT NULL";

            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                list.Add(new KilometerPost
                {
                    KilometerPostId = reader.GetString(0),
                    RegionId = reader.GetString(1),
                    RoadName = reader.GetString(2),
                    Latitude = reader.GetDouble(3),
                    Longitude = reader.GetDouble(4)
                });
            }

            return list;
        }
    }
}