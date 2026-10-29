using System.Windows.Documents;
using System.Text.RegularExpressions;

namespace NICRoute.App;

public partial class UpdateDialog : Window
{
    private readonly UpdateManifest manifest;
    private readonly bool required;
    private readonly Func<IProgress<DownloadProgress>,CancellationToken,Task<string>> download;
    private readonly Func<string,CancellationToken,Task> install;
    private readonly CancellationTokenSource cancellation;
    private bool busy,allowClose,closed,acceptProgress;
    public bool Skipped { get; private set; }
    public UpdateDialog(UpdateManifest manifest,bool required,Func<IProgress<DownloadProgress>,CancellationToken,Task<string>> download,
        Func<string,CancellationToken,Task> install,CancellationToken lifetime)
    {
        this.manifest=manifest; this.required=required; this.download=download; this.install=install;
        cancellation=CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        InitializeComponent();
        TitleText.Text=required?"需要更新才能继续使用":"发现新版本";
        NotesViewer.Document=SafeMarkdown.Document(manifest);
        if(required) { SkipButton.Visibility=Visibility.Collapsed; IgnoreButton.Content="退出程序"; }
    }
    private void Skip_Click(object sender,RoutedEventArgs e) { Skipped=true; allowClose=true; Close(); }
    private void Ignore_Click(object sender,RoutedEventArgs e) { allowClose=true; Close(); }
    private async void Download_Click(object sender,RoutedEventArgs e)
    {
        if(busy) return;
        busy=true; DownloadButton.IsEnabled=SkipButton.IsEnabled=false;
        DownloadBar.Visibility=Visibility.Visible; DownloadBar.IsIndeterminate=true;
        ProgressText.Text="正在连接下载源 · MUDL 多镜像下载";
        try
        {
            acceptProgress=true;
            var progress=new Progress<DownloadProgress>(p=>
            {
                if(closed||!acceptProgress) return;
                DownloadBar.IsIndeterminate=p.Total<=0;
                if(p.Total>0) DownloadBar.Value=Math.Min(99,p.Downloaded*100.0/p.Total);
                var percent=p.Total>0?$"{Math.Min(99,p.Downloaded*100/p.Total)}%":"";
                DownloadButton.Content="下载中 "+percent;
                ProgressText.Text=$"{p.Downloaded/1048576.0:F1} / {p.Total/1048576.0:F1} MB · {p.BytesPerSecond/1048576.0:F1} MB/s · {p.Connections} 个连接";
            });
            var file=await download(progress,cancellation.Token);
            acceptProgress=false;
            cancellation.Token.ThrowIfCancellationRequested();
            DownloadBar.IsIndeterminate=false; DownloadBar.Value=100;
            DownloadButton.Content="正在准备更新…";
            ProgressText.Text="下载校验通过，正在验证安装文件并准备重启。";
            await install(file,cancellation.Token);
            allowClose=true; Close();
        }
        catch(OperationCanceledException) { if(!closed) ProgressText.Text="下载已取消，可重试。"; }
        catch(Exception error)
        {
            if(!closed)
            {
                var paragraph=new Paragraph(new Run(error.Message)){Foreground=(Brush)FindResource("Danger")};
                NotesViewer.Document.Blocks.Add(paragraph); ProgressText.Text="更新未完成，当前版本保持不变。";
            }
        }
        finally
        {
            acceptProgress=false;
            if(!closed) { busy=false; DownloadButton.IsEnabled=true; SkipButton.IsEnabled=!required; DownloadButton.Content="重试下载并更新"; DownloadBar.IsIndeterminate=false; }
        }
    }
    private void Dialog_Closing(object? sender,CancelEventArgs e)
    {
        if(required&&!allowClose) { e.Cancel=true; return; }
        closed=true; cancellation.Cancel();
    }
}

internal static class SafeMarkdown
{
    public static FlowDocument Document(UpdateManifest manifest)
    {
        var doc=new FlowDocument { FontFamily=(FontFamily)System.Windows.Application.Current.FindResource("BodyFont"), FontSize=13, PagePadding=new Thickness(0), LineHeight=23 };
        doc.Blocks.Add(new Paragraph(new Run("NICRoute "+manifest.VersionName)){FontSize=20,FontWeight=FontWeights.SemiBold});
        if(manifest.LocalPublishedAt is { } time) doc.Blocks.Add(new Paragraph(new Run("发布时间："+time)){FontSize=12,Foreground=(Brush)System.Windows.Application.Current.FindResource("Muted")});
        var code=false;
        foreach(var line in manifest.Changelog.Replace("\r", "").Split('\n'))
        {
            if(line.StartsWith("```")) { code=!code; continue; }
            var text=line;
            var paragraph=new Paragraph { Margin=new Thickness(0,0,0,8) };
            if(code) { paragraph.FontFamily=new FontFamily("Consolas"); paragraph.Background=(Brush)System.Windows.Application.Current.FindResource("Base"); paragraph.Inlines.Add(new Run(text)); }
            else
            {
                var heading=Regex.Match(text,@"^(#{1,3})\s+(.+)$");
                if(heading.Success) { paragraph.FontSize=20-heading.Groups[1].Length*2; paragraph.FontWeight=FontWeights.SemiBold; text=heading.Groups[2].Value; }
                else if(Regex.IsMatch(text,@"^\s*[-*]\s+")) text="• "+Regex.Replace(text,@"^\s*[-*]\s+", "");
                else if(text.StartsWith("> ")) { text=text[2..]; paragraph.FontStyle=FontStyles.Italic; }
                else if(Regex.IsMatch(text,@"^\s*([-*_])\1{2,}\s*$")) text="────────────";
                Inline(paragraph,text);
            }
            doc.Blocks.Add(paragraph);
        }
        return doc;
    }
    private static void Inline(Paragraph paragraph,string text)
    {
        var pattern=@"\*\*(.+?)\*\*|\*([^*]+)\*|`([^`]+)`|\[([^\]]+)\]\((https?://[^\s)]+)\)";
        var position=0;
        foreach(Match match in Regex.Matches(text,pattern))
        {
            paragraph.Inlines.Add(new Run(text[position..match.Index]));
            if(match.Groups[1].Success) paragraph.Inlines.Add(new Bold(new Run(match.Groups[1].Value)));
            else if(match.Groups[2].Success) paragraph.Inlines.Add(new Italic(new Run(match.Groups[2].Value)));
            else if(match.Groups[3].Success) paragraph.Inlines.Add(new Run(match.Groups[3].Value){FontFamily=new FontFamily("Consolas")});
            else if(Uri.TryCreate(match.Groups[5].Value,UriKind.Absolute,out var uri)&&uri.Scheme is "http" or "https")
            {
                var link=new Hyperlink(new Run(match.Groups[4].Value)){NavigateUri=uri};
                link.RequestNavigate+=(_,e)=>{ Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri){UseShellExecute=true}); e.Handled=true; };
                paragraph.Inlines.Add(link);
            }
            position=match.Index+match.Length;
        }
        paragraph.Inlines.Add(new Run(text[position..]));
    }
}
