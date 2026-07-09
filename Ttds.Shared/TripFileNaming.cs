using System.Text.RegularExpressions;

namespace Ttds.Shared;
public static class TripFileNaming
{
    public record TripInfo(string VehicleCode, string VehicleName, string TripNo, string DtToken, string Date);

    public static string VehicleNameFromCode(string? code) => (code ?? "").Trim() switch
    {
        "1" => "PrivateCar",
        "2" => "UV",
        "3" => "Jeepney",
        "4" => "BUS",
        _ => "UnknownVehicle"
    };

    public static TripInfo? ParseTripInfo(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var mv = Regex.Match(name, @"\bGPX_(\d+)_", RegexOptions.IgnoreCase);
        var vehCode = mv.Success ? mv.Groups[1].Value : "0";
        var vehName = VehicleNameFromCode(vehCode);

        var m = Regex.Match(name, @"-(\d+)_((\d{8})-(\d{6}))", RegexOptions.IgnoreCase);
        if (!m.Success)
            return new TripInfo(vehCode, vehName, "0", "00000000-000000", "00000000");

        return new TripInfo(vehCode, vehName, m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
    }
}