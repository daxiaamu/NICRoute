namespace NICRoute.App;

public sealed class ApplicationTrafficCard(ApplicationTraffic snapshot) : INotifyPropertyChanged
{
    private ApplicationTraffic snapshot = snapshot;
    public string Key => snapshot.Key;
    public string Name => snapshot.Name;
    public string Detail => snapshot.Detail;
    public string Kind => snapshot.Configured ? "已配置" : "自动发现";
    public string UploadRate => snapshot.UploadRate is { } value ? TrafficDisplay.Bytes(value) + "/s" : "—";
    public string DownloadRate => snapshot.DownloadRate is { } value ? TrafficDisplay.Bytes(value) + "/s" : "—";
    public string Uploaded => TrafficDisplay.Bytes(snapshot.Uploaded);
    public string Downloaded => TrafficDisplay.Bytes(snapshot.Downloaded);
    public string ActualRoute => snapshot.ActualRoute;
    public string Status => snapshot.Status;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Update(ApplicationTraffic value) { snapshot = value; PropertyChanged?.Invoke(this, new(string.Empty)); }
}

internal static class TrafficDisplay
{
    internal static string Bytes(double value)
    {
        var units = new[] { "B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB" }; var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return value.ToString(unit == 0 ? "F0" : "F1", System.Globalization.CultureInfo.CurrentCulture) + " " + units[unit];
    }
}
