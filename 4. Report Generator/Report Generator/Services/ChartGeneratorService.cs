using ScottPlot;
using Report_Generator.Models;
using System.Collections.Generic;
using System.Linq;

namespace Report_Generator.Services
{
    public class ChartGeneratorService
    {
        public byte[]? GenerateSpeedPairChart (List<SegmentAverages> segments, string title, string direction)
        {
            if (segments == null || !segments.Any()) return null;

            int count = segments.Count;
            double?[] x_km = new double?[count];
            double?[] travelSpeeds = new double?[count];
            double?[] runningSpeeds = new double?[count];

            double? cummulativeDistance = 0;
            for (int i = 0; i < count; i++)
            {
                cummulativeDistance += (segments[i].DistanceM)/1000.0;

                x_km[i] = cummulativeDistance;
                travelSpeeds[i] = segments[i].TravelSpeedKph;
                runningSpeeds[i] = segments[i].RunningSpeedKph;
            }

            var plot = new ScottPlot.Plot();

            var travelPlot = plot.Add.Scatter(x_km, travelSpeeds);
            travelPlot.Label = "Travel Speed (KPH)";
            travelPlot.Color = Colors.Blue;
            travelPlot.LineWidth = 2;
            travelPlot.MarkerStyle.Size = 6;
            travelPlot.MarkerStyle.Shape = MarkerShape.FilledCircle;
            travelPlot.MarkerStyle.MarkerColor = Colors.Orange;
            travelPlot.ConnectStyle = ConnectStyle.StepHorizontal;

            var runningPlot = plot.Add.Scatter(x_km, runningSpeeds);
            runningPlot.Label = "Running Speed (KPH)";
            runningPlot.Color = Colors.Green;
            runningPlot.LineWidth = 2;
            runningPlot.MarkerStyle.Size = 6;
            runningPlot.MarkerStyle.Shape = MarkerShape.FilledCircle;
            runningPlot.MarkerStyle.MarkerColor = Colors.Red;
            runningPlot.ConnectStyle = ConnectStyle.StepHorizontal;

            plot.Title(title);
            plot.Axes.Title.Label.FontSize = 26;
            plot.Axes.Bottom.Label.Text = "Distance (km)";
            plot.Axes.Left.Label.Text = "Speed (km/h)";
            plot.Axes.Bottom.Label.FontSize = 20;
            plot.Axes.Left.Label.FontSize = 20;

            var legend = plot.ShowLegend();
            legend.BackgroundColor = Colors.White.WithAlpha(0.8);
            legend.OutlineColor = Colors.LightGray;
            legend.FontSize = 20;

            if (direction == "NB") plot.ShowLegend(Alignment.UpperLeft); 
            if (direction == "SB") plot.ShowLegend(Alignment.UpperRight); 
            plot.Grid.MajorLineColor = Colors.LightGray.WithAlpha(0.3);

            return plot.GetImageBytes(1650, 900, ImageFormat.Png);
        }
    }
}
