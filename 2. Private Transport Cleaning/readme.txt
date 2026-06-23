# Private Transport Cleaning System

A high-performance, enterprise-grade geospatial telemetry pipeline built on **.NET 8.0 ASP.NET Core MVC**. This system ingests raw vehicle tracking data (ZIP, CSV, GPX), processes and filters telemetry errors, executes a custom Snap-to-Centerline algorithm, and prepares optimized datasets for further transit analysis.

> **Project Origin:** This application represents a complete architectural port and optimization of a legacy Python Flask implementation, completed over a intensive 17-day development and enhancement sprint.

---

## 🚀 Key Features & Enhancements

* **Robust Data Ingestion:** Fast streaming file uploads supporting large ZIP and CSV structures via native .NET `IFormFile` interfaces.
* **High-Performance GPX Parsing:** Migrated from Python's sequential `ElementTree` to asynchronous, heavily optimized LINQ-to-XML (`XDocument`) querying.
* **Data Integrity Validation Layer:** (New Feature) Enforces dataset validity before processing begins by cross-validating ZIP/CSV components and extracting temporal markers from filenames.
* **Precision GIS Library:** Native C# implementation of the Haversine formula and vector-based point projection for centerline snapping.
* **Optimized Data Filtering:** Embedded heuristics for speed anomaly validation, multi-point gap detection, and spatial telemetry cleanup.
* **Interactive Route Previews:** Dual-layer visualization engine utilizing `Leaflet.js` to showcase a side-by-side contrast between raw telemetry data and the newly snapped centerline path.
* **Uniform Management Workspace:** Redesigned UI aligned directly with the Public Vehicle Download layout, fully equipped with bulk actions (`Select All`, `Deselect`).

---

## 🏗️ Technical Architecture Matrix

The system architecture has been systematically modernized from the legacy framework to follow strict Separation of Concerns (SoC) principles:

| Legacy Layer (Python / Flask) | Modernized Component (.NET 8.0 MVC) | Architecture Tier & Responsibility |
| :--- | :--- | :--- |
| `Flask Routes / Blueprints` | `Controllers / ActionResults` | **Presentation Layer** — Route management and View engine delivery. |
| `xml.etree.ElementTree` | `XDocument` / `GpxReaderService` | **Data Ingestion** — Optimized XML spatial data parsing streams. |
| Custom Math / NumPy | `GISUtilityLibrary` | **Business Logic Engine** — Handles Haversine math and spatial vectors. |
| `sqlite3` Connection | `Microsoft.Data.Sqlite` | **Data Access Layer (DAL)** — Indexed lookup against `kilometer_post.db`. |
| `csv` & `zipfile` modules | `CsvHelper` & `System.IO.Compression` | **Data Export** — Accelerated mapping and asset packaging pipelines. |

---

## 🛠️ Tech Stack & Dependencies

* **Backend Framework:** .NET 8.0 (ASP.NET Core MVC)
* **Database Engine:** SQLite (via `Microsoft.Data.Sqlite`)
* **CSV Serialization:** `CsvHelper` (v33.0+)
* **Frontend Interface:** Razor Views, Bootstrap 5, Custom CSS
* **Mapping Library:** Leaflet.js

---

## 🔧 Installation & Local Setup

### Prerequisites
* [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
* A local copy of the spatial lookup database (`kilometer_post.db`) placed inside the application's target environment directory.

---

## 📂 Core Project Layout

Private Transport Cleaning/
├── Controllers/                 # Web request routing & orchestration logic
├── Models/                      # File manifests, GPS records, and viewport binding entities
├── Services/                    # Core business logic
│   ├── GpxReaderService.cs      # Native XDocument GPX XML parsing
│   ├── SnappingService.cs       # Custom vector Snap-to-Centerline geometric routing
│   └── ExportService.cs         # CsvHelper execution and ZIP compilation
├── Utilities/                   # Geographic math calculations (Haversine algorithms)
├── Views/                       # Razor UI interfaces (Upload, Map Previews, Batch Management)
├── wwwroot/                     # Static files (Leaflet.js maps, custom UI styles)
├── appsettings.json             # Global application environment configurations
└── Program.cs                   # Application bootstrapping and DI container configurations

## 📈 Optimization Metrics (Post-Migration Findings)

* **Memory Efficiency:** Transitioning from dynamic Python typing to strongly-typed C# data structures minimized the framework footprint during batch processing routines.
* **Data Integrity Guard:** The introduction of the validation layer completely eliminated cascading calculation exceptions previously caused by mismatched file names or corrupted upload payloads.