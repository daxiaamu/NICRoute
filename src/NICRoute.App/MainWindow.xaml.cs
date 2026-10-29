using Microsoft.Win32;
using System.Text.Json;
using System.Windows.Threading;

namespace NICRoute.App;

public sealed record AdapterOption(string Id, string Label);
public sealed record RuleRow(AppRule Rule, string Name, string Detail, string KindLabel, string Glyph,
    string Primary, string Fallback, string Route, Brush RouteBrush, string EnabledLabel, string ToggleLabel, bool IsSelected);

public partial class MainWindow : Window
{
    private readonly SettingsStore store;
    private readonly EngineController engine;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer trafficTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private readonly AdapterTrafficMonitor trafficMonitor = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<AdapterCard> adapterCards = [];
    private readonly ObservableCollection<ApplicationTrafficCard> applicationCards = [];
    private bool trafficPolling;
    private Task trafficPoll = Task.CompletedTask;
    private readonly Queue<string> logs = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> pendingLogs = new();
    private readonly DispatcherTimer logTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Dictionary<string, int> probeFailures = [];
    private readonly HashSet<Guid> selectedRuleIds = [];
    private readonly bool preview;
    private AppSettings settings = new();
    private AppSettings activeSettings = new();
    private IReadOnlyList<AdapterInfo> adapters = [];
    private System.Windows.Forms.NotifyIcon? tray;
    private bool loading = true, busy, polling, dirty, closing;
    private readonly CancellationTokenSource updateLifetime=new();
    private UpdatePreferences updatePreferences=new();
    private UpdatePreferenceStore? updatePreferenceStore;
    private UpdateHttp? updateHttp;
    private UpdateManager? updates;
    private UpdateDialog? updateDialog;
    private readonly ProductIdentity product=ProductIdentity.Read(Path.Combine(AppContext.BaseDirectory,"NICRoute.dll"));
    public string DataDirectory => store.DirectoryPath;

    public MainWindow(bool preview = false, string? dataDirectory = null)
    {
        this.preview = preview;
        store = new(preview ? Path.Combine(AppContext.BaseDirectory, "preview-data") : dataDirectory);
        engine = new(Path.Combine(AppContext.BaseDirectory, "engine", "sing-box.exe"), Path.Combine(store.DirectoryPath, "runtime"));
        InitializeComponent();
        AdapterItems.ItemsSource = TrafficItems.ItemsSource = adapterCards;
        ApplicationTrafficItems.ItemsSource = System.Windows.Data.CollectionViewSource.GetDefaultView(applicationCards);
        System.Windows.Data.CollectionViewSource.GetDefaultView(applicationCards).Filter = value => value is ApplicationTrafficCard card &&
            (card.Name.Contains(ApplicationSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase) || card.Detail.Contains(ApplicationSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!preview)
        {
            try { settings = store.Load(); }
            catch (Exception e)
            {
                System.Windows.MessageBox.Show("读取配置失败，原文件尚未改动。可导入有效配置或重新设置后保存。\n" + e.Message, "NICRoute", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        adapters = AdapterCatalog.GetAdapters();
        if (preview) AddPreviewData();
        activeSettings = SettingsStore.Clone(settings);
        engine.ApplicationTraffic.Configure(activeSettings, adapters);
        engine.Log += AddLog;
        engine.UnexpectedExit += () => Dispatcher.BeginInvoke(() =>
        {
            SetStatus("引擎已退出", false);
            StartButton.Content = "启动分流";
            NoticeText.Text = engine.HasNetworkGuard ? "引擎已退出，受控应用的网卡约束保留。请重启分流，或从托盘选择停止分流以解除。" : "分流引擎意外退出，Windows 已恢复自身路由。请检查日志后重启分流。";
            tray?.ShowBalloonTip(4000, "NICRoute 分流已停止", "引擎意外退出，请检查日志。", System.Windows.Forms.ToolTipIcon.Warning);
        });
        UpdateControls();
        loading = false;
        RefreshRows();
        RefreshApplicationTraffic();
        VersionText.Text = $"NICRoute {product.VersionName} · Windows 10/11 x64 · .NET 10 · sing-box 1.14.4\n配置目录：" + store.DirectoryPath;
        InitializeUpdates();
        timer.Tick += async (_, _) => await PollAdaptersAsync();
        logTimer.Tick += (_, _) => FlushLogs();
        trafficTimer.Tick += (_, _) => QueueTrafficPoll();
        if (!preview)
        {
            SetupTray();
            timer.Start();
            logTimer.Start();
            trafficTimer.Start();
            Loaded += (_, _) => QueueTrafficPoll();
        }
        else NoticeText.Text = "界面预览 · 示例应用规则，不启动分流、不保存配置。";
    }

    private void SyncAdapterCards()
    {
        var ids = adapters.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var card in adapterCards.Where(c => !ids.Contains(c.Id)).ToArray()) adapterCards.Remove(card);
        for (var index = 0; index < adapters.Count; index++)
        {
            var adapter = adapters[index];
            var card = adapterCards.FirstOrDefault(c => c.Id.Equals(adapter.Id, StringComparison.OrdinalIgnoreCase));
            if (card is null) { card = new(adapter); adapterCards.Insert(index, card); }
            else { card.UpdateInfo(adapter); if (adapterCards.IndexOf(card) != index) adapterCards.Move(adapterCards.IndexOf(card), index); }
        }
        TrafficEmpty.Visibility = adapterCards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void QueueTrafficPoll()
    {
        if (closing || trafficPolling) return;
        trafficPoll = PollTrafficAsync();
    }
    private async Task PollTrafficAsync()
    {
        trafficPolling = true;
        try
        {
            var snapshot = adapters.ToArray();
            var readings = await Task.Run(() => { engine.RefreshObservedInterfaces(snapshot); return trafficMonitor.Sample(snapshot); });
            if (closing) return;
            foreach (var card in adapterCards)
                if (readings.TryGetValue(card.Id, out var reading)) card.UpdateTraffic(reading);
            RefreshApplicationTraffic();
        }
        catch (Exception error) when (error is System.Net.NetworkInformation.NetworkInformationException or InvalidOperationException or NotSupportedException)
        { if (!closing) AddLog("流量监控：" + error.Message); }
        finally { trafficPolling = false; }
    }
    private void RefreshApplicationTraffic()
    {
        var refilter = false;
        foreach (var snapshot in engine.ApplicationTraffic.Snapshot())
        {
            var card = applicationCards.FirstOrDefault(c => c.Key.Equals(snapshot.Key, StringComparison.OrdinalIgnoreCase));
            if (card is null) applicationCards.Add(new(snapshot));
            else { refilter |= card.Name != snapshot.Name || card.Detail != snapshot.Detail; card.Update(snapshot); }
        }
        if (refilter && ApplicationSearch.Text.Length > 0) System.Windows.Data.CollectionViewSource.GetDefaultView(applicationCards).Refresh();
        ApplicationTrafficEmpty.Visibility = ApplicationTrafficItems.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplicationMonitorStatus.Text = engine.IsReady ? "每秒刷新 · 仅统计分流接管的连接 · 实际网卡来自连接出口，不是规则预览。" :
            "启动分流后显示每个应用的流量和实际出口；下方保留本次软件运行已采集的累计量。";
    }
    private void ApplicationSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (ApplicationTrafficItems is null) return;
        System.Windows.Data.CollectionViewSource.GetDefaultView(applicationCards).Refresh();
        ApplicationTrafficEmpty.Visibility = ApplicationTrafficItems.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    internal void PreviewApplicationTraffic()
    {
        if (!preview || settings.Rules.Count == 0) return;
        engine.ApplicationTraffic.Begin(); engine.ApplicationTraffic.SetOnline(true);
        var metadata = new ObservedConnection("preview-connection", settings.Rules[0].Application.InstallLocation + "\\ToDo.exe", 1234,
            "tcp", "192.0.2.1:443", adapters[0].Tag, [adapters[0].Tag, settings.Rules[0].SelectorTag], 0, 0, false);
        engine.ApplicationTraffic.Receive(new(true, [new(0, metadata.Id, metadata, 0, 0)]));
        engine.ApplicationTraffic.Snapshot(Stopwatch.GetTimestamp() - Stopwatch.Frequency);
        engine.ApplicationTraffic.Receive(new(false, [new(1, metadata.Id, null, 81920, 245760)]));
        RefreshApplicationTraffic();
        ApplicationMonitorStatus.Text = "界面预览 · 示例应用流量，不启动分流。实际运行时由引擎连接事件统计。";
        ApplicationTrafficTab.IsSelected = true;
    }
    internal async Task PrimeTrafficPreviewAsync()
    {
        if (!preview) throw new InvalidOperationException("仅用于预览采样。");
        await PollTrafficAsync(); await Task.Delay(1100); await PollTrafficAsync();
    }

    private void InitializeUpdates()
    {
        InstalledVersionText.Text=$"当前版本 {product.VersionName} · 版本代码 {product.VersionCode} · Windows x64";
        updatePreferenceStore=new(store.DirectoryPath);
        if(!preview)
            try { updatePreferences=updatePreferenceStore.Load(); }
            catch(Exception error) when(error is IOException or InvalidDataException or System.Text.Json.JsonException)
            { AddLog("更新设置读取失败："+error.Message); updatePreferences.CheckOnStartup=false; }
        var wasLoading=loading; loading=true;
        UpdateChannel.SelectedValue=updatePreferences.Channel;
        UpdateConnections.ItemsSource=new[]{1,4,8,16,32}; UpdateConnections.SelectedItem=updatePreferences.DownloadConnections;
        AutoUpdateCheck.IsChecked=updatePreferences.CheckOnStartup;
        loading=wasLoading;
        if(preview) return;
        updateHttp=new();
        var feed=new UpdateFeed(updateHttp,updatePreferences,updatePreferenceStore.Save);
        feed.Diagnostic+=message=>AddLog("更新源："+message);
        updates=new(product.VersionCode,updatePreferences,feed.CheckAsync,updatePreferenceStore.Save);
        Loaded+=async(_,_)=>
        {
            try { await Task.Delay(2000,updateLifetime.Token); if(updatePreferences.CheckOnStartup&&!closing) await CheckUpdatesAsync(false); }
            catch(OperationCanceledException) { }
        };
    }
    private void UpdatePreference_Changed(object sender,RoutedEventArgs e)
    {
        if(loading||preview||updatePreferenceStore is null) return;
        updatePreferences.CheckOnStartup=AutoUpdateCheck.IsChecked==true;
        updatePreferences.Channel=UpdateChannel.SelectedValue as string??"stable";
        updatePreferences.DownloadConnections=UpdateConnections.SelectedItem as int? ??32;
        try { updatePreferenceStore.Save(updatePreferences); }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException) { AddLog("更新设置保存失败："+error.Message); UpdateStatusText.Text=error.Message; }
    }
    private async void CheckUpdate_Click(object sender,RoutedEventArgs e)=>await CheckUpdatesAsync(true);
    private async Task CheckUpdatesAsync(bool manual)
    {
        if(updates is null||closing) return;
        if(manual) CheckUpdateButton.IsEnabled=false;
        UpdateChannel.IsEnabled=false;
        UpdateStatusText.Text="正在检查更新…";
        var result=await updates.CheckAsync(manual,updateLifetime.Token);
        if(closing) return;
        CheckUpdateButton.IsEnabled=UpdateChannel.IsEnabled=true;
        UpdateDot.Visibility=updates.KnownUpdate is null?Visibility.Collapsed:Visibility.Visible;
        UpdateStatusText.Text=result.Status switch
        {
            UpdateStatus.Current=>$"当前已是最新版本（{product.VersionName}）",
            UpdateStatus.Available=>$"可更新到 {result.Manifest!.VersionName}，已验证官方更新信息。",
            _=>"检查失败："+result.Error
        };
        if(!updates.TryClaimPresentation(result,manual)) return;
        if(result.Status!=UpdateStatus.Available)
        {
            if(manual||result.ManualRequested) System.Windows.MessageBox.Show(this,UpdateStatusText.Text,"NICRoute 软件更新",MessageBoxButton.OK,
                result.Status==UpdateStatus.Failed?MessageBoxImage.Information:MessageBoxImage.None);
            return;
        }
        if(updateDialog is not null) { updateDialog.Activate(); return; }
        var manifest=result.Manifest!;
        var required=updates.IsRequired(manifest)||manifest.RequiredFor(product.VersionCode);
        if(required&&engine.IsRunning) await StopRoutingAsync();
        var downloader=new UpdateDownloader(new MudlDownloadEngine(Path.Combine(AppContext.BaseDirectory,"engine","mudl.exe"),updateHttp!.ResolveHttpsAsync));
        updateDialog=new(manifest,required,
            (progress,token)=>downloader.DownloadAsync(manifest,Path.Combine(store.DirectoryPath,"updates","downloads"),updatePreferences.DownloadConnections,progress,token),
            (zip,token)=>InstallUpdateAsync(manifest,zip,token),updateLifetime.Token){Owner=this};
        try
        {
            updateDialog.ShowDialog();
            if(updateDialog.Skipped) updates.Skip(manifest);
            if(required&&!closing) Close();
        }
        finally { updateDialog=null; }
    }
    private async Task InstallUpdateAsync(UpdateManifest manifest,string zip,CancellationToken token)
    {
        if(updates?.KnownUpdate is { } latest&&(latest.VersionCode!=manifest.VersionCode||latest.Sha256!=manifest.Sha256))
            throw new InvalidDataException("已有更新的版本信息，请重新检查更新。");
        var wasRunning=engine.IsRunning;
        var plan=await UpdateInstaller.PrepareAsync(manifest,zip,store.DirectoryPath,AppContext.BaseDirectory,wasRunning,token);
        token.ThrowIfCancellationRequested();
        wasRunning=engine.IsRunning;
        if(plan.ResumeRouting!=wasRunning)
        {
            plan=plan with{ResumeRouting=wasRunning};
            await File.WriteAllBytesAsync(Path.Combine(plan.SessionDirectory,"install-plan.json"),UpdateJson.Bytes(plan),token);
        }
        ReadNetworkOptions(); store.Save(settings); dirty=false;
        if(wasRunning) await engine.StopAsync();
        try { UpdateInstaller.Launch(plan); }
        catch
        {
            if(wasRunning) await engine.StartAsync(activeSettings,adapters);
            throw;
        }
        Close();
    }

    private void AddPreviewData()
    {
        // Preview-only examples never enter the real user's settings or start an engine.
        if (adapters.Count == 0) return;
        var first = adapters[0];
        settings.GlobalAdapterId = first.Id;
        settings.Rules =
        [
            new() { Application = new() { Kind = ApplicationKind.WindowsPackage, Name = "Microsoft To Do", PackageName = "Microsoft.Todos", PublisherId = "8wekyb3d8bbwe", PackageFamilyName = "Microsoft.Todos_8wekyb3d8bbwe", InstallLocation = @"C:\Program Files\WindowsApps\Microsoft.Todos_1.0.0.0_x64__8wekyb3d8bbwe" }, AdapterId = first.Id, Fallback = FallbackKind.Block },
            new() { Application = new() { Name = "Microsoft Edge", ExecutablePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe" }, AdapterId = adapters.Last().Id, Fallback = FallbackKind.GlobalDefault },
            new() { Application = new() { Name = "下载工具", ExecutablePath = @"D:\Apps\Download\download.exe" }, AdapterId = first.Id, Fallback = FallbackKind.Adapter, FallbackAdapterId = adapters.Last().Id }
        ];
    }

    private void SetupTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("管理已配置应用", null, (_, _) => Dispatcher.Invoke(() => { ShowWindow(); ConfiguredAppsTab.IsSelected = true; }));
        menu.Items.Add("停止分流", null, async (_, _) => await Dispatcher.InvokeAsync(StopRoutingAsync).Task.Unwrap());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(Close));
        tray = new() { Text = "NICRoute · 尚未启动", Visible = true, ContextMenuStrip = menu,
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindow);
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) Hide(); };
    }
    private void ShowWindow() { Show(); WindowState = WindowState.Normal; Activate(); }

    private List<AdapterOption> Options(bool includeSystem)
    {
        var options = adapters.Select(a => new AdapterOption(a.Id, $"{a.Name}  ·  {a.Status}")).ToList();
        if (includeSystem) options.Insert(0, new("", "Windows 自动选择"));
        if (!string.IsNullOrEmpty(settings.GlobalAdapterId) && options.All(a => a.Id != settings.GlobalAdapterId))
            options.Add(new(settings.GlobalAdapterId, "已移除的网卡（不可用）"));
        return options;
    }
    private void UpdateControls()
    {
        var wasLoading = loading; loading = true;
        GlobalAdapter.ItemsSource = Options(true);
        GlobalAdapter.SelectedValue = settings.GlobalAdapterId;
        ProbeEnabled.IsChecked = settings.ProbeInternet;
        ProbeAddress.Text = settings.ProbeAddress;
        ProbePort.Text = settings.ProbePort.ToString();
        DnsAddress.Text = settings.DnsServer;
        SyncAdapterCards();
        UpdateAdapterSummary();
        loading = wasLoading;
    }
    private string AdapterName(string id) => adapters.FirstOrDefault(a => a.Id == id)?.Name ?? "已移除的网卡";
    private void UpdateAdapterSummary()
    {
        AdapterSummary.Text = $"{adapters.Count(a => a.Available)}/{adapters.Count} 网卡可用 · " +
            (activeSettings.ProbeInternet ? "联网检测开 · 流量1秒" : "仅检测链路 · 流量1秒");
        AdapterSummary.Foreground = (Brush)FindResource("Muted");
    }
    private void OpenNetworkOptions_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => NetworkTab.IsSelected = true;
    private void RefreshRows()
    {
        if (RuleItems is null) return;
        var search = RuleSearch.Text?.Trim() ?? "";
        var kind = RuleKindFilter.SelectedValue as string ?? "all";
        var state = RuleStateFilter.SelectedValue as string ?? "all";
        var matching = settings.Rules.Where(r =>
            (r.Application.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || r.Application.Detail.Contains(search, StringComparison.OrdinalIgnoreCase)) &&
            (kind == "all" || (kind == "package") == (r.Application.Kind == ApplicationKind.WindowsPackage)) &&
            (state == "all" || (state == "enabled") == r.Enabled)).ToArray();
        // Batch actions always apply to the visible selection, including after a filter or import.
        selectedRuleIds.IntersectWith(matching.Select(r => r.Id));
        RuleItems.ItemsSource = matching.Select(rule =>
        {
            var route = RuleResolver.Resolve(rule, settings, adapters);
            var routePending = engine.IsReady && !dirty &&
                engine.GetSelectedOutbound(rule.Enabled ? rule.SelectorTag : RuleResolver.GlobalTag) != route.Outbound;
            var fallback = rule.Fallback switch { FallbackKind.Block => "不联网", FallbackKind.GlobalDefault => "全局默认", _ => AdapterName(rule.FallbackAdapterId) };
            return new RuleRow(rule, rule.Application.Name, rule.Application.Detail, rule.Application.KindLabel,
                rule.Application.Kind == ApplicationKind.WindowsPackage ? "\uE782" : "\uE7F4", AdapterName(rule.AdapterId), fallback,
                routePending ? "出口待更新 · 请重新应用" : route.Label,
                (Brush)FindResource(routePending ? "Warning" : route.IsBlocked ? "Danger" : route.IsFallback ? "Warning" : "Success"),
                rule.Enabled ? "已启用" : "已停用 · 跟随全局", rule.Enabled ? "停用" : "启用", selectedRuleIds.Contains(rule.Id));
        }).ToArray();
        EmptyState.Visibility = matching.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = settings.Rules.Count == 0 ? "还没有应用规则" : "没有匹配的应用";
        EmptyDetail.Text = settings.Rules.Count == 0 ? "点击“添加应用”，已有的应用规则会统一显示在这里。" : "试试其他名称、程序路径或应用包名，也可以重置类型和状态筛选。";
        RuleSummary.Text = $"共 {settings.Rules.Count} 个应用 · {settings.Rules.Count(r => r.Enabled)} 个已启用 · " +
            (dirty ? "有修改待保存并应用" : "在这里统一管理首选网卡和回退规则");
        RuleCount.Text = $"显示 {matching.Length} / {settings.Rules.Count} 个应用";
        RouteColumnTitle.Text = "规则出口预览";
        SaveButton.Content = dirty ? "保存并应用 *" : "保存并应用";
        RefreshSelectionControls();
    }
    private void RefreshSelectionControls()
    {
        if (RuleBatchActions is null) return;
        var visible = (RuleItems.ItemsSource as IEnumerable<RuleRow>)?.Count() ?? 0;
        SelectVisibleRules.IsEnabled = visible > 0;
        SelectVisibleRules.IsChecked = selectedRuleIds.Count == 0 ? false : selectedRuleIds.Count == visible ? true : null;
        RuleBatchActions.Visibility = selectedRuleIds.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        RuleCount.Visibility = selectedRuleIds.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RuleSelectionCount.Text = $"已选择 {selectedRuleIds.Count} 项";
    }
    private void SelectRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: Guid id } box) return;
        if (box.IsChecked == true) selectedRuleIds.Add(id); else selectedRuleIds.Remove(id);
        RefreshSelectionControls();
    }
    private void SelectVisibleRules_Click(object sender, RoutedEventArgs e)
    {
        var visible = (RuleItems.ItemsSource as IEnumerable<RuleRow>)?.Select(r => r.Rule.Id).ToArray() ?? [];
        var allSelected = visible.Length > 0 && visible.All(selectedRuleIds.Contains);
        selectedRuleIds.Clear();
        if (!allSelected) selectedRuleIds.UnionWith(visible);
        RefreshRows();
    }
    private void ClearRuleSelection_Click(object sender, RoutedEventArgs e) { selectedRuleIds.Clear(); RefreshRows(); }
    private void RuleFilter_Changed(object sender, SelectionChangedEventArgs e) => RefreshRows();
    private void ResetRuleFilters_Click(object sender, RoutedEventArgs e)
    {
        RuleSearch.Clear(); RuleKindFilter.SelectedValue = "all"; RuleStateFilter.SelectedValue = "all";
        RefreshRows();
    }
    private bool ConfirmRuleRemoval(IReadOnlyList<AppRule> rules) => System.Windows.MessageBox.Show(this,
        (rules.Count == 1 ? $"删除“{rules[0].Application.Name}”的应用规则？" : $"删除所选 {rules.Count} 个应用的规则？") +
        "\n保存并应用后，这些应用将跟随全局默认网卡。应用本身不会被卸载。", "删除应用规则", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    private void BatchRules_Click(object sender, RoutedEventArgs e)
    {
        var selected = settings.Rules.Where(r => selectedRuleIds.Contains(r.Id)).ToArray();
        if (selected.Length == 0 || sender is not Button { Tag: string action }) return;
        if (action == "delete")
        {
            if (!ConfirmRuleRemoval(selected)) return;
            settings.Rules.RemoveAll(r => selectedRuleIds.Contains(r.Id)); selectedRuleIds.Clear();
        }
        else if (action is "enable" or "disable")
            foreach (var rule in selected) rule.Enabled = action == "enable";
        else return;
        MarkDirty();
    }
    private void MarkDirty()
    {
        if (loading) return;
        dirty = true;
        NoticeText.Text = "有未应用的修改。点击“保存并应用”后生效；运行中重新应用会短暂重连。";
        RefreshRows();
    }
    private void GlobalAdapter_Changed(object sender, SelectionChangedEventArgs e)
    { if (!loading && GlobalAdapter.SelectedValue is string id) { settings.GlobalAdapterId = id; MarkDirty(); } }
    private void NetworkOption_Changed(object sender, RoutedEventArgs e) => MarkDirty();
    private void NetworkText_Changed(object sender, TextChangedEventArgs e) => MarkDirty();
    private void RuleSearch_Changed(object sender, TextChangedEventArgs e) => RefreshRows();

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RuleEditor(adapters) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (settings.Rules.Any(r => r.Application.Identity.Equals(dialog.Result.Application.Identity, StringComparison.OrdinalIgnoreCase)))
        { ShowError("该应用已有规则，请直接编辑现有规则。"); return; }
        settings.Rules.Add(dialog.Result); RevealConfiguredRule(dialog.Result); MarkDirty();
    }
    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not AppRule rule) return;
        var dialog = new RuleEditor(adapters, rule) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (settings.Rules.Any(r => r.Id != rule.Id && r.Application.Identity.Equals(dialog.Result.Application.Identity, StringComparison.OrdinalIgnoreCase)))
        { ShowError("该应用已有规则，请直接编辑现有规则。"); return; }
        settings.Rules[settings.Rules.IndexOf(rule)] = dialog.Result; RevealConfiguredRule(dialog.Result); MarkDirty();
    }
    private void RevealConfiguredRule(AppRule rule)
    {
        ConfiguredAppsTab.IsSelected = true;
        ResetRuleFilters_Click(this, new RoutedEventArgs());
        selectedRuleIds.Clear(); selectedRuleIds.Add(rule.Id);
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (closing) return;
            RuleItems.UpdateLayout();
            var row = (RuleItems.ItemsSource as IEnumerable<RuleRow>)?.FirstOrDefault(r => r.Rule.Id == rule.Id);
            if (row is not null && RuleItems.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement element) element.BringIntoView();
        }, DispatcherPriority.Loaded);
    }
    private void ToggleRule_Click(object sender, RoutedEventArgs e)
    { if (((Button)sender).Tag is AppRule rule) { rule.Enabled = !rule.Enabled; MarkDirty(); } }
    private void Delete_Click(object sender, RoutedEventArgs e)
    { if (((Button)sender).Tag is AppRule rule && ConfirmRuleRemoval([rule])) { settings.Rules.Remove(rule); MarkDirty(); } }

    private void ReadNetworkOptions()
    {
        settings.ProbeInternet = ProbeEnabled.IsChecked == true;
        settings.ProbeAddress = ProbeAddress.Text.Trim();
        if (!int.TryParse(ProbePort.Text, out var port)) throw new InvalidDataException("检测端口请输入 1–65535 的数字。");
        settings.ProbePort = port;
        settings.DnsServer = DnsAddress.Text.Trim();
        settings.Validate();
    }
    private async Task SaveAndApplyAsync()
    {
        ReadNetworkOptions();
        // Refresh versioned/moved package paths when saving, without changing package identity.
        if (settings.Rules.Any(r => r.Application.Kind == ApplicationKind.WindowsPackage))
        {
            try { WindowsAppCatalog.RefreshTargets(settings, await WindowsAppCatalog.GetAppsAsync()); }
            catch (Exception e) { AddLog("未能刷新 Windows 应用目录，保留已有匹配：" + e.Message); }
        }
        store.Save(settings);
        var restart = engine.IsRunning;
        if (restart) { SetStatus("重新应用中", false); await engine.StopAsync(); }
        activeSettings = SettingsStore.Clone(settings);
        engine.ApplicationTraffic.Configure(activeSettings, adapters);
        probeFailures.Clear();
        UpdateAdapterSummary();
        dirty = false;
        if (restart)
        {
            adapters = await ScanAdaptersAsync();
            await engine.StartAsync(activeSettings, adapters);
            SetStatus("分流运行中", true);
        }
        NoticeText.Text = restart ? "配置已保存并应用。应用可能需要重新建立连接。" : "配置已保存，启动分流后生效。";
        RefreshRows();
    }
    private async void Save_Click(object sender, RoutedEventArgs e) => await RunBusyAsync(SaveAndApplyAsync);
    private async void Start_Click(object sender, RoutedEventArgs e)
    { if (engine.IsRunning) await RunBusyAsync(StopRoutingAsync); else await StartRoutingAsync(); }
    public async Task StartRoutingAsync() => await RunBusyAsync(async () =>
    {
        if (preview) return;
        if(updates?.RequiredUpdatePending==true)
        {
            await CheckUpdatesAsync(true);
            if(updates.RequiredUpdatePending) throw new InvalidOperationException("当前版本需要更新，请在“软件更新”中重试或退出软件。");
            return;
        }
        await SaveAndApplyAsync();
        if (!App.IsAdministrator)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
            start.ArgumentList.Add("--start"); start.ArgumentList.Add("--takeover");
            // Preserve the initiating user's profile even if UAC uses another admin account.
            start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(store.DirectoryPath);
            Process.Start(start);
            Close(); return;
        }
        SetStatus("正在启动", false);
        adapters = await ScanAdaptersAsync();
        UpdateControls();
        await engine.StartAsync(activeSettings, adapters);
        SetStatus("分流运行中", true);
        StartButton.Content = "停止分流";
        NoticeText.Text = "正在按规则分流。最小化后驻留通知区域；关闭窗口会停止分流并退出。";
        RefreshRows();
    });
    private async Task StopRoutingAsync()
    {
        await engine.StopAsync();
        SetStatus("尚未启动", false);
        StartButton.Content = "启动分流";
        NoticeText.Text = "分流已停止，恢复 Windows 自身路由。";
        RefreshRows();
    }
    private async Task RunBusyAsync(Func<Task> action)
    {
        if (busy) return;
        busy = true; StartButton.IsEnabled = SaveButton.IsEnabled = MainTabs.IsEnabled = GlobalAdapter.IsEnabled = false;
        try { await action(); }
        catch (Exception e)
        {
            AddLog(e.ToString());
            if (!engine.IsRunning) { SetStatus("未启动", false); StartButton.Content = "启动分流"; }
            NoticeText.Text = e.Message.Split('\n')[0];
            ShowError(e.Message);
        }
        finally { busy = false; StartButton.IsEnabled = SaveButton.IsEnabled = MainTabs.IsEnabled = GlobalAdapter.IsEnabled = true; }
    }
    private void SetStatus(string text, bool running)
    {
        StatusText.Text = text;
        StatusDot.Fill = (Brush)FindResource(running ? "Success" : "Muted");
        if (tray is not null) tray.Text = "NICRoute · " + text;
    }
    private async Task<IReadOnlyList<AdapterInfo>> ScanAdaptersAsync()
    {
        var fresh = AdapterCatalog.GetAdapters();
        if (!activeSettings.ProbeInternet) return fresh;
        var tested = await AdapterCatalog.CheckInternetAsync(fresh, activeSettings.ProbeAddress, activeSettings.ProbePort);
        return tested.Select(adapter =>
        {
            if (!adapter.LinkUp || adapter.Addresses.Count == 0) { probeFailures.Remove(adapter.Id); return adapter; }
            if (adapter.InternetReachable != false) { probeFailures.Remove(adapter.Id); return adapter; }
            var failures = probeFailures.GetValueOrDefault(adapter.Id) + 1;
            probeFailures[adapter.Id] = failures;
            return failures < 2 ? adapter with { InternetReachable = null } : adapter;
        }).ToArray();
    }
    private async Task PollAdaptersAsync()
    {
        if (busy || polling || closing) return;
        polling = true;
        try
        {
            adapters = await ScanAdaptersAsync();
            // Only saved settings affect live traffic; draft edits remain a preview.
            if (engine.IsReady) await engine.UpdateSelectionsAsync(activeSettings, adapters);
            loading = true;
            var draftId = settings.GlobalAdapterId;
            GlobalAdapter.ItemsSource = Options(true); GlobalAdapter.SelectedValue = draftId;
            SyncAdapterCards();
            UpdateAdapterSummary();
            loading = false;
            RefreshRows();
        }
        catch (Exception e) { AddLog("自动检测：" + e.Message); NoticeText.Text = "出口更新未完成，请检查日志；新插入的网卡可能需要重新保存并应用。"; }
        finally { loading = false; polling = false; }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await PollAdaptersAsync();
    private void AddLog(string line)
    {
        pendingLogs.Enqueue($"{DateTime.Now:HH:mm:ss}  {line}");
        while (pendingLogs.Count > 1000) pendingLogs.TryDequeue(out _);
    }
    private void FlushLogs()
    {
        if (pendingLogs.IsEmpty) return;
        while (pendingLogs.TryDequeue(out var line))
        {
            logs.Enqueue(line);
            while (logs.Count > 500) logs.Dequeue();
        }
        LogText.Text = string.Join(Environment.NewLine, logs);
        LogText.ScrollToEnd();
    }
    private void ClearLog_Click(object sender, RoutedEventArgs e) { pendingLogs.Clear(); logs.Clear(); LogText.Clear(); }
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "NICRoute 配置 (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { settings = SettingsStore.Read(dialog.FileName); UpdateControls(); MarkDirty(); }
        catch (Exception error) { ShowError("导入失败：" + error.Message); }
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "NICRoute 配置 (*.json)|*.json", FileName = "NICRoute-settings.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { ReadNetworkOptions(); File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(settings, SettingsStore.JsonOptions)); NoticeText.Text = "配置已导出。"; }
        catch (Exception error) { ShowError("导出失败：" + error.Message); }
    }
    private static void ShowError(string message) => System.Windows.MessageBox.Show(message, "NICRoute", MessageBoxButton.OK, MessageBoxImage.Warning);
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        e.Cancel = true;
        if (dirty && !preview)
        {
            var choice = System.Windows.MessageBox.Show(this, "有未保存的修改。是否保存后退出？", "NICRoute", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel) return;
            if (choice == MessageBoxResult.Yes)
            {
                try { ReadNetworkOptions(); store.Save(settings); }
                catch (Exception error) { ShowError(error.Message); return; }
            }
        }
        closing = true; timer.Stop(); logTimer.Stop(); trafficTimer.Stop(); updateLifetime.Cancel();
        IsEnabled = false;
        try { await trafficPoll; if(updates is not null) await updates.DrainAsync(); await engine.DisposeAsync(); }
        finally { updateHttp?.Dispose(); tray?.Dispose(); _ = Dispatcher.BeginInvoke(Close); }
    }
}
