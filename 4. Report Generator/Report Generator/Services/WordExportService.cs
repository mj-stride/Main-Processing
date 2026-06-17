using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Report_Generator.Models;
using NetTopologySuite.Noding;

namespace Report_Generator.Services
{
    public class WordExportService
    {
        public byte[] GenerateReport(string region, string roadName, string surveyDate, string vehicleType, List<DirectionalAverages> dirAverages, List<SegmentAverages> segAverages)
        {
            using var memoryStream = new MemoryStream();

            using (var wordDocument = WordprocessingDocument.Create(memoryStream, WordprocessingDocumentType.Document, true))
            {
                MainDocumentPart mainPart = wordDocument.AddMainDocumentPart();
                mainPart.Document = new Document();
                Body body = mainPart.Document.AppendChild(new Body());

                string nbFrom = segAverages.First(s => s.Direction == "NB").From;
                string nbTo = segAverages.Last(s => s.Direction == "NB").To;
                string sbFrom = segAverages.First(s => s.Direction == "SB").From;
                string sbTo = segAverages.Last(s => s.Direction == "SB").To;

                SetDefaultFont(mainPart, "Tahoma");

                AddTitle(body, $"{region} - {roadName} ({vehicleType})");
                AddParagraph(body, $"Survey Date: {surveyDate}");

                AddHeading1(body, "1.0 Directional Averages", true);
                AddParagraph(body, "Directional averages are presented by time period (AM, MID, PM) and represent " +
                                   "the average travel conditions across the full corridor, extending from " +
                                   $"{nbFrom} to {nbTo} (NB/EB) and from {sbFrom} to {sbTo} (SB/WB).");

                string[] periods = { "AM", "MID", "PM" };
                foreach (var period in periods)
                {   
                    AddHeading2(body, $"{period} Directional Averages", true);
                    AddDirectionalTable(body, dirAverages, period);
                    AddParagraph(body, "");
                }

                SetA4PageSize(body);
            }

            return memoryStream.ToArray();
        }

        // --- OPENXML METHODS ---

        private void SetA4PageSize(Body body)
        {
            // A4 dimensions
            PageSize pageSize = new PageSize()
            {
                Width = (UInt32Value)11906U,
                Height = (UInt32Value)16838U
            };

            SectionProperties sectionProps = new SectionProperties();
            sectionProps.Append(pageSize);

            body.AppendChild(sectionProps);
        }

        private void SetDefaultFont(MainDocumentPart mainPart, string fontName)
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

        private void AddTitle(Body body, string text)
        {
            Paragraph para = body.AppendChild(new Paragraph());
            Run run = para.AppendChild(new Run());
            RunProperties runProps = run.AppendChild(new RunProperties());
            
            runProps.Append(new Bold());
            runProps.Append(new FontSize() { Val = "22" });
            para.PrependChild(new ParagraphProperties(new SpacingBetweenLines() { After = "0" }));

            run.AppendChild(new Text(text));
        }

        private void AddHeading1(Body body, string text, bool isBold = false)
        {
            Paragraph para = body.AppendChild(new Paragraph());
            Run run = para.AppendChild(new Run());
            RunProperties runProps = run.AppendChild(new RunProperties());

            runProps.Append(new FontSize() { Val = "22" });
            para.PrependChild(new ParagraphProperties(new SpacingBetweenLines() { After = "0" }));
            if (isBold)
            {
                runProps.Append(new Bold());
            }

            run.AppendChild(new Text(text));
        }

        private void AddHeading2(Body body, string text, bool isBold = false)
        {
            Paragraph para = body.AppendChild(new Paragraph());
            Run run = para.AppendChild(new Run());
            RunProperties runProps = run.AppendChild(new RunProperties());

            runProps.Append(new FontSize() { Val = "20" });
            para.PrependChild(new ParagraphProperties(new SpacingBetweenLines() { After = "0" }));

            if (isBold)
            {
                runProps.Append(new Bold());
            }

            run.AppendChild(new Text(text));
        }

        private void AddParagraph(Body body, string text)
        {
            Paragraph para = body.AppendChild(new Paragraph());
            Run run = para.AppendChild(new Run());
            RunProperties runProps = run.AppendChild(new RunProperties());

            runProps.Append(new FontSize() { Val = "22" });

            run.AppendChild(new Text(text));
        }

        private void AddDirectionalTable(Body body, List<DirectionalAverages> averages, string period)
        {
            var periodData = averages.Where(s => s.Period == period).ToList();
            var nb = periodData.FirstOrDefault(d => d.Direction == "NB");
            var sb = periodData.FirstOrDefault(d => d.Direction == "SB");

            string formatTime(double? seconds) => seconds.HasValue ? TimeSpan.FromSeconds(Math.Round(seconds.Value)).ToString(@"h\:mm\:ss") : "";
            string formatDistance(double? distance) => distance.HasValue ? (distance.Value / 1000.0).ToString("0.00") : "";
            string formatSpeed(double? speed) => speed.HasValue ? speed.Value.ToString("0.00") : "";

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
            table.AppendChild(tblProp);

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

        private TableRow CreateRow(string[] cells)
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

                RunProperties runProps = new RunProperties();
                runProps.Append(new RunFonts() { Ascii = "Tahoma", HighAnsi = "Tahoma" });
                runProps.Append(new FontSize() { Val = "22" });

                Paragraph para = new Paragraph(new Run(runProps, new Text(text)));

                para.PrependChild(new ParagraphProperties(new SpacingBetweenLines() { Before = "0", After = "0" }));

                cell.Append(para);
                row.Append(cell);
            }
            return row;
        }
    }
}