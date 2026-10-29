using System.ComponentModel;
using System.Globalization;

namespace NICRoute.App;

public sealed class AdapterCard(AdapterInfo info) : INotifyPropertyChanged
{
    private AdapterInfo info = info;
    private AdapterTraffic traffic = new(TrafficState.Collecting, null, null, 0, 0);
    public string Id => info.Id;
    public string Name => info.Name;
    public string Description => info.Description;
    public string Status => info.Status;
    public bool Available => info.Available;
    public string AddressLabel => info.AddressLabel;
    public string UploadRate => Rate(traffic.UploadBytesPerSecond);
    public string DownloadRate => Rate(traffic.DownloadBytesPerSecond);
    public string Uploaded => Bytes(traffic.UploadedBytes);
    public string Downloaded => Bytes(traffic.DownloadedBytes);
    public string MonitorStatus => traffic.State switch
    {
        TrafficState.Live => "实时 · 每秒刷新", TrafficState.Disconnected => "网卡已断开",
        TrafficState.Unavailable => "暂时无法读取", _ => "正在采样…"
    };
    public string TrafficDetail => $"{MonitorStatus}\n本次监测累计：上行 {Uploaded} · 下行 {Downloaded}\n统计整张网卡，包含其他应用、局域网和后台流量。";
    public event PropertyChangedEventHandler? PropertyChanged;
    public void UpdateInfo(AdapterInfo value) { info = value; Changed(); }
    public void UpdateTraffic(AdapterTraffic value) { traffic = value; Changed(); }
    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    private string Rate(double? value) => traffic.State == TrafficState.Disconnected ? "0 B/s" :
        value is { } rate ? Bytes(rate) + "/s" : "—";
    private static string Bytes(double value)
    {
        var units = new[] { "B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB" };
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return value.ToString(unit == 0 ? "F0" : "F1", CultureInfo.CurrentCulture) + " " + units[unit];
    }
}
