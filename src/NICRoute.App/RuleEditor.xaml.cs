using Microsoft.Win32;
using System.Text.Json;

namespace NICRoute.App;

public partial class RuleEditor : Window
{
    private IReadOnlyList<ApplicationTarget> applications = [];
    private ApplicationTarget? chosen;
    private readonly AppRule? original;
    private bool reading;
    public AppRule Result { get; private set; } = new();

    public RuleEditor(IReadOnlyList<AdapterInfo> adapters, AppRule? rule = null)
    {
        original = rule;
        InitializeComponent();
        Title = rule is null ? "添加应用规则" : "编辑应用规则";
        var options = adapters.Select(a => new AdapterOption(a.Id, a.Name + " · " + a.Status)).ToList();
        foreach (var id in new[] { rule?.AdapterId, rule?.FallbackAdapterId }.Where(id => !string.IsNullOrEmpty(id)))
            if (options.All(a => a.Id != id)) options.Add(new(id!, "已移除的网卡（不可用）"));
        PrimaryAdapter.ItemsSource = options;
        BackupAdapter.ItemsSource = options.ToArray();
        PrimaryAdapter.SelectedValue = rule?.AdapterId ?? options.FirstOrDefault()?.Id;
        BackupAdapter.SelectedValue = rule?.FallbackAdapterId;
        FallbackMode.ItemsSource = new[] { new { Kind = FallbackKind.Block, Label = "不联网" },
            new { Kind = FallbackKind.GlobalDefault, Label = "使用全局默认网卡" }, new { Kind = FallbackKind.Adapter, Label = "使用其他网卡…" } };
        FallbackMode.SelectedValue = rule?.Fallback ?? FallbackKind.Block;
        if (rule is not null)
        {
            chosen = JsonSerializer.Deserialize<ApplicationTarget>(JsonSerializer.Serialize(rule.Application))!;
            SelectedLabel.Text = chosen.Name + " · " + chosen.KindLabel;
            if (chosen.Kind == ApplicationKind.Desktop) { SourceTabs.SelectedIndex = 1; ExeLabel.Text = chosen.ExecutablePath; }
        }
        Loaded += async (_, _) => await LoadAppsAsync();
    }
    private async Task LoadAppsAsync()
    {
        if (reading) return;
        reading = true;
        CatalogStatus.Text = "正在读取 Windows 应用，首次可能需要数秒…";
        try
        {
            applications = await WindowsAppCatalog.GetAppsAsync();
            CatalogStatus.Text = $"{applications.Count} 个应用 · 按包匹配，常规版本更新后规则继续有效";
            FilterApps();
        }
        catch (Exception e) { CatalogStatus.Text = "无法读取应用列表。请确认 Windows AppX 服务可用，再刷新重试。"; ErrorText.Text = e.Message; }
        finally { reading = false; }
    }
    private void FilterApps()
    {
        if (AppList is null) return;
        var query = AppSearch.Text.Trim();
        AppList.ItemsSource = applications.Where(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || a.PackageFamilyName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    private void AppSearch_Changed(object sender, TextChangedEventArgs e) => FilterApps();
    private async void Reload_Click(object sender, RoutedEventArgs e) => await LoadAppsAsync();
    private void AppList_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (AppList.SelectedItem is ApplicationTarget app)
        { chosen = app; SelectedLabel.Text = app.Name + " · Windows 应用"; ErrorText.Text = ""; }
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "应用程序 (*.exe)|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        var info = FileVersionInfo.GetVersionInfo(dialog.FileName);
        chosen = new() { Name = string.IsNullOrWhiteSpace(info.FileDescription) ? Path.GetFileNameWithoutExtension(dialog.FileName) : info.FileDescription,
            ExecutablePath = dialog.FileName, Kind = ApplicationKind.Desktop };
        ExeLabel.Text = dialog.FileName;
        SelectedLabel.Text = chosen.Name + " · 桌面程序";
        ErrorText.Text = "";
    }
    private void FallbackMode_Changed(object sender, SelectionChangedEventArgs e)
    { if (BackupAdapter is not null) BackupAdapter.Visibility = FallbackMode.SelectedValue is FallbackKind.Adapter ? Visibility.Visible : Visibility.Collapsed; }
    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (chosen is null) throw new InvalidDataException("请先选择一个应用。");
            Result = new() { Id = original?.Id ?? Guid.NewGuid(), Enabled = original?.Enabled ?? true, Application = chosen,
                AdapterId = PrimaryAdapter.SelectedValue as string ?? "", Fallback = (FallbackKind)(FallbackMode.SelectedValue ?? FallbackKind.Block),
                FallbackAdapterId = BackupAdapter.SelectedValue as string ?? "" };
            new AppSettings { Rules = [Result] }.Validate();
            DialogResult = true;
        }
        catch (Exception error) { ErrorText.Text = error.Message; }
    }
}
