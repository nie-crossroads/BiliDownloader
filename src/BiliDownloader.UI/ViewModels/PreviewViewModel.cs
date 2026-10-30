using System.Collections.ObjectModel;
using System.Windows.Media;
using BiliDownloader.Core.BiliApi;
using BiliDownloader.Core.Models;
using BiliDownloader.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BiliDownloader.UI.ViewModels;

/// <summary>选集 chip。</summary>
public partial class PageChipViewModel : ObservableObject
{
    public PageChipViewModel(PageInfo page, bool isSelected)
    {
        Cid = page.Cid;
        Index = page.Index;
        Part = page.Part;
        IsSelected = isSelected;
    }

    public long Cid { get; }
    public int Index { get; }
    public string Part { get; }

    public string Display => $"{Index:00} {Part}";

    [ObservableProperty] private bool _isSelected;
}

/// <summary>画质下拉项：内部是 (qn, codecid) 二元组，文案带编码（视觉规范 §6.3）。</summary>
public sealed class QualityChoice
{
    public QualityChoice(QualityOption option) => Option = option;

    public QualityOption Option { get; }
    public string Display => Option.Display;
    public bool IsAvailable => Option.IsAvailable;
    public int Qn => Option.Qn;
    public int CodecId => Option.CodecId;
    public long Bandwidth => Option.Bandwidth;
}

public sealed class AudioChoice
{
    public AudioChoice(AudioOption option) => Option = option;

    public AudioOption Option { get; }
    public string Display => Option.Display;
    public bool IsAvailable => Option.IsAvailable;
    public int Id => Option.Id;
    public long Bandwidth => Option.Bandwidth;
}

/// <summary>预览卡视图模型：标题 / UP / 选集 / 画质 / 音质 / 摘要。</summary>
public partial class PreviewViewModel : ObservableObject
{
    private readonly VideoInfo _info;

    public PreviewViewModel(VideoInfo info, AppSettings settings)
    {
        _info = info;

        Title = info.Title;
        UpName = string.IsNullOrWhiteSpace(info.UpName) ? "未知 UP" : info.UpName!;
        DurationText = Formatters.Duration(info.Duration);
        IsMultiPage = info.IsMultiPage;
        HasSeasonNotice = info.HasUgcSeason;
        IsPaid = info.IsPaidContent;

        var codecs = info.Qualities.Where(q => q.IsAvailable)
            .Select(q => QualityCatalog.CodecName(q.CodecId))
            .Distinct()
            .ToList();
        MetaText = $"{UpName}　·　共 {info.Pages.Count} P　·　{(codecs.Count > 0 ? string.Join(" / ", codecs) : "—")}";

        // 默认选中规则：链接带 ?p=N 就只选该 P；没带则只选 P1。
        // 其余分 P 通过「选择更多」弹框勾选，避免多 P 视频一进来就全选。
        var defaultPage = info.SelectedPage ?? 1;
        Pages = new ObservableCollection<PageChipViewModel>(
            info.Pages.Select(p => new PageChipViewModel(p, p.Index == defaultPage)));
        foreach (var chip in Pages) chip.PropertyChanged += (_, _) => RefreshSelectionNote();

        Qualities = new ObservableCollection<QualityChoice>(info.Qualities.Select(q => new QualityChoice(q)));
        Audios = new ObservableCollection<AudioChoice>(info.Audios.Select(a => new AudioChoice(a)));

        var defaultQuality = QualityCatalog.PickDefault(info.Qualities, settings.PreferredQn, settings.PreferredCodec);
        var defaultAudio = QualityCatalog.PickDefaultAudio(info.Audios, settings.PreferredAudioId);

        _selectedQuality = defaultQuality is null
            ? null
            : Qualities.FirstOrDefault(q => q.Qn == defaultQuality.Qn && q.CodecId == defaultQuality.CodecId);
        _selectedAudio = defaultAudio is null ? null : Audios.FirstOrDefault(a => a.Id == defaultAudio.Id);

        RefreshSelectionNote();
    }

    public string Bvid => _info.Bvid;
    public string Title { get; }
    public string UpName { get; }
    public string? UpNameOrNull => _info.UpName;
    public TimeSpan Duration => _info.Duration;
    public int TotalPages => _info.Pages.Count;
    public string DurationText { get; }
    public string MetaText { get; }
    public string? CoverUrl => _info.CoverUrl;
    public bool IsMultiPage { get; }
    public bool HasSeasonNotice { get; }
    public bool IsPaid { get; }

    [ObservableProperty] private ImageSource? _coverImage;

    public ObservableCollection<PageChipViewModel> Pages { get; }
    public ObservableCollection<QualityChoice> Qualities { get; }
    public ObservableCollection<AudioChoice> Audios { get; }

    [ObservableProperty] private QualityChoice? _selectedQuality;
    [ObservableProperty] private AudioChoice? _selectedAudio;
    [ObservableProperty] private string _selectionNote = string.Empty;

    /// <summary>
    /// 预览卡上那唯一一个选集 chip 的文案：始终是「01 标题」，长度随标题，超长会被截断。
    /// 完整列表不铺在预览卡上，多 P 视频的选集全靠「选择更多」弹框（视觉规范 §6.4）。
    /// </summary>
    public string CurrentPageText { get; private set; } = "未选择分 P";

    /// <summary>多选时还选了多少集，例如「+11」。单独一个字段是为了不被标题的截断吃掉。</summary>
    public string CurrentPageBadge { get; private set; } = string.Empty;

    public bool HasPageBadge { get; private set; }

    /// <summary>标题过长被截断时，完整文案走 ToolTip。</summary>
    public string CurrentPageTip { get; private set; } = string.Empty;

    public bool CanDownload =>
        !IsPaid && SelectedQuality is { IsAvailable: true } && SelectedPages.Count > 0;

    public IReadOnlyList<PageChipViewModel> SelectedPages =>
        Pages.Where(p => p.IsSelected).ToList();

    public IReadOnlyList<long> SelectedCids => SelectedPages.Select(p => p.Cid).ToList();

    public string PaidNoticeText => "该视频为充电专属 / 付费内容，v1 不支持下载";

    public string SeasonNoticeText => "该视频属于合集，v1 仅下载当前视频";

    public async Task LoadCoverAsync()
    {
        CoverImage = await ImageCache.LoadAsync(CoverUrl, 344).ConfigureAwait(true);
    }

    /// <summary>
    /// 下载入队后把选集勾选清掉：用户要重新挑一集才能再下一次，
    /// 免得连点两次「下载」把同一批分 P 重复入队。预览卡本身保留。
    /// </summary>
    public void ClearPageSelection()
    {
        foreach (var chip in Pages) chip.IsSelected = false;
        RefreshSelectionNote();
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var chip in Pages) chip.IsSelected = true;
        RefreshSelectionNote();
    }

    [RelayCommand]
    private void InvertSelection()
    {
        foreach (var chip in Pages) chip.IsSelected = !chip.IsSelected;
        RefreshSelectionNote();
    }

    partial void OnSelectedQualityChanged(QualityChoice? value) => RefreshSelectionNote();

    partial void OnSelectedAudioChanged(AudioChoice? value) => RefreshSelectionNote();

    /// <summary>「已选 3 P · 约 640 MB」：按所选画质 + 音质带宽 × 时长估算。</summary>
    private void RefreshSelectionNote()
    {
        var selected = SelectedPages;
        if (selected.Count == 0)
        {
            SelectionNote = "未选择分 P";
        }
        else
        {
            var bytes = selected.Sum(chip =>
            {
                var page = _info.Pages.FirstOrDefault(p => p.Cid == chip.Cid);
                var seconds = (page?.Duration ?? TimeSpan.Zero).TotalSeconds;
                if (seconds <= 0) seconds = _info.Duration.TotalSeconds / Math.Max(1, _info.Pages.Count);
                var bandwidth = (SelectedQuality?.Bandwidth ?? 0) + (SelectedAudio?.Bandwidth ?? 0);
                return bandwidth * seconds / 8;
            });

            SelectionNote = bytes > 0
                ? $"已选 {selected.Count} P · 约 {Formatters.Bytes((long)bytes)}"
                : $"已选 {selected.Count} P";
        }

        RefreshCurrentPage();

        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(SelectedPages));
        OnPropertyChanged(nameof(SelectedCids));
    }

    private void RefreshCurrentPage()
    {
        var selected = SelectedPages;
        if (selected.Count == 0)
        {
            CurrentPageText = "未选择分 P";
            CurrentPageBadge = string.Empty;
            HasPageBadge = false;
            CurrentPageTip = string.Empty;
        }
        else
        {
            var first = selected[0];
            CurrentPageText = first.Display;
            CurrentPageBadge = selected.Count > 1 ? $"+{selected.Count - 1}" : string.Empty;
            HasPageBadge = selected.Count > 1;
            CurrentPageTip = selected.Count == 1
                ? first.Part
                : string.Join(Environment.NewLine, selected.Select(p => p.Display));
        }

        OnPropertyChanged(nameof(CurrentPageText));
        OnPropertyChanged(nameof(CurrentPageBadge));
        OnPropertyChanged(nameof(HasPageBadge));
        OnPropertyChanged(nameof(CurrentPageTip));
    }

    /// <summary>
    /// 登录态变化后拿新的 VideoInfo 重建预览卡时，把用户已经选好的东西搬回来：
    /// 分 P、画质、音质。能匹配的尽量保留，匹配不上（权限变了 / 新视频没有这一档）就退回默认值。
    /// </summary>
    public void RestoreSelection(IReadOnlyCollection<long> cids, int? qn, int? codecId, int? audioId)
    {
        if (cids.Count > 0)
        {
            var wanted = cids.ToHashSet();
            foreach (var chip in Pages) chip.IsSelected = wanted.Contains(chip.Cid);

            // 一个都没对上（视频分 P 数量变了）时保底选第一集，否则下载按钮会永远不可用。
            if (Pages.All(p => !p.IsSelected) && Pages.Count > 0) Pages[0].IsSelected = true;
        }

        var quality = qn is null
            ? null
            : Qualities.FirstOrDefault(q => q.Qn == qn && q.CodecId == codecId && q.IsAvailable)
              ?? Qualities.FirstOrDefault(q => q.Qn == qn && q.IsAvailable)
              ?? Qualities.FirstOrDefault(q => q.IsAvailable);
        if (quality is not null) SelectedQuality = quality;

        var audio = audioId is null
            ? null
            : Audios.FirstOrDefault(a => a.Id == audioId && a.IsAvailable)
              ?? Audios.FirstOrDefault(a => a.IsAvailable);
        if (audio is not null) SelectedAudio = audio;

        RefreshSelectionNote();
    }
}
