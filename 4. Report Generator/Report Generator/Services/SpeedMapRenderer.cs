using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using SkiaSharp;
using Report_Generator.Models;

namespace Report_Generator.Services
{
    public class SpeedMapRenderer
    {
        private static readonly HttpClient _http = new HttpClient();
        private const double EarthRadius = 6378137.0; // Web Mercator sphere radius, meters
        private const int TileSize = 256;

        private const int TargetMapSizePx = 1600;
        private const int TitleBarHeightPx = 70;
        private const int MinZoom = 3;
        private const int MaxZoom = 18;

        private static readonly (string Label, string Color)[] LegendItems =
        {
            ("0–5 kph", "red"), ("6–15 kph", "orange"), ("16–30 kph", "yellow"),
            ("31–45 kph", "green"), ("46+ kph", "blue")
        };

        // Matplotlib's named-color RGB values
        private static readonly Dictionary<string, SKColor> ColorMap = new()
        {
            ["red"] = new SKColor(0xFF, 0x00, 0x00),
            ["orange"] = new SKColor(0xFF, 0xA5, 0x00),
            ["yellow"] = new SKColor(0xFF, 0xFF, 0x00),
            ["green"] = new SKColor(0x00, 0x80, 0x00),
            ["blue"] = new SKColor(0x00, 0x00, 0xFF),
            ["gray"] = new SKColor(0x80, 0x80, 0x80),
        };

        public SpeedMapRenderer()
        {
            if (!_http.DefaultRequestHeaders.UserAgent.Any())
                _http.DefaultRequestHeaders.Add("User-Agent", "TrafficReportGenerator/1.0");
        }

        public async Task<byte[]?> ExportTripSpeedPngAsync(List<TripCp2CpSegment> segments, List<ControlPoint> controlPoints, string title)
        {
            if (segments == null || !segments.Any())
            {
                Console.WriteLine("    ⚠️ No segments to render — skipping map.");
                return null;
            }

            // 1. Bounds in EPSG:3857 meters, padded 1.15x and forced square — mirrors
            //    Python's total_bounds + cx/cy/half*1.15 + ax.set_xlim/set_ylim block.
            var allMeters = new List<(double X, double Y)>();
            foreach (var seg in segments)
                foreach (var c in seg.Geometry.Coordinates)
                    allMeters.Add(LonLatToMeters(c.X, c.Y));
            foreach (var cp in controlPoints)
                allMeters.Add(LonLatToMeters(cp.Longitude, cp.Latitude));

            double minX = allMeters.Min(p => p.X), maxX = allMeters.Max(p => p.X);
            double minY = allMeters.Min(p => p.Y), maxY = allMeters.Max(p => p.Y);
            double cx = (minX + maxX) / 2.0, cy = (minY + maxY) / 2.0;
            double half = Math.Max(maxX - minX, maxY - minY) / 2.0 * 1.15;
            if (half < 50) half = 50; // guard against a degenerate single-point bbox

            // 2. Pick a zoom whose resolution roughly fills TargetMapSizePx — mirrors what
            //    contextily infers automatically from the requested extent.
            int requestedZoom = PickZoom(half * 2);

            // 3. Fetch + composite the OSM tile mosaic covering the padded bbox, then crop
            //    tightly to the exact pixel bbox (contextily does this crop for you; here
            //    it's explicit so the output isn't tile-grid-aligned). The mosaic fetch may
            //    clamp to a coarser zoom than requested if the bbox is unexpectedly large —
            //    `zoom` below is always the zoom actually used, never the original estimate.
            var (mosaic, originPxX, originPxY, zoom) = await FetchTileMosaicAsync(cx - half, cx + half, cy - half, cy + half, requestedZoom);
            if (mosaic == null)
            {
                Console.WriteLine("    ⚠️ Basemap tiles not added (mosaic fetch failed).");
                return null;
            }

            double pxMinX = MetersXToPixel(cx - half, zoom);
            double pxMaxX = MetersXToPixel(cx + half, zoom);
            double pxMinY = MetersYToPixel(cy + half, zoom); // max Y meters -> min pixel Y
            double pxMaxY = MetersYToPixel(cy - half, zoom);

            int cropX = (int)Math.Round(pxMinX - originPxX);
            int cropY = (int)Math.Round(pxMinY - originPxY);
            int cropW = Math.Max(1, (int)Math.Round(pxMaxX - pxMinX));
            int cropH = Math.Max(1, (int)Math.Round(pxMaxY - pxMinY));

            using var mapBitmap = new SKBitmap(cropW, cropH);
            using (var cropCanvas = new SKCanvas(mapBitmap))
            {
                cropCanvas.DrawBitmap(mosaic, new SKRect(cropX, cropY, cropX + cropW, cropY + cropH), new SKRect(0, 0, cropW, cropH));
            }
            mosaic.Dispose();

            // 4. Compose final canvas: title bar + map.
            int canvasW = cropW;
            int canvasH = cropH + TitleBarHeightPx;
            using var bitmap = new SKBitmap(canvasW, canvasH);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(mapBitmap, 0, TitleBarHeightPx);

            // Local projector: WGS84 lon/lat -> final canvas pixel coords.
            SKPoint Proj(double lon, double lat)
            {
                var (mx, my) = LonLatToMeters(lon, lat);
                double px = MetersXToPixel(mx, zoom) - pxMinX;
                double py = MetersYToPixel(my, zoom) - pxMinY + TitleBarHeightPx;
                return new SKPoint((float)px, (float)py);
            }

            DrawSegments(canvas, segments, Proj);
            DrawControlPoints(canvas, controlPoints, Proj);
            DrawTitle(canvas, title, canvasW);
            DrawLegend(canvas, canvasW, TitleBarHeightPx);
            DrawAttribution(canvas, canvasH);

            canvas.Flush();
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }

        // ---------- Web Mercator helpers ----------

        private static (double X, double Y) LonLatToMeters(double lon, double lat)
        {
            double x = lon * Math.PI / 180.0 * EarthRadius;
            double latRad = lat * Math.PI / 180.0;
            double y = Math.Log(Math.Tan(Math.PI / 4.0 + latRad / 2.0)) * EarthRadius;
            return (x, y);
        }

        private static double MetersXToPixel(double xMeters, int zoom)
        {
            double mapSize = TileSize * Math.Pow(2, zoom);
            return (xMeters + Math.PI * EarthRadius) / (2 * Math.PI * EarthRadius) * mapSize;
        }

        private static double MetersYToPixel(double yMeters, int zoom)
        {
            double mapSize = TileSize * Math.Pow(2, zoom);
            return (Math.PI * EarthRadius - yMeters) / (2 * Math.PI * EarthRadius) * mapSize;
        }

        private const int MaxTilesPerAxis = 16;

        private static int PickZoom(double bboxSizeMeters)
        {
            double desiredMpp = bboxSizeMeters / TargetMapSizePx;
            int zoom = MinZoom;
            for (int z = MinZoom; z <= MaxZoom; z++)
            {
                double mpp = (2 * Math.PI * EarthRadius) / (TileSize * Math.Pow(2, z));
                if (mpp < desiredMpp) break;
                zoom = z;
            }
            return zoom;
        }

        private async Task<(SKBitmap? Bitmap, double OriginPxX, double OriginPxY, int Zoom)> FetchTileMosaicAsync(
            double minXm, double maxXm, double minYm, double maxYm, int zoom)
        {
            double pxMinX = MetersXToPixel(minXm, zoom);
            double pxMaxX = MetersXToPixel(maxXm, zoom);
            double pxMinY = MetersYToPixel(maxYm, zoom);
            double pxMaxY = MetersYToPixel(minYm, zoom);

            int tileXMin = (int)Math.Floor(pxMinX / TileSize);
            int tileXMax = (int)Math.Floor((pxMaxX - 1) / TileSize);
            int tileYMin = (int)Math.Floor(pxMinY / TileSize);
            int tileYMax = (int)Math.Floor((pxMaxY - 1) / TileSize);

            int tilesX = Math.Max(1, tileXMax - tileXMin + 1);
            int tilesY = Math.Max(1, tileYMax - tileYMin + 1);

            if (tilesX > MaxTilesPerAxis || tilesY > MaxTilesPerAxis)
            {
                Console.WriteLine($"    ⚠️ Tile mosaic would be {tilesX}x{tilesY} at zoom {zoom} — clamping to a coarser zoom.");
                while (zoom > MinZoom && (tilesX > MaxTilesPerAxis || tilesY > MaxTilesPerAxis))
                {
                    zoom--;
                    pxMinX = MetersXToPixel(minXm, zoom);
                    pxMaxX = MetersXToPixel(maxXm, zoom);
                    pxMinY = MetersYToPixel(maxYm, zoom);
                    pxMaxY = MetersYToPixel(minYm, zoom);

                    tileXMin = (int)Math.Floor(pxMinX / TileSize);
                    tileXMax = (int)Math.Floor((pxMaxX - 1) / TileSize);
                    tileYMin = (int)Math.Floor(pxMinY / TileSize);
                    tileYMax = (int)Math.Floor((pxMaxY - 1) / TileSize);

                    tilesX = Math.Max(1, tileXMax - tileXMin + 1);
                    tilesY = Math.Max(1, tileYMax - tileYMin + 1);
                }
                Console.WriteLine($"    -> Using zoom {zoom}, mosaic {tilesX}x{tilesY} tiles.");
            }

            var bitmap = new SKBitmap(tilesX * TileSize, tilesY * TileSize);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);
            var canvasLock = new object();

            var fetches = new List<Task>();
            for (int tx = 0; tx < tilesX; tx++)
            {
                for (int ty = 0; ty < tilesY; ty++)
                {
                    int tileX = tileXMin + tx;
                    int tileY = tileYMin + ty;
                    int destX = tx * TileSize, destY = ty * TileSize;
                    fetches.Add(FetchTileAsync(tileX, tileY, zoom, canvas, destX, destY, canvasLock));
                }
            }
            await Task.WhenAll(fetches);

            return (bitmap, tileXMin * TileSize, tileYMin * TileSize, zoom);
        }

        private async Task FetchTileAsync(int tileX, int tileY, int zoom, SKCanvas canvas, int destX, int destY, object canvasLock)
        {
            string url = $"https://tile.openstreetmap.org/{zoom}/{tileX}/{tileY}.png";
            try
            {
                var bytes = await _http.GetByteArrayAsync(url);
                using var tile = SKBitmap.Decode(bytes);
                if (tile != null)
                {
                    lock (canvasLock)
                    {
                        canvas.DrawBitmap(tile, destX, destY);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    ⚠️ Tile fetch failed: {url} — {ex.Message}");
            }
        }

        // ---------- Drawing (replaces ax.plot / ax.scatter / ax.text / ax.legend) ----------

        private void DrawSegments(SKCanvas canvas, List<TripCp2CpSegment> segments, Func<double, double, SKPoint> proj)
        {
            using var paint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                IsAntialias = true,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round,
            };

            foreach (var seg in segments)
            {
                paint.Color = ColorMap.TryGetValue(seg.ColorName, out var c) ? c : ColorMap["gray"];
                paint.StrokeWidth = seg.LineWidth * 1.6f;

                var coords = seg.Geometry.Coordinates;
                if (coords.Length == 0) continue;

                using var path = new SKPath();
                path.MoveTo(proj(coords[0].X, coords[0].Y));
                for (int i = 1; i < coords.Length; i++)
                    path.LineTo(proj(coords[i].X, coords[i].Y));

                canvas.DrawPath(path, paint);
            }
        }

        private void DrawControlPoints(SKCanvas canvas, List<ControlPoint> cps, Func<double, double, SKPoint> proj)
        {
            using var dotPaint = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Black, IsAntialias = true };
            using var haloPaint = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.White, StrokeWidth = 3, IsAntialias = true };
            using var textPaint = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Black, IsAntialias = true };
            using var font = new SKFont { Size = 24 };

            foreach (var cp in cps)
            {
                var pt = proj(cp.Longitude, cp.Latitude);
                canvas.DrawCircle(pt, 5, dotPaint);

                float tx = pt.X + 15, ty = pt.Y + 8;
                canvas.DrawText(cp.Name, tx, ty, SKTextAlign.Left, font, haloPaint);
                canvas.DrawText(cp.Name, tx, ty, SKTextAlign.Left, font, textPaint);
            }
        }

        private void DrawTitle(SKCanvas canvas, string title, int canvasW)
        {
            using var font = new SKFont { Size = 28 };
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            float width = font.MeasureText(title, paint);
            canvas.DrawText(title, (canvasW - width) / 2f, TitleBarHeightPx / 2f + 10, SKTextAlign.Left, font, paint);
        }

        private void DrawLegend(SKCanvas canvas, int canvasW, int mapTop)
        {
            int boxW = 220, boxH = 40 + LegendItems.Length * 32 + 10;
            int x = canvasW - boxW - 20;
            int y = mapTop + 20;

            using var bgPaint = new SKPaint { Color = new SKColor(255, 255, 255, 230), Style = SKPaintStyle.Fill };
            using var borderPaint = new SKPaint { Color = new SKColor(120, 120, 120), Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
            canvas.DrawRect(x, y, boxW, boxH, bgPaint);
            canvas.DrawRect(x, y, boxW, boxH, borderPaint);

            using var titleFont = new SKFont { Size = 26 };
            using var labelFont = new SKFont { Size = 26 };
            using var textPaint = new SKPaint { Color = SKColors.Black, IsAntialias = true };

            canvas.DrawText("CP-to-CP Speed", x + 15, y + 28, SKTextAlign.Left, titleFont, textPaint);

            for (int i = 0; i < LegendItems.Length; i++)
            {
                var (label, colorName) = LegendItems[i];
                int rowY = y + 42 + i * 34;
                using var swatchPaint = new SKPaint { Color = ColorMap[colorName], Style = SKPaintStyle.Fill };
                canvas.DrawRect(x, rowY, 60, 18, swatchPaint);
                canvas.DrawText(label, x + 80, rowY + 18, SKTextAlign.Left, labelFont, textPaint);
            }
        }

        private void DrawAttribution(SKCanvas canvas, int canvasH)
        {
            using var font = new SKFont { Size = 12 };
            using var bgPaint = new SKPaint { Color = new SKColor(255, 255, 255, 180), Style = SKPaintStyle.Fill };
            using var textPaint = new SKPaint { Color = new SKColor(60, 60, 60), IsAntialias = true };
            string text = "(C) OpenStreetMap contributors";
            float w = font.MeasureText(text, textPaint);
            canvas.DrawRect(4, canvasH - 20, w + 8, 18, bgPaint);
            canvas.DrawText(text, 8, canvasH - 6, SKTextAlign.Left, font, textPaint);
        }
    }
}