namespace NICRoute.App;

public partial class MainWindow
{
    internal string VerifyTrafficPreview()
    {
        if (!preview) throw new InvalidOperationException("流量界面检查仅允许预览模式运行。");
        if (!ReferenceEquals(AdapterItems.ItemsSource, TrafficItems.ItemsSource) || adapterCards.Count != adapters.Count)
            throw new InvalidOperationException("流量列表未绑定全部网卡。");
        var cards = adapterCards.ToArray();
        SyncAdapterCards();
        if (!cards.SequenceEqual(adapterCards)) throw new InvalidOperationException("刷新网卡不应重新创建流量视图。");
        if (adapters.Any(a => a.LinkUp) && !adapterCards.Any(c => c.UploadRate.EndsWith("/s") && c.DownloadRate.EndsWith("/s")))
            throw new InvalidOperationException("活动网卡未显示速率。");
        if (adapterCards.Any(c => c.TrafficDetail.Contains("NaN") || c.TrafficDetail.Contains("Infinity")))
            throw new InvalidOperationException("流量格式不应包含无效数值。");
        return "PASS: native adapter counters bind to both traffic views; card identity survives refresh.";
    }
    // Exercise the real WPF controls and handlers using only --preview sample rules.
    internal string VerifyRuleManagementPreview()
    {
        if (!preview) throw new InvalidOperationException("管理界面检查仅允许在预览模式运行。");
        var original = SettingsStore.Clone(settings);
        var originalDirty = dirty;
        var originalNotice = NoticeText.Text;
        var originalTab = MainTabs.SelectedItem;
        var checks = new List<string>();
        RuleRow[] Visible() => ((IEnumerable<RuleRow>)RuleItems.ItemsSource).ToArray();
        void Require(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + name);
            checks.Add("PASS: " + name);
        }
        try
        {
            ResetRuleFilters_Click(this, new RoutedEventArgs());
            Require(Visible().Length == 3, "all existing configured applications are listed");
            RuleSearch.Text = "MICROSOFT";
            Require(Visible().Length == 2, "search is case-insensitive");
            RuleSearch.Text = "Microsoft.Todos_8wekyb3d8bbwe";
            Require(Visible().Length == 1, "Windows application package identity can be searched");
            RuleKindFilter.SelectedValue = "desktop";
            Require(Visible().Length == 0 && EmptyState.Visibility == Visibility.Visible && !SelectVisibleRules.IsEnabled,
                "combined filters show a useful empty state");
            ResetRuleFilters_Click(this, new RoutedEventArgs());
            RuleKindFilter.SelectedValue = "package";
            SelectVisibleRules_Click(this, new RoutedEventArgs());
            Require(selectedRuleIds.Count == 1 && RuleBatchActions.Visibility == Visibility.Visible,
                "select all only selects visible configured applications");
            BatchRules_Click(new Button { Tag = "disable" }, new RoutedEventArgs());
            Require(settings.Rules.Single(r => r.Application.Kind == ApplicationKind.WindowsPackage).Enabled == false &&
                settings.Rules.Where(r => r.Application.Kind == ApplicationKind.Desktop).All(r => r.Enabled),
                "batch disable leaves hidden applications unchanged");
            RuleKindFilter.SelectedValue = "desktop";
            Require(selectedRuleIds.Count == 0 && RuleBatchActions.Visibility == Visibility.Collapsed,
                "filtering out a selection removes it from batch actions");
            SelectVisibleRules_Click(this, new RoutedEventArgs());
            BatchRules_Click(new Button { Tag = "enable" }, new RoutedEventArgs());
            Require(!settings.Rules.Single(r => r.Application.Kind == ApplicationKind.WindowsPackage).Enabled,
                "batch enable cannot enable a hidden application");
            RuleKindFilter.SelectedValue = "all";
            RuleStateFilter.SelectedValue = "disabled";
            Require(Visible().Length == 1, "disabled applications remain centrally manageable");
            var chosen = settings.Rules.First(r => r.Application.Kind == ApplicationKind.Desktop);
            RevealConfiguredRule(chosen); MarkDirty();
            Require(Visible().Length == 3 && selectedRuleIds.SetEquals([chosen.Id]) &&
                RuleSummary.Text.Contains("待保存") && ConfiguredAppsTab.IsSelected,
                "added or edited applications are revealed with a pending-save indication");
            ToggleRule_Click(new Button { Tag = chosen }, new RoutedEventArgs());
            Require(!chosen.Enabled, "individual enable or disable acts on the configured rule");
            ClearRuleSelection_Click(this, new RoutedEventArgs());
            Require(selectedRuleIds.Count == 0 && RuleBatchActions.Visibility == Visibility.Collapsed,
                "selection can be cleared");
            return string.Join(Environment.NewLine, checks);
        }
        finally
        {
            settings = original; dirty = originalDirty; selectedRuleIds.Clear();
            ResetRuleFilters_Click(this, new RoutedEventArgs()); RefreshRows();
            NoticeText.Text = originalNotice;
            MainTabs.SelectedItem = originalTab;
        }
    }
}
