namespace Ttds.Shared;
public enum PeakPeriod { AM, MID, PM, OFF }

public static class PeakPeriodUtils
{
    public static PeakPeriod Classify(TimeSpan timeOfDay)
    {
        if (timeOfDay >= new TimeSpan(6, 0, 0) && timeOfDay <= new TimeSpan(10, 29, 59)) return PeakPeriod.AM;
        if (timeOfDay >= new TimeSpan(10, 30, 0) && timeOfDay <= new TimeSpan(15, 29, 59)) return PeakPeriod.MID;
        if (timeOfDay >= new TimeSpan(15, 30, 0) && timeOfDay <= new TimeSpan(19, 30, 0)) return PeakPeriod.PM;
        return PeakPeriod.OFF;
    }

    public static string FolderName(PeakPeriod p) => p.ToString(); // "AM" | "MID" | "PM" | "OFF"

    public static string Label(PeakPeriod p) => p switch
    {
        PeakPeriod.AM => "AM Peak (06:00–10:00)",
        PeakPeriod.MID => "Mid Peak (10:00–15:00)",
        PeakPeriod.PM => "PM Peak (15:00–19:30)",
        _ => "Off-Peak"
    };
}