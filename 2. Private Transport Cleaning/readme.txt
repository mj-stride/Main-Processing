# Private Transport Cleaning System

A high performance enterprise grade geospatial telemetry pipeline built on .NET 8.0 ASP.NET Core MVC. This system ingests raw vehicle tracking data (ZIP, CSV, GPX), processes and filters telemetry errors, executes a Snap-to-Centerline algorithm, and prepares optimized datasets for transit analysis.

Project Origin:
This application is a complete architectural port and optimization of a legacy Python Flask implementation completed over a 17 day development sprint.

---

## Key Features and Enhancements

* Robust Data Ingestion: Fast streaming file uploads supporting large ZIP and CSV files using IFormFile.
* High Performance GPX Parsing: Migrated from ElementTree to LINQ to XML using XDocument.
* Data Integrity Validation Layer: Validates datasets before processing and checks file consistency.
* GIS Library: C# implementation of Haversine formula and vector based projection for centerline snapping.
* Data Filtering: Removes invalid speed readings, duplicates, and spatial anomalies.
* Interactive Route Preview: Uses Leaflet.js to display raw and processed routes.
* Management Workspace: UI for managing and selecting processed trips.

---

## Technical Architecture

The system follows Separation of Concerns principles.

| Legacy Layer | Modern Component | Responsibility |
|--------------|------------------|----------------|
| Flask Routes | Controllers | Request handling |
| ElementTree | XDocument Service | Data parsing |
| NumPy Math | GIS Utility | Geospatial calculations |
| SQLite3 | Microsoft.Data.Sqlite | Data storage |
| csv module | CsvHelper | Data export |

---

## Tech Stack

* Backend: .NET 8.0 ASP.NET Core MVC
* Database: SQLite
* CSV Processing: CsvHelper
* Frontend: Razor Views, Bootstrap, CSS
* Mapping: Leaflet.js

---

## Installation

Prerequisites:
* .NET 8 SDK
* kilometer_post.db file placed in project directory

---

## Project Structure

```text
Private Transport Cleaning/
├── Controllers/
├── Models/
├── Services/
│   ├── GpxReaderService.cs
│   ├── SnappingService.cs
│   └── ExportService.cs
├── Utilities/
├── Views/
├── wwwroot/
├── appsettings.json
└── Program.cs