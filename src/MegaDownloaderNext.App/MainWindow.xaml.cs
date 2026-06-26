using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Interop;
using MegaDownloaderNext.Core.Downloads;
using MegaDownloaderNext.Core.Links;
using MegaDownloaderNext.Core.Mega;
using MegaDownloaderNext.Core.Transfers;
using WpfKey = System.Windows.Input.Key;
using WpfKeyboard = System.Windows.Input.Keyboard;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfModifierKeys = System.Windows.Input.ModifierKeys;
using WpfDragEventArgs = System.Windows.DragEventArgs;
using WinForms = System.Windows.Forms;

namespace MegaDownloaderNext.App;

public partial class MainWindow : Window
{
    private const int DefaultConcurrentDownloads = 2;
    private const int MinConcurrentDownloads = 1;
    private const int MaxConcurrentDownloads = 6;
    private const string AppDataDirectoryName = "MegaDownloaderPlus";
    private const string PreviousAppDataDirectoryName = "MegaDownloadManager";
    private const string LegacyAppDataDirectoryName = "MegaDownloaderNext";
    private const string QueueFileName = "queue.json";
    private const long MaxDroppedTextFileBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan FolderExpansionTimeout = TimeSpan.FromMinutes(5);
    private static readonly HashSet<string> SupportedDroppedTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt",
        ".url",
        ".md",
        ".html",
        ".htm",
        ".log",
        ".csv"
    };

    private readonly MainWindowModel _model = new();
    private readonly ICollectionView _itemsView;
    private readonly DownloadQueue _queue = new();
    private readonly MegaApiTransferService _megaTransfers = new();
    private MegaAccountSession? _currentMegaSession;
    private CancellationTokenSource? _downloadCancellation;
    private bool _isRunning;
    private bool _bandwidthLimitReached;
    private bool _shutdownRequested;
    private HwndSource? _windowSource;
    private IntPtr _windowHandle;

    private const int WM_CLIPBOARDUPDATE = 0x031D;
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    public MainWindow()
    {
        InitializeComponent();
        var settings = LoadSettings();
        _model.DownloadDirectory = NormalizeDownloadDirectory(settings.DownloadDirectory);
        _model.ConcurrentDownloads = NormalizeConcurrentDownloads(settings.ConcurrentDownloads);
        _model.AutoStartAfterAdd = settings.AutoStartAfterAdd ?? false;
        _model.StopOnBandwidthLimit = settings.StopOnBandwidthLimit ?? true;
        _model.MediaPlayerPath = settings.MediaPlayerPath ?? string.Empty;
        _model.AutoExtract = settings.AutoExtract ?? false;
        _model.AutoDetectClipboardLinks = settings.AutoDetectClipboardLinks ?? true;
        _model.MegaEmail = settings.MegaEmail ?? string.Empty;
        _model.RememberMegaLogin = settings.RememberMegaLogin ?? false;
        _currentMegaSession = _model.RememberMegaLogin
            ? TryUnprotectMegaSession(settings.ProtectedMegaSession)
            : null;

        if (_currentMegaSession is not null)
        {
            _model.MegaEmail = _currentMegaSession.Email;
            _model.MegaAccountStatusText = $"MEGA 로그인 유지: {_currentMegaSession.Email}";
            _megaTransfers.ConfigureAccountSession(_currentMegaSession);
        }
        else
        {
            _model.MegaAccountStatusText = "MEGA 익명 모드";
        }

        _itemsView = CollectionViewSource.GetDefaultView(_model.Items);
        _itemsView.Filter = FilterDownloads;
        _model.Items.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(new Action(RefreshQueueViewState));

        DataContext = _model;
        _model.StatusText = "시작할 준비가 되었습니다.";
        RestoreSavedQueue();
        RefreshQueueViewState();

        SourceInitialized += MainWindow_SourceInitialized;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(_windowHandle);
        _windowSource?.AddHook(HwndHandler);
        AddClipboardFormatListener(_windowHandle);
    }

    private IntPtr HwndHandler(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            OnClipboardChanged();
        }
        return IntPtr.Zero;
    }

    private string _lastClipboardText = string.Empty;

    private async void OnClipboardChanged()
    {
        if (!_model.AutoDetectClipboardLinks)
        {
            return;
        }

        await TryAddClipboardLinksAsync(showParseFailure: true);
    }

    private async Task TryAddClipboardLinksAsync(bool showParseFailure)
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsText()) return;

            var text = System.Windows.Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text) || text == _lastClipboardText) return;

            _lastClipboardText = text;
            var links = MegaUrlParser.ParseMany(text);

            if (links.Count > 0)
            {
                ShowStatus($"클립보드에서 MEGA 링크 {links.Count}개 감지됨.");
                await AddLinksAsync(links);
            }
            else if (showParseFailure && text.Contains("mega", StringComparison.OrdinalIgnoreCase))
            {
                ShowStatus("클립보드에서 MEGA 링크를 찾지 못했습니다.");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Clipboard error: {ex.Message}");
        }
    }

    private async Task AddLinksAsync(IReadOnlyList<MegaLink> links)
    {
        var targetDirectory = _model.DownloadDirectory;
        var added = 0;
        var skippedUnreadableNodes = 0;
        var skippedDuplicates = 0;

        foreach (var link in links)
        {
            if (link.Kind == MegaLinkKind.Folder)
            {
                try
                {
                    _model.StatusText = $"폴더 확장 중: {link.NodeId}";
                    using var expansionCancellation = new CancellationTokenSource(FolderExpansionTimeout);
                    var expansion = await _megaTransfers.ExpandFolderWithReportAsync(link, expansionCancellation.Token);
                    if (expansion.Files.Count == 0)
                    {
                        ShowStatus($"폴더에서 다운로드 가능한 파일을 찾지 못했습니다: {link.NodeId}");
                        continue;
                    }

                    var folderTargetDirectory = CreateFolderTargetDirectory(targetDirectory, expansion.FolderName);
                    skippedUnreadableNodes += expansion.SkippedUnreadableNodes;
                    foreach (var file in expansion.Files)
                    {
                        if (_queue.ContainsEquivalent(file, folderTargetDirectory))
                        {
                            skippedDuplicates++;
                            continue;
                        }

                        var item = _queue.Add(file, folderTargetDirectory);
                        AddDownloadItemModel(item);
                        added++;
                    }
                }
                catch (OperationCanceledException)
                {
                    ShowStatus($"폴더 확장 시간 초과: {link.NodeId}");
                }
                catch (Exception ex)
                {
                    ShowStatus($"폴더 확장 실패: {ex.Message}");
                }
            }
            else
            {
                if (_queue.ContainsEquivalent(link, targetDirectory))
                {
                    skippedDuplicates++;
                    continue;
                }

                var item = _queue.Add(link, targetDirectory);
                AddDownloadItemModel(item);
                added++;
            }
        }

        if (added > 0 || skippedUnreadableNodes > 0 || skippedDuplicates > 0)
        {
            _model.RefreshSummary();
            ShowStatus(FormatAddStatus(added, skippedUnreadableNodes, skippedDuplicates));

            if (_model.AutoStartAfterAdd && !_isRunning)
            {
                await StartQueueAsync();
            }

            SaveQueue();
        }
    }

    private void AddDownloadItemModel(DownloadItem item)
    {
        var model = DownloadItemModel.From(item, item);
        model.PropertyChanged += DownloadItemModel_PropertyChanged;
        _model.Items.Add(model);
    }

    private void RemoveDownloadItemModel(DownloadItemModel model)
    {
        model.PropertyChanged -= DownloadItemModel_PropertyChanged;
        _model.Items.Remove(model);
    }

    private void DownloadItemModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadItemModel.State)
            or nameof(DownloadItemModel.StateText)
            or nameof(DownloadItemModel.Name))
        {
            RefreshQueueFilter();
        }
    }

    private bool FilterDownloads(object obj)
    {
        if (obj is not DownloadItemModel item) return false;
        if (!MatchesQueueSearch(item))
        {
            return false;
        }

        if (SidebarList == null) return true;

        var selected = (ListBoxItem)SidebarList.SelectedItem;
        if (selected == null || selected == NavAll) return true;

        if (selected == NavDownloading)
            return item.State is DownloadState.Downloading or DownloadState.Resolving;
        if (selected == NavCompleted)
            return item.State is DownloadState.Completed or DownloadState.Skipped;
        if (selected == NavErrors)
            return item.State is DownloadState.Failed or DownloadState.BandwidthLimited;

        return true;
    }

    private bool MatchesQueueSearch(DownloadItemModel item)
    {
        var query = _model.QueueSearchText;
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        return ContainsSearchText(item.Name, query)
            || ContainsSearchText(item.StateText, query)
            || ContainsSearchText(item.SizeText, query)
            || ContainsSearchText(item.TargetDirectory, query)
            || ContainsSearchText(item.LocalPathText, query)
            || ContainsSearchText(item.OriginalUrl, query)
            || ContainsSearchText(item.NodeId, query)
            || ContainsSearchText(item.KeyPreview, query);
    }

    private static bool ContainsSearchText(string? value, string query)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshQueueFilter()
    {
        if (_itemsView is null)
        {
            return;
        }

        _itemsView.Refresh();
        RefreshQueueViewState();
    }

    private void RefreshQueueViewState()
    {
        if (_itemsView is null || QueueEmptyPanel is null)
        {
            return;
        }

        var hasVisibleItems = _itemsView.Cast<object>().Any();
        QueueEmptyPanel.Visibility = hasVisibleItems ? Visibility.Collapsed : Visibility.Visible;
        if (hasVisibleItems)
        {
            return;
        }

        var hasSearch = !string.IsNullOrWhiteSpace(_model.QueueSearchText);
        ClearEmptySearchButton.Visibility = hasSearch ? Visibility.Visible : Visibility.Collapsed;

        if (hasSearch)
        {
            QueueEmptyTitle.Text = "검색 결과가 없습니다";
            QueueEmptyDescription.Text = "검색어를 지우거나 다른 조건으로 다시 찾아보세요.";
        }
        else if (_model.Items.Count > 0)
        {
            QueueEmptyTitle.Text = "이 보기에는 항목이 없습니다";
            QueueEmptyDescription.Text = "왼쪽에서 다른 범주를 선택하거나 새 MEGA 링크를 추가하세요.";
        }
        else
        {
            QueueEmptyTitle.Text = "다운로드할 링크를 추가하세요";
            QueueEmptyDescription.Text = "MEGA 링크를 붙여넣거나 창 위로 끌어오면 대기열에 바로 추가됩니다.";
        }
    }

    private void Sidebar_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)   
    {
        if (SidebarList?.SelectedItem is not ListBoxItem selected) return;

        if (SettingsSidebarList != null && SettingsSidebarList.SelectedItem != null)
        {
            SettingsSidebarList.SelectedItem = null;
        }

        if (SettingsView != null) SettingsView.Visibility = Visibility.Collapsed;
        if (QueueView != null) QueueView.Visibility = Visibility.Visible;

        if (CurrentCategoryTitle != null && selected.Content is StackPanel panel && panel.Children.Count > 1 && panel.Children[1] is TextBlock text)
        {
            CurrentCategoryTitle.Text = text.Text;
        }

        RefreshQueueFilter();
    }

    private void SettingsNav_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SettingsSidebarList?.SelectedItem is ListBoxItem selected)
        {
            if (SidebarList != null) SidebarList.SelectedItem = null;

            if (QueueView != null) QueueView.Visibility = Visibility.Collapsed;
            if (SettingsView != null) SettingsView.Visibility = Visibility.Visible;
            if (CurrentCategoryTitle != null) CurrentCategoryTitle.Text = "설정";
        }
    }

    private void MainWindow_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        if (e.Key == WpfKey.Escape && AddLinksOverlay.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            HideAddLinks_Click(sender, e);
            return;
        }

        if (e.Key == WpfKey.F && WpfKeyboard.Modifiers.HasFlag(WpfModifierKeys.Control))
        {
            e.Handled = true;
            FocusQueueSearch();
            return;
        }

        if (e.Key == WpfKey.L && WpfKeyboard.Modifiers.HasFlag(WpfModifierKeys.Control))
        {
            e.Handled = true;
            ShowAddLinks_Click(sender, e);
            return;
        }

        if (IsTextInputFocused())
        {
            return;
        }

        if (e.Key == WpfKey.Delete)
        {
            e.Handled = true;
            RemoveSelected_Click(sender, e);
            return;
        }

        if (e.Key == WpfKey.F5)
        {
            e.Handled = true;
            RetryErrors_Click(sender, e);
        }
    }

    private void MainWindow_DragOver(object sender, WpfDragEventArgs e)
    {
        var canDrop = HasPotentialMegaLinkDrop(e.Data);
        e.Effects = canDrop
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        SetDropHintVisible(canDrop);
        e.Handled = true;
    }

    private void MainWindow_DragLeave(object sender, WpfDragEventArgs e)
    {
        SetDropHintVisible(false);
        e.Handled = true;
    }

    private async void MainWindow_Drop(object sender, WpfDragEventArgs e)
    {
        e.Handled = true;
        SetDropHintVisible(false);
        if (!TryReadDroppedLinkText(e.Data, out var droppedText))
        {
            ShowStatus("드롭한 항목에서 읽을 수 있는 MEGA 링크를 찾지 못했습니다.");
            return;
        }

        await AddLinksFromTextAsync(droppedText, fallbackToClipboard: false);
    }

    private void SetDropHintVisible(bool isVisible)
    {
        var visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        if (DropHintOverlay.Visibility != visibility)
        {
            DropHintOverlay.Visibility = visibility;
        }
    }

    private void ShowAddLinks_Click(object sender, RoutedEventArgs e)
    {
        AddLinksOverlay.Visibility = Visibility.Visible;
        PrefillLinkInputFromClipboard();
        LinkInput.Focus();
        LinkInput.CaretIndex = LinkInput.Text.Length;
    }

    private void HideAddLinks_Click(object sender, RoutedEventArgs e)
    {
        AddLinksOverlay.Visibility = Visibility.Collapsed;
        LinkInput.Clear();
    }

    private async void AddLinks_Click(object sender, RoutedEventArgs e)
    {
        if (await AddLinksFromTextAsync(LinkInput.Text, fallbackToClipboard: true))
        {
            HideAddLinks_Click(null!, null!);
        }
    }

    private async void LinkInput_KeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == WpfKey.Escape)
        {
            e.Handled = true;
            HideAddLinks_Click(sender, e);
            return;
        }

        if (e.Key == WpfKey.Enter && WpfKeyboard.Modifiers.HasFlag(WpfModifierKeys.Control))
        {
            e.Handled = true;
            if (await AddLinksFromTextAsync(LinkInput.Text, fallbackToClipboard: true))
            {
                HideAddLinks_Click(sender, e);
            }
        }
    }

    private async Task<bool> AddLinksFromTextAsync(string? text, bool fallbackToClipboard)
    {
        var inputText = text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(inputText) && fallbackToClipboard && System.Windows.Clipboard.ContainsText())
        {
            inputText = System.Windows.Clipboard.GetText();
            LinkInput.Text = inputText;
        }

        var links = MegaUrlParser.ParseMany(inputText);
        if (links.Count == 0)
        {
            ShowStatus(inputText.Contains("mega", StringComparison.OrdinalIgnoreCase)
                ? "MEGA 링크처럼 보이지만 형식을 읽지 못했습니다. 링크 전체를 그대로 붙여넣어 주세요."
                : "유효한 MEGA 링크를 찾을 수 없습니다.");
            return false;
        }

        var targetDirectory = NormalizeDownloadDirectory(_model.DownloadDirectory);
        _model.DownloadDirectory = targetDirectory;
        SaveSettings();

        ShowStatus($"MEGA 링크 {links.Count}개 인식됨. 추가 중...");
        await AddLinksAsync(links);
        return true;
    }

    private static bool HasPotentialMegaLinkDrop(System.Windows.IDataObject data)
    {
        if (data.GetDataPresent(System.Windows.DataFormats.UnicodeText)
            || data.GetDataPresent(System.Windows.DataFormats.Text))
        {
            return true;
        }

        if (!data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            || data.GetData(System.Windows.DataFormats.FileDrop) is not string[] filePaths)
        {
            return false;
        }

        return filePaths.Any(IsSupportedDroppedTextFile);
    }

    private static bool TryReadDroppedLinkText(System.Windows.IDataObject data, out string text)
    {
        var builder = new StringBuilder();
        if (TryGetDroppedText(data, out var droppedText))
        {
            builder.AppendLine(droppedText);
        }

        if (data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            && data.GetData(System.Windows.DataFormats.FileDrop) is string[] filePaths)
        {
            foreach (var filePath in filePaths.Take(20))
            {
                if (!IsSupportedDroppedTextFile(filePath))
                {
                    continue;
                }

                try
                {
                    var info = new FileInfo(filePath);
                    if (info.Length > MaxDroppedTextFileBytes)
                    {
                        continue;
                    }

                    builder.AppendLine(File.ReadAllText(filePath));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    Debug.WriteLine($"Dropped file read failed: {ex.Message}");
                }
            }
        }

        text = builder.ToString();
        return MegaUrlParser.ParseMany(text).Count > 0;
    }

    private static bool TryGetDroppedText(System.Windows.IDataObject data, out string text)
    {
        text = string.Empty;
        if (data.GetDataPresent(System.Windows.DataFormats.UnicodeText)
            && data.GetData(System.Windows.DataFormats.UnicodeText) is string unicodeText)
        {
            text = unicodeText;
            return true;
        }

        if (data.GetDataPresent(System.Windows.DataFormats.Text)
            && data.GetData(System.Windows.DataFormats.Text) is string plainText)
        {
            text = plainText;
            return true;
        }

        return false;
    }

    private static bool IsSupportedDroppedTextFile(string filePath)
    {
        return File.Exists(filePath)
            && SupportedDroppedTextExtensions.Contains(Path.GetExtension(filePath));
    }

    private void ShowStatus(string message)
    {
        _model.StatusText = message;
        StatusToast.Visibility = Visibility.Visible;
        Task.Delay(3000).ContinueWith(_ => Dispatcher.Invoke(() =>
        {
            if (_model.StatusText == message)
            {
                StatusToast.Visibility = Visibility.Collapsed;
            }
        }));
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void FilterButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshQueueFilter();
    }

    private void QueueSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshQueueFilter();
    }

    private void ClearQueueSearch_Click(object sender, RoutedEventArgs e)
    {
        _model.QueueSearchText = string.Empty;
        FocusQueueSearch();
        RefreshQueueFilter();
    }

    private void FocusQueueSearch()
    {
        if (QueueView.Visibility != Visibility.Visible)
        {
            SidebarList.SelectedItem = NavAll;
        }

        QueueSearchBox.Focus();
        QueueSearchBox.SelectAll();
    }

    private void PrefillLinkInputFromClipboard()
    {
        if (!string.IsNullOrWhiteSpace(LinkInput.Text))
        {
            return;
        }

        try
        {
            if (!System.Windows.Clipboard.ContainsText())
            {
                return;
            }

            var clipboardText = System.Windows.Clipboard.GetText();
            if (MegaUrlParser.ParseMany(clipboardText).Count == 0)
            {
                return;
            }

            LinkInput.Text = clipboardText;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Clipboard prefill failed: {ex.Message}");
        }
    }

    private static bool IsTextInputFocused()
    {
        var focused = WpfKeyboard.FocusedElement;
        return focused is System.Windows.Controls.TextBox
            or PasswordBox
            or System.Windows.Controls.RichTextBox
            || focused is TextElement;
    }

    private void BrowseDownloadDirectory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new WinForms.FolderBrowserDialog
        {
            SelectedPath = _model.DownloadDirectory,
            Description = "다운로드 폴더를 선택하세요"
        };

        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            _model.DownloadDirectory = NormalizeDownloadDirectory(dialog.SelectedPath);
            SaveSettings();
        }
    }

    private void StreamItem_Click(object sender, RoutedEventArgs e)
    {
        var selected = _model.SelectedItem;
        if (selected == null) return;

        var playerPath = _model.MediaPlayerPath;
        if (string.IsNullOrWhiteSpace(playerPath))
        {
            playerPath = FindDefaultMediaPlayer();
        }

        if (string.IsNullOrWhiteSpace(playerPath) || !File.Exists(playerPath))
        {
            ShowStatus("미디어 플레이어를 찾을 수 없습니다. 설정에서 경로를 지정해주세요.");
            return;
        }

        try
        {
            ShowStatus($"{selected.Name} 스트리밍을 준비 중입니다...");
        }
        catch (Exception ex)
        {
            ShowStatus($"스트리밍 시작 실패: {ex.Message}");
        }
    }

    private string? FindDefaultMediaPlayer()
    {
        string[] commonPaths = [
            @"C:\Program Files\VideoLAN\VLC\vlc.exe",
            @"C:\Program Files (x86)\VideoLAN\VLC\vlc.exe",
            @"C:\Program Files\DAUM\PotPlayer\PotPlayer64.exe",
            @"C:\Program Files (x86)\DAUM\PotPlayer\PotPlayer.exe"
        ];

        foreach (var path in commonPaths)
        {
            if (File.Exists(path)) return path;
        }

        return null;
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = _model.SelectedItem;
        if (selected != null)
        {
            RemoveDownloadItemModel(selected);
            _queue.Remove(selected.Source);
            _model.RefreshSummary();
            SaveQueue();
        }
    }

    private void OpenSelectedFile_Click(object sender, RoutedEventArgs e)
    {
        var path = GetSelectedExistingLocalPath();
        if (path is null)
        {
            ShowStatus("아직 열 수 있는 로컬 파일이 없습니다.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowStatus($"파일 열기 실패: {ex.Message}");
        }
    }

    private void RevealSelectedFile_Click(object sender, RoutedEventArgs e)
    {
        var selected = _model.SelectedItem;
        if (selected is null)
        {
            ShowStatus("항목을 먼저 선택해주세요.");
            return;
        }

        try
        {
            var localPath = GetSelectedExistingLocalPath();
            if (localPath is not null)
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{localPath}\"")
                {
                    UseShellExecute = true
                });
                return;
            }

            var targetDirectory = NormalizeDownloadDirectory(selected.Source.TargetDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", targetDirectory)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowStatus($"폴더 열기 실패: {ex.Message}");
        }
    }

    private void CopySelectedLink_Click(object sender, RoutedEventArgs e)
    {
        var selected = _model.SelectedItem;
        if (selected is null)
        {
            ShowStatus("항목을 먼저 선택해주세요.");
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(selected.OriginalUrl);
            ShowStatus("원본 링크를 복사했습니다.");
        }
        catch (Exception ex)
        {
            ShowStatus($"링크 복사 실패: {ex.Message}");
        }
    }

    private string? GetSelectedExistingLocalPath()
    {
        var selected = _model.SelectedItem;
        if (selected is null)
        {
            return null;
        }

        var item = selected.Source;
        if (!string.IsNullOrWhiteSpace(item.LocalPath) && File.Exists(item.LocalPath))
        {
            return item.LocalPath;
        }

        var relativePath = item.RelativePath ?? item.Name;
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        try
        {
            var fallbackPath = BuildTargetPath(item.TargetDirectory, relativePath);
            if (File.Exists(fallbackPath))
            {
                item.LocalPath = fallbackPath;
                selected.SyncFromSource();
                SaveQueue();
                return fallbackPath;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Local path lookup failed: {ex.Message}");
        }

        return null;
    }

    private void BrowseMediaPlayer_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "실행 파일 (*.exe)|*.exe|모든 파일 (*.*)|*.*",
            Title = "미디어 플레이어 실행 파일을 선택하세요",
            InitialDirectory = !string.IsNullOrWhiteSpace(_model.MediaPlayerPath)
                ? Path.GetDirectoryName(_model.MediaPlayerPath)
                : @"C:\Program Files"
        };

        if (dialog.ShowDialog() == true)
        {
            _model.MediaPlayerPath = dialog.FileName;
            SaveSettings();
        }
    }

    private void OpenDownloadDirectory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var targetDirectory = NormalizeDownloadDirectory(_model.DownloadDirectory);
            _model.DownloadDirectory = targetDirectory;
            SaveSettings();
            Process.Start(new ProcessStartInfo("explorer.exe", targetDirectory)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _model.StatusText = ex.Message;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_isRunning)
        {
            if (!_shutdownRequested && !ConfirmCloseWhileDownloadsRunning())
            {
                e.Cancel = true;
                return;
            }

            e.Cancel = true;
            RequestShutdownAfterTransfersStop();
            return;
        }

        if (_windowHandle != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(_windowHandle);
        }

        _windowSource?.RemoveHook(HwndHandler);
        SaveQueue();
        _megaTransfers.Dispose();
        SaveSettings();
        base.OnClosing(e);
    }

    private bool ConfirmCloseWhileDownloadsRunning()
    {
        var activeCount = _model.Items.Count(item => item.State is DownloadState.Resolving or DownloadState.Downloading);
        var message = activeCount == 1
            ? "다운로드 1개가 진행 중입니다. 종료하면 현재 작업은 일시 정지되고 다음 실행 때 복원됩니다. 종료할까요?"
            : $"다운로드 {activeCount}개가 진행 중입니다. 종료하면 현재 작업은 일시 정지되고 다음 실행 때 복원됩니다. 종료할까요?";

        return System.Windows.MessageBox.Show(
            this,
            message,
            "다운로드 진행 중",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private async void RequestShutdownAfterTransfersStop()
    {
        if (_shutdownRequested)
        {
            return;
        }

        _shutdownRequested = true;
        MarkActiveDownloadsPausedForShutdown();
        SaveQueue();
        _downloadCancellation?.Cancel();
        _model.StatusText = "종료 준비 중: 다운로드를 일시 정지하고 있습니다...";

        while (_isRunning)
        {
            await Task.Delay(100);
        }

        Close();
    }

    private void MarkActiveDownloadsPausedForShutdown()
    {
        foreach (var model in _model.Items.Where(item => item.State is DownloadState.Resolving or DownloadState.Downloading))
        {
            var item = model.Source;
            item.State = DownloadState.Paused;
            item.ErrorMessage = "Paused because the app was closed.";
            model.SyncFromSource();
        }

        _model.RefreshSummary();
    }

    private void ConcurrentDownloads_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        SaveSettings();
    }

    private void SettingsChanged(object sender, RoutedEventArgs e)
    {
        SaveSettings();
    }

    private void MegaSettingsChanged(object sender, RoutedEventArgs e)
    {
        SaveSettings();
    }

    private void MegaAccountInput_KeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key != WpfKey.Enter)
        {
            return;
        }

        e.Handled = true;
        MegaLogin_Click(sender, e);
    }

    private async void MegaLogin_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            ShowStatus("다운로드 중에는 MEGA 계정을 전환할 수 없습니다.");
            return;
        }

        var email = _model.MegaEmail.Trim();
        var password = MegaPasswordInput.Password;
        var mfaKey = MegaMfaInput.Text;
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            ShowStatus("MEGA 이메일과 비밀번호를 입력해주세요.");
            return;
        }

        SetMegaAccountButtonsEnabled(false);
        _model.MegaAccountStatusText = "MEGA 로그인 중...";

        try
        {
            var session = await _megaTransfers.LoginWithAccountAsync(
                email,
                password,
                mfaKey,
                CancellationToken.None);

            _currentMegaSession = session;
            _model.MegaEmail = session.Email;
            _model.MegaAccountStatusText = $"MEGA 로그인됨: {session.Email}";
            MegaPasswordInput.Clear();
            MegaMfaInput.Clear();
            SaveSettings();
            ShowStatus("MEGA 계정 로그인 완료.");
        }
        catch (Exception ex)
        {
            _model.MegaAccountStatusText = "MEGA 로그인 실패";
            ShowStatus($"MEGA 로그인 실패: {FormatMegaAccountError(ex)}");
        }
        finally
        {
            SetMegaAccountButtonsEnabled(true);
        }
    }

    private void MegaLogout_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            ShowStatus("다운로드 중에는 MEGA 계정을 전환할 수 없습니다.");
            return;
        }

        _currentMegaSession = null;
        _megaTransfers.ClearAccountSession();
        _model.RememberMegaLogin = false;
        _model.MegaAccountStatusText = "MEGA 익명 모드";
        MegaPasswordInput.Clear();
        MegaMfaInput.Clear();
        SaveSettings();
        ShowStatus("MEGA 계정 로그아웃 완료.");
    }

    private void SetMegaAccountButtonsEnabled(bool isEnabled)
    {
        MegaLoginButton.IsEnabled = isEnabled;
        MegaLogoutButton.IsEnabled = isEnabled;
    }

    private void ClearCompleted_Click(object sender, RoutedEventArgs e)
    {
        _queue.ClearCompleted();
        for (var i = _model.Items.Count - 1; i >= 0; i--)
        {
            if (_model.Items[i].State is DownloadState.Completed or DownloadState.Canceled or DownloadState.Skipped)
            {
                _model.Items[i].PropertyChanged -= DownloadItemModel_PropertyChanged;
                _model.Items.RemoveAt(i);
            }
        }

        _model.RefreshSummary();
        SaveQueue();
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            ShowStatus("다운로드 중에는 대기열을 비울 수 없습니다. 먼저 일시 정지해주세요.");
            return;
        }

        var itemCount = _model.Items.Count;
        if (itemCount == 0)
        {
            ShowStatus("비울 대기열이 없습니다.");
            return;
        }

        var result = System.Windows.MessageBox.Show(
            this,
            $"대기열의 {itemCount:N0}개 항목을 모두 삭제할까요?\n\n다운로드된 파일은 삭제되지 않습니다.",
            "대기열 비우기",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        foreach (var item in _model.Items)
        {
            item.PropertyChanged -= DownloadItemModel_PropertyChanged;
        }

        _model.SelectedItem = null;
        _model.Items.Clear();
        _queue.Clear();
        _model.RefreshSummary();
        RefreshQueueFilter();
        SaveQueue();
        ShowStatus("대기열을 비웠습니다.");
    }

    private async void StartDownloads_Click(object sender, RoutedEventArgs e)
    {
        await StartQueueAsync();
    }

    private async Task StartQueueAsync()
    {
        if (_isRunning)
        {
            return;
        }

        var pending = _model.Items
            .Where(item => item.Source.State is DownloadState.Queued or DownloadState.Failed or DownloadState.BandwidthLimited or DownloadState.Canceled or DownloadState.Paused)
            .ToList();

        if (pending.Count == 0)
        {
            _model.StatusText = "No queued downloads.";
            return;
        }

        foreach (var model in pending.Where(IsRetryable))
        {
            ResetForRetry(model);
        }

        await RunTransferBatchAsync(
            pending,
            pending.Count == 1 ? "Starting 1 download." : $"Starting {pending.Count} downloads.");
    }

    private async void RetryErrors_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            return;
        }

        var retryItems = _model.Items
            .Where(IsRetryable)
            .ToList();

        if (retryItems.Count == 0)
        {
            _model.StatusText = "No failed downloads to retry.";
            return;
        }

        foreach (var item in retryItems)
        {
            ResetForRetry(item);
        }

        await RunTransferBatchAsync(
            retryItems,
            retryItems.Count == 1 ? "Retrying 1 download." : $"Retrying {retryItems.Count} downloads.");        
    }

    private async void RetrySelected_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            return;
        }

        var selected = _model.SelectedItem;
        if (selected is null)
        {
            _model.StatusText = "Select a failed download first.";
            return;
        }

        if (!IsRetryable(selected))
        {
            _model.StatusText = "Selected item is not failed.";
            return;
        }

        ResetForRetry(selected);
        await RunTransferBatchAsync([selected], $"Retrying {selected.Name}.");
    }

    private async Task RunTransferBatchAsync(IReadOnlyList<DownloadItemModel> pending, string startStatus)      
    {
        _model.StatusText = startStatus;
        _downloadCancellation = new CancellationTokenSource();
        _bandwidthLimitReached = false;
        SetRunningState(true);

        try
        {
            using var semaphore = new SemaphoreSlim(_model.ConcurrentDownloads);
            var tasks = pending.Select(async model =>
            {
                await semaphore.WaitAsync(_downloadCancellation.Token);
                try
                {
                    await RunDownloadAsync(model, _downloadCancellation.Token);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
            ShowStatus(_bandwidthLimitReached
                ? _model.StopOnBandwidthLimit
                    ? "MEGA 대역폭 제한에 도달해 대기열을 일시 정지했습니다."
                    : "일부 다운로드가 MEGA 대역폭 제한에 걸렸습니다."
                : "대기열 완료.");
        }
        catch (OperationCanceledException)
        {
            ShowStatus(_bandwidthLimitReached
                ? "MEGA 대역폭 제한에 도달해 대기열을 일시 정지했습니다."
                : "다운로드를 일시 정지했습니다.");
        }
        finally
        {
            SetRunningState(false);
            _downloadCancellation.Dispose();
            _downloadCancellation = null;
            _model.RefreshSummary();
            SaveQueue();
        }
    }

    private void StopDownloads_Click(object sender, RoutedEventArgs e)
    {
        _downloadCancellation?.Cancel();
        _model.StatusText = "Pausing downloads...";
    }

    private async Task RunDownloadAsync(DownloadItemModel model, CancellationToken cancellationToken)
    {
        var item = model.Source;
        if (item.FolderFile is not null)
        {
            await RunFolderFileDownloadAsync(model, cancellationToken);
            return;
        }

        try
        {
            item.ErrorMessage = null;
            model.ResetTransferStats();
            item.State = DownloadState.Resolving;
            model.SyncFromSource();
            _model.StatusText = $"Resolving {model.Name}.";

            var node = await _megaTransfers.GetFileNodeAsync(item.Link, cancellationToken);
            item.Name = node.Name;
            item.SizeBytes = node.Size;
            
            var expectedOutputPath = BuildTargetPath(item.TargetDirectory, node.Name);
            if (TryMarkSkippedIfExisting(item, model, expectedOutputPath, node.Size))
            {
                _model.StatusText = $"Skipped {node.Name}; file already exists.";
                _model.RefreshSummary();
                return;
            }

            var outputPath = item.LocalPath;
            if (string.IsNullOrEmpty(outputPath) || !File.Exists(outputPath))
            {
                outputPath = CreateAvailablePath(item.TargetDirectory, node.Name);
                item.LocalPath = outputPath;
            }

            item.State = DownloadState.Downloading;
            model.SyncFromSource();
            _model.StatusText = $"Downloading {node.Name}.";

            var progress = new Progress<DownloadProgress>(p =>
            {
                item.DownloadedBytes = p.DownloadedBytes;
                item.SizeBytes = p.TotalBytes;
                model.SyncFromSource();
                _model.RefreshSummary();
            });

            await _megaTransfers.DownloadFileLinkAsync(item.Link, node, outputPath, progress, cancellationToken);

            await HandleDownloadCompletionAsync(model, outputPath, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (_bandwidthLimitReached)
            {
                item.State = DownloadState.Queued;
                item.ErrorMessage = "Waiting until MEGA bandwidth quota resets.";
            }
            else
            {
                item.State = DownloadState.Paused;
                item.ErrorMessage = "Paused by user.";
            }

            model.SyncFromSource();
            throw;
        }
        catch (Exception ex)
        {
            if (IsBandwidthLimitExceeded(ex))
            {
                MarkBandwidthLimited(item, model);
                return;
            }

            item.State = DownloadState.Failed;
            item.ErrorMessage = ex.Message;
            model.SyncFromSource();
            _model.StatusText = ex.Message;
        }
    }

    private async Task RunFolderFileDownloadAsync(DownloadItemModel model, CancellationToken cancellationToken) 
    {
        var item = model.Source;
        var folderFile = item.FolderFile ?? throw new InvalidOperationException("Missing folder file data.");   

        try
        {
            item.ErrorMessage = null;
            model.ResetTransferStats();
            
            var expectedOutputPath = BuildTargetPath(item.TargetDirectory, folderFile.RelativePath);
            if (TryMarkSkippedIfExisting(item, model, expectedOutputPath, folderFile.SizeBytes))
            {
                _model.StatusText = $"Skipped {folderFile.RelativePath}; file already exists.";
                _model.RefreshSummary();
                return;
            }

            var outputPath = item.LocalPath;
            if (string.IsNullOrEmpty(outputPath) || !File.Exists(outputPath))
            {
                outputPath = CreateAvailablePath(item.TargetDirectory, folderFile.RelativePath);
                item.LocalPath = outputPath;
            }

            item.State = DownloadState.Downloading;
            model.SyncFromSource();
            _model.StatusText = $"Downloading {folderFile.RelativePath}.";

            var progress = new Progress<DownloadProgress>(p =>
            {
                item.DownloadedBytes = p.DownloadedBytes;
                item.SizeBytes = p.TotalBytes;
                model.SyncFromSource();
                _model.RefreshSummary();
            });

            await _megaTransfers.DownloadFolderFileAsync(folderFile, outputPath, progress, cancellationToken);   

            await HandleDownloadCompletionAsync(model, outputPath, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (_bandwidthLimitReached)
            {
                item.State = DownloadState.Queued;
                item.ErrorMessage = "Waiting until MEGA bandwidth quota resets.";
            }
            else
            {
                item.State = DownloadState.Paused;
                item.ErrorMessage = "Paused by user.";
            }

            model.SyncFromSource();
            throw;
        }
        catch (Exception ex)
        {
            if (IsBandwidthLimitExceeded(ex))
            {
                MarkBandwidthLimited(item, model);
                return;
            }

            item.State = DownloadState.Failed;
            item.ErrorMessage = ex.Message;
            model.SyncFromSource();
            _model.StatusText = ex.Message;
        }
    }

    private static string GetDefaultDownloadDirectory()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(userProfile, "Downloads");
        return Directory.Exists(downloads) ? downloads : userProfile;
    }

    private static AppSettings LoadSettings()
    {
        try
        {
            foreach (var settingsPath in new[] { GetSettingsPath(), GetPreviousSettingsPath(), GetLegacySettingsPath() })
            {
                if (File.Exists(settingsPath))
                {
                    var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsPath));
                    if (settings is not null)
                    {
                        return settings;
                    }
                }
            }
        }
        catch
        {
        }

        return new AppSettings(GetDefaultDownloadDirectory(), DefaultConcurrentDownloads, false, true);
    }

    private void SaveSettings()
    {
        SaveSettings(
            _model.DownloadDirectory,
            _model.ConcurrentDownloads,
            _model.AutoStartAfterAdd,
            _model.StopOnBandwidthLimit,
            _model.MediaPlayerPath,
            _model.AutoExtract,
            _model.AutoDetectClipboardLinks,
            _model.MegaEmail,
            _model.RememberMegaLogin,
            _currentMegaSession);
    }

    private static void SaveSettings(
        string? directory,
        int concurrentDownloads,
        bool autoStartAfterAdd,
        bool stopOnBandwidthLimit,
        string? mediaPlayerPath,
        bool autoExtract,
        bool autoDetectClipboardLinks,
        string? megaEmail,
        bool rememberMegaLogin,
        MegaAccountSession? megaSession)
    {
        try
        {
            var settingsPath = GetSettingsPath();
            var settingsDirectory = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrWhiteSpace(settingsDirectory))
            {
                Directory.CreateDirectory(settingsDirectory);
            }

            var settings = new AppSettings(
                directory,
                NormalizeConcurrentDownloads(concurrentDownloads),
                autoStartAfterAdd,
                stopOnBandwidthLimit,
                mediaPlayerPath,
                autoExtract,
                autoDetectClipboardLinks,
                megaEmail,
                rememberMegaLogin,
                rememberMegaLogin && megaSession is not null
                    ? ProtectMegaSession(megaSession)
                    : null);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });  
            File.WriteAllText(settingsPath, json);
        }
        catch
        {
        }
    }

    private void RestoreSavedQueue()
    {
        var queue = LoadSavedQueue();
        if (queue.Items.Count == 0)
        {
            return;
        }

        var restored = 0;
        foreach (var savedItem in queue.Items)
        {
            try
            {
                var item = RestoreDownloadItem(savedItem);
                if (item is null)
                {
                    continue;
                }

                AddDownloadItemModel(item);
                restored++;
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or IOException)
            {
                Debug.WriteLine($"Queue restore skipped item: {ex.Message}");
            }
        }

        if (restored > 0)
        {
            _model.RefreshSummary();
            _model.StatusText = $"{restored}개 미완료 항목을 복원했습니다.";
        }
    }

    private DownloadItem? RestoreDownloadItem(PersistedDownloadItem savedItem)
    {
        var targetDirectory = NormalizeDownloadDirectory(savedItem.TargetDirectory);
        DownloadItem item;
        if (savedItem.FolderFile is { } savedFolderFile)
        {
            var node = new MegaFileNodeSnapshot(
                savedFolderFile.NodeId,
                savedFolderFile.Name,
                savedFolderFile.SizeBytes,
                savedFolderFile.ParentId,
                Convert.FromBase64String(savedFolderFile.FullKeyBase64));
            var folderFile = new MegaFolderFile(savedItem.Link, node, savedFolderFile.RelativePath);
            if (_queue.ContainsEquivalent(folderFile, targetDirectory))
            {
                return null;
            }

            item = _queue.Add(folderFile, targetDirectory);
        }
        else
        {
            if (_queue.ContainsEquivalent(savedItem.Link, targetDirectory))
            {
                return null;
            }

            item = _queue.Add(savedItem.Link, targetDirectory);
        }

        item.Name = savedItem.Name ?? item.Name;
        item.LocalPath = savedItem.LocalPath;
        item.SizeBytes = savedItem.SizeBytes;
        item.DownloadedBytes = savedItem.DownloadedBytes;
        item.State = GetRestoredState(savedItem.State);
        item.ErrorMessage = GetRestoredError(savedItem.State, savedItem.ErrorMessage);
        return item;
    }

    private void SaveQueue()
    {
        try
        {
            var items = _queue.Items
                .Where(ShouldPersistQueueItem)
                .Select(ToPersistedDownloadItem)
                .OfType<PersistedDownloadItem>()
                .ToList();

            var queuePath = GetQueuePath();
            var queueDirectory = Path.GetDirectoryName(queuePath);
            if (!string.IsNullOrWhiteSpace(queueDirectory))
            {
                Directory.CreateDirectory(queueDirectory);
            }

            if (items.Count == 0)
            {
                if (File.Exists(queuePath))
                {
                    File.Delete(queuePath);
                }

                return;
            }

            var json = JsonSerializer.Serialize(
                new PersistedDownloadQueue(Version: 1, items),
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(queuePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            Debug.WriteLine($"Queue save failed: {ex.Message}");
        }
    }

    private static PersistedDownloadQueue LoadSavedQueue()
    {
        try
        {
            var queuePath = GetQueuePath();
            if (!File.Exists(queuePath))
            {
                return PersistedDownloadQueue.Empty;
            }

            return JsonSerializer.Deserialize<PersistedDownloadQueue>(File.ReadAllText(queuePath))
                ?? PersistedDownloadQueue.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"Queue load failed: {ex.Message}");
            return PersistedDownloadQueue.Empty;
        }
    }

    private static PersistedDownloadItem? ToPersistedDownloadItem(DownloadItem item)
    {
        var folderFile = item.FolderFile is null
            ? null
            : ToPersistedFolderFile(item.FolderFile);
        if (item.FolderFile is not null && folderFile is null)
        {
            return null;
        }

        return new PersistedDownloadItem(
            item.Link,
            item.TargetDirectory,
            item.LocalPath,
            string.IsNullOrWhiteSpace(item.Name) ? null : item.Name,
            item.State,
            item.SizeBytes,
            item.DownloadedBytes,
            item.ErrorMessage,
            folderFile);
    }

    private static PersistedMegaFolderFile? ToPersistedFolderFile(MegaFolderFile folderFile)
    {
        try
        {
            var node = MegaFileNodeSnapshot.From(folderFile.Node, folderFile.Name);
            return new PersistedMegaFolderFile(
                folderFile.RelativePath,
                node.Id,
                node.Name,
                node.Size,
                node.ParentId,
                Convert.ToBase64String(node.FullKey));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool ShouldPersistQueueItem(DownloadItem item)
    {
        return item.State is not (DownloadState.Completed or DownloadState.Skipped);
    }

    private static DownloadState GetRestoredState(DownloadState state)
    {
        return state is DownloadState.Downloading or DownloadState.Resolving
            ? DownloadState.Paused
            : state;
    }

    private static string? GetRestoredError(DownloadState savedState, string? savedError)
    {
        if (!string.IsNullOrWhiteSpace(savedError))
        {
            return savedError;
        }

        return savedState is DownloadState.Downloading or DownloadState.Resolving
            ? "Restored from previous session."
            : null;
    }

    private static MegaAccountSession? TryUnprotectMegaSession(string? protectedSession)
    {
        var payload = UnprotectSecret(protectedSession);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            var session = JsonSerializer.Deserialize<PersistedMegaSession>(payload);
            return session is null
                ? null
                : new MegaAccountSession(session.Email, session.SessionId, session.MasterKeyBase64);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string ProtectMegaSession(MegaAccountSession session)
    {
        var payload = JsonSerializer.Serialize(new PersistedMegaSession(
            session.Email,
            session.SessionId,
            session.MasterKeyBase64));
        return ProtectSecret(payload);
    }

    private static string ProtectSecret(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    private static string? UnprotectSecret(string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return null;
        }

        try
        {
            var protectedBytes = Convert.FromBase64String(protectedValue);
            var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string FormatMegaAccountError(Exception exception)
    {
        var message = exception.Message;
        if (message.Contains("TwoFactor", StringComparison.OrdinalIgnoreCase)
            || message.Contains("two-factor", StringComparison.OrdinalIgnoreCase))
        {
            return "2FA 코드가 필요하거나 올바르지 않습니다.";
        }

        if (message.Contains("BadSessionId", StringComparison.OrdinalIgnoreCase))
        {
            return "저장된 MEGA 로그인 세션이 만료됐습니다. 다시 로그인해주세요.";
        }

        return string.IsNullOrWhiteSpace(message)
            ? "알 수 없는 오류입니다."
            : message;
    }

    private static string GetSettingsPath()
    {
        return GetSettingsPath(AppDataDirectoryName);
    }

    private static string GetQueuePath()
    {
        var settingsDirectory = Path.GetDirectoryName(GetSettingsPath());
        return Path.Combine(settingsDirectory ?? ".", QueueFileName);
    }

    private static string GetPreviousSettingsPath()
    {
        return GetSettingsPath(PreviousAppDataDirectoryName);
    }

    private static string GetLegacySettingsPath()
    {
        return GetSettingsPath(LegacyAppDataDirectoryName);
    }

    private static string GetSettingsPath(string appDataDirectoryName)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, appDataDirectoryName, "settings.json");
    }

    private static string NormalizeDownloadDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return GetDefaultDownloadDirectory();
        }

        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        var fullPath = Path.GetFullPath(expanded);
        Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    private static int NormalizeConcurrentDownloads(int? value)
    {
        return Math.Clamp(value ?? DefaultConcurrentDownloads, MinConcurrentDownloads, MaxConcurrentDownloads); 
    }

    private static string CreateFolderTargetDirectory(string baseDirectory, string folderName)
    {
        var folderTargetDirectory = Path.Combine(baseDirectory, SanitizePathSegment(folderName));
        Directory.CreateDirectory(folderTargetDirectory);
        return folderTargetDirectory;
    }

    private static string FormatAddStatus(int added, int skippedUnreadableNodes, int skippedDuplicates)
    {
        var parts = new List<string>();
        if (added > 0)
        {
            parts.Add($"{added}개 항목 추가됨");
        }

        if (skippedDuplicates > 0)
        {
            parts.Add($"{skippedDuplicates}개 중복 항목 건너뜀");
        }

        if (skippedUnreadableNodes > 0)
        {
            parts.Add($"{skippedUnreadableNodes}개 항목은 이름을 읽지 못해 제외");
        }

        return parts.Count == 0
            ? "추가할 새 항목이 없습니다."
            : string.Join(" · ", parts);
    }

    private async Task HandleDownloadCompletionAsync(
        DownloadItemModel model,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var item = model.Source;
        item.LocalPath = outputPath;
        item.DownloadedBytes = item.SizeBytes ?? item.DownloadedBytes;
        item.State = DownloadState.Completed;
        model.SyncFromSource();
        _model.StatusText = $"완료됨: {model.Name}";

        if (_model.AutoExtract && ArchiveExtractor.IsSupported(outputPath))
        {
            try
            {
                var extractDir = Path.Combine(Path.GetDirectoryName(outputPath) ?? ".", Path.GetFileNameWithoutExtension(outputPath));
                Directory.CreateDirectory(extractDir);

                _model.StatusText = $"압축 해제 중: {model.Name}...";
                var progress = new Progress<string>(p => _model.StatusText = p);
                await ArchiveExtractor.ExtractAsync(outputPath, extractDir, progress, cancellationToken);
                _model.StatusText = $"압축 해제 완료: {model.Name}";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ShowStatus($"압축 해제 실패: {ex.Message}");
            }
        }
    }

    private void MarkBandwidthLimited(DownloadItem item, DownloadItemModel model)
    {
        _bandwidthLimitReached = true;
        item.State = DownloadState.BandwidthLimited;
        item.ErrorMessage = "MEGA bandwidth limit exceeded. Try again after the quota resets.";
        model.SyncFromSource();
        _model.StatusText = _model.StopOnBandwidthLimit
            ? "MEGA bandwidth limit reached. Queue paused."
            : "MEGA bandwidth limit reached for one item.";
        _model.RefreshSummary();
        if (_model.StopOnBandwidthLimit)
        {
            _downloadCancellation?.Cancel();
        }
    }

    private static bool IsRetryable(DownloadItemModel model)
    {
        return model.Source.State is DownloadState.Failed or DownloadState.BandwidthLimited or DownloadState.Canceled or DownloadState.Paused;
    }

    private static void ResetForRetry(DownloadItemModel model)
    {
        var item = model.Source;
        item.State = DownloadState.Queued;
        item.ErrorMessage = null;
        model.SyncFromSource();
    }

    private static bool IsBandwidthLimitExceeded(Exception exception)
    {
        if (exception is HttpRequestException { StatusCode: not null } httpException
            && (int)httpException.StatusCode.Value == 509)
        {
            return true;
        }

        if (exception is AggregateException aggregateException
            && aggregateException.InnerExceptions.Any(IsBandwidthLimitExceeded))
        {
            return true;
        }

        if (exception.InnerException is not null && IsBandwidthLimitExceeded(exception.InnerException))
        {
            return true;
        }

        return exception.Message.Contains("509", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("Bandwidth Limit Exceeded", StringComparison.OrdinalIgnoreCase)       
            || exception.Message.Contains("bandwidth limit", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryMarkSkippedIfExisting(
        DownloadItem item,
        DownloadItemModel model,
        string outputPath,
        long? expectedSizeBytes)
    {
        if (!File.Exists(outputPath))
        {
            return false;
        }

        var existingSizeBytes = new FileInfo(outputPath).Length;
        if (expectedSizeBytes.HasValue && existingSizeBytes != expectedSizeBytes.Value)
        {
            return false;
        }

        item.DownloadedBytes = expectedSizeBytes ?? existingSizeBytes;
        item.SizeBytes = expectedSizeBytes ?? existingSizeBytes;
        item.LocalPath = outputPath;
        item.State = DownloadState.Skipped;
        item.ErrorMessage = "Skipped because a matching file already exists.";
        model.SyncFromSource();
        return true;
    }

    private static string BuildTargetPath(string targetDirectory, string relativePath)
    {
        var targetRoot = Path.GetFullPath(targetDirectory);
        var targetPath = Path.GetFullPath(Path.Combine([targetRoot, .. GetSafePathParts(relativePath)]));
        var targetRootWithSeparator = targetRoot.EndsWith(Path.DirectorySeparatorChar)
            ? targetRoot
            : targetRoot + Path.DirectorySeparatorChar;

        if (!targetPath.StartsWith(targetRootWithSeparator, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(targetPath, targetRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Download path escapes the target directory.");
        }

        return targetPath;
    }

    private static string CreateAvailablePath(string targetDirectory, string relativePath)
    {
        var fullPath = BuildTargetPath(targetDirectory, relativePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (!File.Exists(fullPath))
        {
            return fullPath;
        }

        var finalDirectory = directory ?? targetDirectory;
        var fileName = Path.GetFileNameWithoutExtension(fullPath);
        var extension = Path.GetExtension(fullPath);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(finalDirectory, $"{fileName} ({i}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static string[] GetSafePathParts(string relativePath)
    {
        var parts = relativePath
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Select(SanitizePathSegment)
            .ToArray();

        return parts.Length == 0 ? ["mega-download"] : parts;
    }

    private static string SanitizePathSegment(string segment)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var cleaned = new string(segment.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray())
            .Trim()
            .TrimEnd('.');

        if (string.IsNullOrWhiteSpace(cleaned)
            || cleaned is "." or ".."
            || IsReservedWindowsFileName(cleaned))
        {
            return "_";
        }

        return cleaned.Length <= 120 ? cleaned : cleaned[..120];
    }

    private static bool IsReservedWindowsFileName(string segment)
    {
        var name = Path.GetFileNameWithoutExtension(segment);
        if (string.IsNullOrEmpty(name))
        {
            name = segment;
        }

        return ReservedWindowsFileNames.Contains(name);
    }

    private static readonly HashSet<string> ReservedWindowsFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "LPT1",
        "LPT2",
        "LPT3",
        "LPT4",
        "LPT5",
        "LPT6",
        "LPT7",
        "LPT8",
        "LPT9"
    };

    private void SetRunningState(bool isRunning)
    {
        _isRunning = isRunning;
        StartButton.IsEnabled = !isRunning;
        RetryErrorsButton.IsEnabled = !isRunning;
        RetrySelectedButton.IsEnabled = !isRunning;
        ClearAllButton.IsEnabled = !isRunning;
        StopButton.IsEnabled = isRunning;
    }
}

public sealed class MainWindowModel : ObservableObject
{
    private string _statusText = string.Empty;
    private string _queueSummary = "0 queued";
    private string _queueSearchText = string.Empty;
    private string _concurrencySummary = "2 active slots";
    private string _downloadDirectory = string.Empty;
    private int _concurrentDownloads = 2;
    private bool _autoStartAfterAdd;
    private bool _stopOnBandwidthLimit = true;
    private string _mediaPlayerPath = string.Empty;
    private bool _autoExtract;
    private bool _autoDetectClipboardLinks = true;
    private string _megaEmail = string.Empty;
    private bool _rememberMegaLogin;
    private string _megaAccountStatusText = "MEGA 익명 모드";
    private DownloadItemModel? _selectedItem;

    public ObservableCollection<DownloadItemModel> Items { get; } = [];

    public ObservableCollection<int> ConcurrentDownloadChoices { get; } = [1, 2, 3, 4, 5, 6];

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public string QueueSummary
    {
        get => _queueSummary;
        set => SetField(ref _queueSummary, value);
    }

    public string QueueSearchText
    {
        get => _queueSearchText;
        set => SetField(ref _queueSearchText, value);
    }

    public string ConcurrencySummary
    {
        get => _concurrencySummary;
        private set => SetField(ref _concurrencySummary, value);
    }

    public string DownloadDirectory
    {
        get => _downloadDirectory;
        set => SetField(ref _downloadDirectory, value);
    }

    public int ConcurrentDownloads
    {
        get => _concurrentDownloads;
        set
        {
            var normalized = Math.Clamp(value, 1, 6);
            if (SetField(ref _concurrentDownloads, normalized))
            {
                RefreshConcurrencySummary();
            }
        }
    }

    public bool AutoStartAfterAdd
    {
        get => _autoStartAfterAdd;
        set => SetField(ref _autoStartAfterAdd, value);
    }

    public bool StopOnBandwidthLimit
    {
        get => _stopOnBandwidthLimit;
        set => SetField(ref _stopOnBandwidthLimit, value);
    }

    public string MediaPlayerPath
    {
        get => _mediaPlayerPath;
        set => SetField(ref _mediaPlayerPath, value);
    }

    public bool AutoExtract
    {
        get => _autoExtract;
        set => SetField(ref _autoExtract, value);
    }

    public bool AutoDetectClipboardLinks
    {
        get => _autoDetectClipboardLinks;
        set => SetField(ref _autoDetectClipboardLinks, value);
    }

    public string MegaEmail
    {
        get => _megaEmail;
        set => SetField(ref _megaEmail, value);
    }

    public bool RememberMegaLogin
    {
        get => _rememberMegaLogin;
        set => SetField(ref _rememberMegaLogin, value);
    }

    public string MegaAccountStatusText
    {
        get => _megaAccountStatusText;
        set => SetField(ref _megaAccountStatusText, value);
    }

    public DownloadItemModel? SelectedItem
    {
        get => _selectedItem;
        set => SetField(ref _selectedItem, value);
    }

    public void RefreshSummary()
    {
        if (Items.Count == 0)
        {
            QueueSummary = "대기열이 비어 있음";
            return;
        }

        var active = Items.Count(item => item.State is DownloadState.Resolving or DownloadState.Downloading);   
        var completed = Items.Count(item => item.State == DownloadState.Completed);
        var skipped = Items.Count(item => item.State == DownloadState.Skipped);
        var attention = Items.Count(item => item.State is DownloadState.Failed or DownloadState.BandwidthLimited);
        QueueSummary = attention > 0
            ? $"총 {Items.Count}개 · {active}개 진행 중 · {completed}개 완료 · {skipped}개 스킵 · {attention}개 확인 필요"
            : $"총 {Items.Count}개 · {active}개 진행 중 · {completed}개 완료 · {skipped}개 스킵";
    }

    private void RefreshConcurrencySummary()
    {
        ConcurrencySummary = ConcurrentDownloads == 1
            ? "1개 슬롯 활성"
            : $"{ConcurrentDownloads}개 슬롯 활성";
    }
}

public sealed class DownloadItemModel : ObservableObject
{
    private string _name;
    private DownloadState _state;
    private string _progressText;
    private string _sizeText;
    private string _errorMessage;
    private string _stateText;
    private string _speedText;
    private string _etaText;
    private string _localPathText;
    private double _progressValue;
    private long _lastSpeedBytes;
    private DateTimeOffset _lastSpeedSample;
    private System.Windows.Media.Brush _stateForeground;
    private System.Windows.Media.Brush _stateBackground;

    private DownloadItemModel(DownloadItem item, DownloadItem source)
    {
        Source = source;
        _name = item.DisplayName;
        Kind = item.Kind;
        _state = item.State;
        _stateText = FormatState(item.State);
        _progressText = $"{item.Progress:P0}";
        _progressValue = item.Progress;
        _sizeText = FormatBytes(item.SizeBytes);
        _errorMessage = item.ErrorMessage ?? string.Empty;
        _speedText = "-";
        _etaText = "-";
        _localPathText = FormatLocalPath(item.LocalPath);
        _lastSpeedBytes = item.DownloadedBytes;
        _lastSpeedSample = DateTimeOffset.UtcNow;
        _stateForeground = GetStateForeground(item.State);
        _stateBackground = GetStateBackground(item.State);
        TargetDirectory = item.TargetDirectory;
        OriginalUrl = item.Link.OriginalUrl;
        NodeId = item.Link.NodeId;
        KeyPreview = item.Link.Key.Length <= 8
            ? item.Link.Key
            : $"{item.Link.Key[..4]}...{item.Link.Key[^4..]}";
    }

    public DownloadItem Source { get; }

    public string Name
    {
        get => _name;
        private set => SetField(ref _name, value);
    }

    public MegaLinkKind Kind { get; }

    public DownloadState State
    {
        get => _state;
        private set => SetField(ref _state, value);
    }

    public string StateText
    {
        get => _stateText;
        private set => SetField(ref _stateText, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetField(ref _progressText, value);
    }

    public string SpeedText
    {
        get => _speedText;
        private set => SetField(ref _speedText, value);
    }

    public string EtaText
    {
        get => _etaText;
        private set => SetField(ref _etaText, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set => SetField(ref _progressValue, value);
    }

    public string SizeText
    {
        get => _sizeText;
        private set => SetField(ref _sizeText, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetField(ref _errorMessage, value);
    }

    public string LocalPathText
    {
        get => _localPathText;
        private set => SetField(ref _localPathText, value);
    }

    public System.Windows.Media.Brush StateForeground
    {
        get => _stateForeground;
        private set => SetField(ref _stateForeground, value);
    }

    public System.Windows.Media.Brush StateBackground
    {
        get => _stateBackground;
        private set => SetField(ref _stateBackground, value);
    }

    public string TargetDirectory { get; }

    public string OriginalUrl { get; }

    public string NodeId { get; }

    public string KeyPreview { get; }

    public static DownloadItemModel From(DownloadItem item, DownloadItem source) => new(item, source);

    public void ResetTransferStats()
    {
        _lastSpeedBytes = Source.DownloadedBytes;
        _lastSpeedSample = DateTimeOffset.UtcNow;
        SpeedText = "-";
        EtaText = "-";
    }

    public void SyncFromSource()
    {
        Name = Source.DisplayName;
        State = Source.State;
        StateText = FormatState(Source.State);
        StateForeground = GetStateForeground(Source.State);
        StateBackground = GetStateBackground(Source.State);
        ProgressValue = Source.Progress;
        ProgressText = Source.SizeBytes is > 0
            ? $"{Source.Progress:P0}"
            : Source.DownloadedBytes > 0
                ? FormatBytes(Source.DownloadedBytes)
                : "0%";
        SizeText = FormatBytes(Source.SizeBytes);
        ErrorMessage = Source.ErrorMessage ?? string.Empty;
        LocalPathText = FormatLocalPath(Source.LocalPath);
        UpdateSpeed();
    }

    private void UpdateSpeed()
    {
        if (Source.State != DownloadState.Downloading)
        {
            if (Source.State is DownloadState.Completed or DownloadState.Queued or DownloadState.Failed or DownloadState.Canceled or DownloadState.BandwidthLimited or DownloadState.Skipped or DownloadState.Paused)
            {
                SpeedText = "-";
                EtaText = "-";
            }

            _lastSpeedBytes = Source.DownloadedBytes;
            _lastSpeedSample = DateTimeOffset.UtcNow;
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var elapsedSeconds = (now - _lastSpeedSample).TotalSeconds;
        if (elapsedSeconds < 0.5)
        {
            return;
        }

        var deltaBytes = Math.Max(0, Source.DownloadedBytes - _lastSpeedBytes);
        var bytesPerSecond = deltaBytes / elapsedSeconds;
        SpeedText = $"{FormatBytes((long)bytesPerSecond)}/s";
        EtaText = FormatEta(Source.SizeBytes, Source.DownloadedBytes, bytesPerSecond);
        _lastSpeedBytes = Source.DownloadedBytes;
        _lastSpeedSample = now;
    }

    private static string FormatBytes(long? bytes)
    {
        if (bytes is null)
        {
            return "-";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes.Value;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    private static string FormatLocalPath(string? path)
    {
        return string.IsNullOrWhiteSpace(path) ? "-" : path;
    }

    private static string FormatEta(long? totalBytes, long downloadedBytes, double bytesPerSecond)
    {
        if (totalBytes is null or <= 0)
        {
            return "-";
        }

        var remainingBytes = Math.Max(0, totalBytes.Value - downloadedBytes);
        if (remainingBytes == 0)
        {
            return "-";
        }

        if (bytesPerSecond <= 1)
        {
            return "계산 중";
        }

        var remaining = TimeSpan.FromSeconds(remainingBytes / bytesPerSecond);
        if (remaining.TotalDays >= 1)
        {
            return $"{(int)remaining.TotalDays}일 {remaining.Hours}시간";
        }

        if (remaining.TotalHours >= 1)
        {
            return $"{(int)remaining.TotalHours}시간 {remaining.Minutes}분";
        }

        if (remaining.TotalMinutes >= 1)
        {
            return $"{(int)remaining.TotalMinutes}분 {remaining.Seconds}초";
        }

        return $"{Math.Max(1, remaining.Seconds)}초";
    }

    private static string FormatState(DownloadState state)
    {
        return state switch
        {
            DownloadState.Queued => "대기 중",
            DownloadState.Resolving => "주소 확인 중",
            DownloadState.Downloading => "다운로드 중",
            DownloadState.Paused => "일시 정지",
            DownloadState.Completed => "완료됨",
            DownloadState.Failed => "오류 발생",
            DownloadState.Canceled => "취소됨",
            DownloadState.BandwidthLimited => "대역폭 제한",
            DownloadState.Skipped => "스킵됨",
            _ => state.ToString()
        };
    }

    private static System.Windows.Media.Brush GetStateForeground(DownloadState state)
    {
        return state switch
        {
            DownloadState.Completed => BrushFrom("#107C10"),
            DownloadState.Downloading => BrushFrom("#0078D4"),
            DownloadState.Resolving => BrushFrom("#6B4F00"),
            DownloadState.Paused => BrushFrom("#847545"),
            DownloadState.BandwidthLimited => BrushFrom("#D83B01"),
            DownloadState.Skipped => BrushFrom("#616161"),
            DownloadState.Failed => BrushFrom("#C42B1C"),
            DownloadState.Canceled => BrushFrom("#6E5C4A"),
            _ => BrushFrom("#484848")
        };
    }

    private static System.Windows.Media.Brush GetStateBackground(DownloadState state)
    {
        return state switch
        {
            DownloadState.Completed => BrushFrom("#DFF6DD"),
            DownloadState.Downloading => BrushFrom("#E5F0FC"),
            DownloadState.Resolving => BrushFrom("#FFF4CE"),
            DownloadState.Paused => BrushFrom("#FFF9E5"),
            DownloadState.BandwidthLimited => BrushFrom("#FED9CC"),
            DownloadState.Skipped => BrushFrom("#F3F3F3"),
            DownloadState.Failed => BrushFrom("#FDE7E9"),
            DownloadState.Canceled => BrushFrom("#F0ECE6"),
            _ => BrushFrom("#F3F3F3")
        };
    }

    private static System.Windows.Media.Brush BrushFrom(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}

public sealed record AppSettings(
    string? DownloadDirectory,
    int? ConcurrentDownloads,
    bool? AutoStartAfterAdd,
    bool? StopOnBandwidthLimit,
    string? MediaPlayerPath = null,
    bool? AutoExtract = null,
    bool? AutoDetectClipboardLinks = null,
    string? MegaEmail = null,
    bool? RememberMegaLogin = null,
    string? ProtectedMegaSession = null);

public sealed record PersistedMegaSession(
    string Email,
    string SessionId,
    string MasterKeyBase64);

public sealed record PersistedDownloadQueue(
    int Version,
    IReadOnlyList<PersistedDownloadItem> Items)
{
    public static PersistedDownloadQueue Empty { get; } = new(1, []);
}

public sealed record PersistedDownloadItem(
    MegaLink Link,
    string TargetDirectory,
    string? LocalPath,
    string? Name,
    DownloadState State,
    long? SizeBytes,
    long DownloadedBytes,
    string? ErrorMessage,
    PersistedMegaFolderFile? FolderFile);

public sealed record PersistedMegaFolderFile(
    string RelativePath,
    string NodeId,
    string Name,
    long SizeBytes,
    string? ParentId,
    string FullKeyBase64);

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
