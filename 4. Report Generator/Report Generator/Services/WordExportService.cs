using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Report_Generator.Models;
using NetTopologySuite.Noding;
using System.Globalization;
using System.ComponentModel;

namespace Report_Generator.Services
{
    public class WordExportService
    {
        public byte[] GenerateReport (string region, string roadName, string surveyDate, string vehicleType, List<DirectionalAverages> dirAverages, List<SegmentAverages> segAverages)
        {
            using var memoryStream = new MemoryStream();

            using (var wordDocument = WordprocessingDocument.Create(memoryStream, WordprocessingDocumentType.Document, true))
            {
                MainDocumentPart mainPart = wordDocument.AddMainDocumentPart();
                mainPart.Document = new Document();
                Body body = mainPart.Document.AppendChild(new Body());

                string nbFrom = segAverages.FirstOrDefault(s => s.Direction == "NB")?.From ?? "";
                string nbTo = segAverages.LastOrDefault(s => s.Direction == "NB")?.To ?? "";
                string sbFrom = segAverages.FirstOrDefault(s => s.Direction == "SB")?.From ?? "";
                string sbTo = segAverages.LastOrDefault(s => s.Direction == "SB")?.To ?? "";

                SetDefaultFont(mainPart, "Tahoma");

                AddTitle(body, $"{region} - {roadName} ({vehicleType})");
                AddParagraph(body, $"Survey Date: {surveyDate}");

                AddHeading1(body, "1.0 Directional Averages", true);
                AddParagraph(body, "Directional averages are presented by time period (AM, MID, PM) and represent " +
                                   "the average travel conditions across the full corridor, extending from " +
                                   $"{nbFrom} to {nbTo} (NB/EB) and from {sbFrom} to {sbTo} (SB/WB).", true);

                string[] periods = { "AM", "MID", "PM" };
                string[] directions = { "NB", "SB" };

                foreach (var period in periods)
                {   
                    AddHeading2(body, $"{period} Directional Averages", true);
                    AddDirectionalTable(body, dirAverages, period);
                    AddParagraph(body, "");
                }

                AddPageBreak(body);

                AddHeading1(body, "2.0 Segment Travel Time Analysis", true);
                AddParagraph(body, "Segment results are averaged per From–To segment across all trips, grouped by direction. ");

                foreach (var period in periods)
                {
                    AddHeading2(body, $"{period} Period", true);
                    AddPeriodSummaryParagraph(body, segAverages, period);
                    AddParagraph(body, "");
                }

                AddPageBreak(body);

                foreach (var period in periods)
                {
                    AddHeading1(body, $"{period} - Averaged Segment Results", true);
                    AddHeading1(body, "");
                    
                    foreach (var direction in directions)
                    {
                        string dirText = direction == "NB" ? "NB/EB" : "SB/WB";
                        AddHeading2(body, $"{dirText} Segment-by-segment travel time, speed, and delay analysis", true, true);
                        var segments = segAverages.Where(s => s.Period == period && s.Direction == direction).ToList();
                        AddSegmentTable(body, segments, direction);
                        AddParagraph(body, "");
                    }

                    AddParagraph(body, "");
                }




                SetA4PageLayout(body);
            }

            return memoryStream.ToArray();
        }

        // --- OPENXML METHODS ---

        private void SetA4PageLayout (Body body)
        {
            // A4 dimensions
            PageSize pageSize = new PageSize()
            {
                Width = (UInt32Value)11906U,
                Height = (UInt32Value)16838U
            };

            PageMargin pageMargin = new PageMargin()
            {
                Top = 1134,
                Right = 1134U,
                Bottom = 1134,
                Left = 1440U,
            };

            SectionProperties sectionProps = new SectionProperties();
            sectionProps.Append(pageSize);
            sectionProps.Append(pageMargin);

            body.AppendChild(sectionProps);
        }

        private void SetDefaultFont (MainDocumentPart mainPart, string fontName)
        {
            StyleDefinitionsPart stylePart = mainPart.AddNewPart<StyleDefinitionsPart>();
            stylePart.Styles = new Styles(
                new DocDefaults(
                    new RunPropertiesDefault(
                        new RunPropertiesBaseStyle(
                            new RunFonts() { Ascii = fontName, HighAnsi = fontName, ComplexScript = fontName }
                        )
                    )
                )
            );
        }

        private void AddPageBreak(Body body)
        {
            Paragraph para = body.AppendChild(new Paragraph());
            Run run = para.AppendChild(new Run());
            run.AppendChild(new Break() { Type = BreakValues.Page });
        }

        private void AddTitle (Body body, string text)
        {
            Paragraph para = body.AppendChild(new Paragraph());
            para.AppendChild(new ParagraphProperties(new SpacingBetweenLines() { After = "0" }));
            
            Run run = para.AppendChild(new Run());
            RunProperties runProps = run.AppendChild(new RunProperties());
            runProps.Append(new Bold());
            runProps.Append(new FontSize() { Val = "22" });

            run.AppendChild(new Text(text));
        }

        private void AddHeading1 (Body body, string text, bool isBold = false, bool isItalic = false)
        {
            Paragraph para = body.AppendChild(new Paragraph());
            para.AppendChild(new ParagraphProperties(new SpacingBetweenLines() { After = "0" }));

            Run run = para.AppendChild(new Run());
            RunProperties runProps = run.AppendChild(new RunProperties());
            runProps.Append(new FontSize() { Val = "22" });
            if (isBold) runProps.Append(new Bold());
            if (isItalic) runProps.Append(new Italic());

            run.AppendChild(new Text(text));
        }

        private void AddHeading2 (Body body, string text, bool isBold = false, bool isItalic = false)
        {
            Paragraph para = body.AppendChild(new Paragraph());
            para.AppendChild(new ParagraphProperties(new SpacingBetweenLines() { After = "0" }));

            Run run = para.AppendChild(new Run());
            RunProperties runProps = run.AppendChild(new RunProperties());
            runProps.Append(new FontSize() { Val = "20" });
            if (isBold) runProps.Append(new Bold());
            if (isItalic) runProps.Append(new Italic());

            run.AppendChild(new Text(text));
        }

        private void AddParagraph (Body body, string text, bool justify = false)
        {
            Paragraph para = body.AppendChild(new Paragraph());

            ParagraphProperties paraProps = new ParagraphProperties();
            if (justify) paraProps.Append(new Justification() { Val = JustificationValues.Both });
            paraProps.Append(new SpacingBetweenLines() { Line = "275" });
            para.AppendChild(paraProps);
            
            Run run = para.AppendChild(new Run());
            RunProperties runProps = run.AppendChild(new RunProperties());
            runProps.Append(new FontSize() { Val = "22" });

            run.AppendChild(new Text(text));
        }

        private void AddDirectionalTable (Body body, List<DirectionalAverages> averages, string period)
        {
            var periodData = averages.Where(s => s.Period == period).ToList();
            var nb = periodData.FirstOrDefault(d => d.Direction == "NB");
            var sb = periodData.FirstOrDefault(d => d.Direction == "SB");

            string formatTime(double? seconds) => seconds.HasValue ? TimeSpan.FromSeconds(Math.Round(seconds.Value)).ToString(@"h\:mm\:ss") : "-";
            string formatDistance(double? distance) => distance.HasValue ? (distance.Value / 1000.0).ToString("0.00") : "-";
            string formatSpeed(double? speed) => speed.HasValue ? speed.Value.ToString("0.00") : "-";

            Table table = new Table();

            TableProperties tblProp = new TableProperties(
                new TableBorders(
                    new TopBorder() { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 4, Color = "000000" },
                    new BottomBorder() { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 4, Color = "000000" },
                    new LeftBorder() { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 4, Color = "000000" },
                    new RightBorder() { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 4, Color = "000000" },
                    new InsideHorizontalBorder() { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 4, Color = "000000" },
                    new InsideVerticalBorder() { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 4, Color = "000000" }
                ),
                new TableLayout() { Type = TableLayoutValues.Fixed }
            );
            table.AppendChild (tblProp);

            TableGrid tableGrid = new TableGrid(
                new GridColumn() { Width = "2325" },
                new GridColumn() { Width = "2325" },
                new GridColumn() { Width = "2325" },
                new GridColumn() { Width = "2325" }
            );
            table.AppendChild(tableGrid);

            table.AppendChild(CreateRow(new[] { "Metric", "NB/EB", "SB/WB", "Units" }));
            table.AppendChild(CreateRow(new[] { "Avg Travel Time", formatTime(nb?.AvgTravelTimeSec), formatTime(sb?.AvgTravelTimeSec), "hh:mm:ss" }));
            table.AppendChild(CreateRow(new[] { "Avg Distance", formatDistance(nb?.AvgDistanceM), formatDistance(sb?.AvgDistanceM), "km" }));
            table.AppendChild(CreateRow(new[] { "Avg Travel Speed", formatSpeed(nb?.AvgTravelSpeedKph), formatSpeed(sb?.AvgTravelSpeedKph), "kph" }));
            table.AppendChild(CreateRow(new[] { "Avg Running Speed", formatSpeed(nb?.AvgRunningSpeedKph), formatSpeed(sb?.AvgRunningSpeedKph), "kph" }));
            table.AppendChild(CreateRow(new[] { "Avg Delay Time", formatTime(nb?.AvgDelayTimeSec), formatTime(sb?.AvgDelayTimeSec), "hh:mm:ss" }));
            table.AppendChild(CreateRow(new[] { "Avg Delay Length", formatDistance(nb?.AvgDelayLengthM), formatDistance(sb?.AvgDelayLengthM), "km" }));

            body.AppendChild(table);
        }

        private TableRow CreateRow (string[] cells, string fontSize = "22", bool center = false)
        {
            TableRow row = new TableRow();
            foreach (var text in cells)
            {
                TableCell cell = new TableCell();

                TableCellProperties tcp = new TableCellProperties(
                    new TableCellMargin(
                        new TopMargin() { Width = "0", Type = TableWidthUnitValues.Dxa },
                        new BottomMargin() { Width = "0", Type = TableWidthUnitValues.Dxa },
                        new LeftMargin() { Width = "100", Type = TableWidthUnitValues.Dxa },
                        new RightMargin() { Width = "100", Type = TableWidthUnitValues.Dxa }
                    ),
                    new TableCellVerticalAlignment() { Val = TableVerticalAlignmentValues.Center }
                );

                cell.Append(tcp);

                ParagraphProperties paraProps = new ParagraphProperties(
                    new Justification() { Val = center ? JustificationValues.Center : JustificationValues.Start },
                    new SpacingBetweenLines() { Line = "240", Before = "0", After = "0" }
                );

                RunProperties runProps = new RunProperties();
                runProps.Append(new RunFonts() { Ascii = "Tahoma", HighAnsi = "Tahoma" });
                runProps.Append(new FontSize() { Val = fontSize });

                Paragraph para = new Paragraph(paraProps, new Run(runProps, new Text(text)));

                cell.Append(para);
                row.Append(cell);
            }
            return row;
        }

        private void AddPeriodSummaryParagraph (Body body, List<SegmentAverages> segAverages, string period)
        {
            var periodSegments = segAverages
                .Where(s => s.Period == period)
                .ToList();

            if (!periodSegments.Any())
            {
                AddParagraph(body, $"No segment data was available for the {period} period.");
                return;
            }

            var slowest = periodSegments.OrderBy(s => s.TravelSpeedKph).First();
            var fastest = periodSegments.OrderByDescending(s => s.TravelSpeedKph).First();

            string getDirText(string dir) => dir == "NB" ? "Northbound" : "Southbound";
            string getSpeedText(SegmentAverages segAvg)
            {
                string ts = $"{segAvg.TravelSpeedKph:0.0} kph";
                if (segAvg.RunningSpeedKph.HasValue)
                {
                    ts += $" (running speed of {segAvg.RunningSpeedKph:0.0} kph)";
                }
                return ts;
            }
            string getDelayText(SegmentAverages segAvg)
            {
                if (!string.IsNullOrWhiteSpace(segAvg.DelayCausesSummary))
                    return segAvg.DelayCausesSummary.Trim();

                return "the dominant delay causes identified during the survey";
            }

            AddParagraph(body, $"During the {period} period, the slowest segment was observed from " +
                               $"{slowest.From} to {slowest.To} ({getDirText(slowest.Direction)}), " +
                               $"recording the lowest average travel speed of {getSpeedText(slowest)}. " +
                               $"This reduced performance was primarily influenced by {getDelayText(slowest)}.", true);

            AddParagraph(body, $"In contrast, the fastest segment during the {period} period was recorded from " +
                               $"{fastest.From} to {fastest.To} ({getDirText(fastest.Direction)}), " +
                               $"with an average travel speed of {getSpeedText(fastest)}, " +
                               $"indicating relatively uninterrupted traffic flow.", true);
        }

        private void AddSegmentTable (Body body, List<SegmentAverages> segAverages, string direction)
        {
            if (!segAverages.Any())
            {
                AddParagraph(body, $"No {direction} Data");
                return;
            }

            string formatVal(double? val) => val.HasValue ? val.Value.ToString("0.00") : "";

            Table table = new Table();

            TableProperties tblProp = new TableProperties(
                new TableBorders(
                    new TopBorder() { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 4, Color = "000000" },
                    new BottomBorder() { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 4, Color = "000000" },
                    new LeftBorder() { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 4, Color = "000000" },
                    new RightBorder() { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 4, Color = "000000" },
                    new InsideHorizontalBorder() { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 4, Color = "000000" },
                    new InsideVerticalBorder() { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 4, Color = "000000" }
                ),
                new TableWidth() { Width = "0", Type = TableWidthUnitValues.Auto }
            );
            table.AppendChild(tblProp);

            TableGrid tableGrid = new TableGrid(
                new GridColumn() { Width = "675" },  
                new GridColumn() { Width = "675" }, 
                new GridColumn() { Width = "850" }, 
                new GridColumn() { Width = "950" }, 
                new GridColumn() { Width = "925" }, 
                new GridColumn() { Width = "925" }, 
                new GridColumn() { Width = "900" }, 
                new GridColumn() { Width = "850" }, 
                new GridColumn() { Width = "1700" }, 
                new GridColumn() { Width = "950" }   
            );
            table.AppendChild(tableGrid);

            table.AppendChild(CreateRow(new[] { "From", 
                "To", "Travel Time Sec", "Distance", "Travel Speed Kph", 
                "Running Speed Kph", "Delays", "Delay Length", "Delay Causes Summary", "Segment Distance" }, "20", true));

            double cumulativeDistance = 0;

            foreach (var seg in segAverages)
            {
                double currentDistance = seg.DistanceM ?? 0;
                cumulativeDistance += currentDistance;

                table.AppendChild(CreateRow(new[]
                {
                    seg.From,
                    seg.To,
                    formatVal(seg.TravelTimeSec),
                    formatVal(cumulativeDistance),
                    formatVal(seg.TravelSpeedKph),
                    formatVal(seg.RunningSpeedKph),
                    formatVal(seg.DelayTimeSec),
                    formatVal(seg.DelayLengthM),
                    string.IsNullOrWhiteSpace(seg.DelayCausesSummary) ? "" : seg.DelayCausesSummary,
                    formatVal(seg.DistanceM)
                }, "18", true));
            }

            body.AppendChild(table);
        }
    }
}