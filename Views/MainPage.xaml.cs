using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using GLook.Models;
using GLook.Services;
using GLook.ViewModels;
using Microsoft.Web.WebView2.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace GLook.Views;

public sealed partial class MainPage : Page
{
    private const int ArrowCursorId = 32512;
    private const int SizeWestEastCursorId = 32644;
    private const int SizeNorthSouthCursorId = 32645;
    private const int DefaultAutoSyncIntervalMinutes = 5;
    private const int MinimumAutoSyncIntervalMinutes = 1;
    private const int MaximumAutoSyncIntervalMinutes = 60;
    private const double CompactReadingBreakpoint = 980;
    private const double OverlayFoldersBreakpoint = 760;
    private const double MinimumFolderWidth = 210;
    private const double MaximumFolderWidth = 460;
    private const double MinimumMessageListWidth = 280;
    private const double MinimumReadingPaneWidth = 340;
    private const double MinimumMessageListHeight = 190;
    private const double MinimumReadingPaneHeight = 240;
    private bool isCompact;
    private bool composeDialogOpen;
    private bool notificationPollInProgress;
    private bool autoSyncInProgress;
    private bool notificationBaselineEstablished;
    private readonly HashSet<string> knownUnreadInboxThreads = new(StringComparer.Ordinal);
    private DispatcherQueueTimer? autoSyncTimer;
    private bool autoSyncEnabled = true;
    private int autoSyncIntervalMinutes = DefaultAutoSyncIntervalMinutes;
    private ReadingPaneMode readingPaneMode = ReadingPaneMode.Right;
    private MessageDensity messageDensity = MessageDensity.Comfortable;
    private ThreadGrouping threadGrouping = ThreadGrouping.Date;
    private ThreadSort threadSort = ThreadSort.Newest;
    private bool showMessageListToolbar = true;
    private bool showMessagePreview = true;
    private bool showSenderAvatars = true;
    private bool isRebuildingThreadView;
    private bool isUpdatingThreadSelectionBoxes;
    private bool messageWebViewInitialized;
    private bool showRemoteMessageContent;
    private string? renderedThreadId;
    private readonly HashSet<string> collapsedThreadGroups = new(StringComparer.Ordinal);
    private readonly HashSet<string> selectedThreadIds = new(StringComparer.Ordinal);
    private readonly ObservableCollection<MailThreadGroup> threadGroups = [];
    private readonly CollectionViewSource threadGroupsSource = new() { IsSourceGrouped = true };
    private TreeViewNode? contextFolderNode;
    private bool? folderContextHasPointerTarget;
    private bool? threadContextHasPointerTarget;
    private double messageListWidth = 390;
    private double messageListHeight = 330;
    private readonly string uiSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GLook",
        "ui-settings.json");

    public MainViewModel ViewModel { get; }

    public MainPage()
    {
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GLook");
        var secureStore = new EncryptedDataStore(Path.Combine(appDataPath, "secure"));
        var gmail = new GmailClientService(secureStore);
        var localStore = new LocalMailStore(Path.Combine(appDataPath, "mail.db"));
        var signatureStore = new SignatureSettingsStore(secureStore);

        ViewModel = new MainViewModel(gmail, localStore, signatureStore);
        InitializeComponent();
        DataContext = ViewModel;
        ComposeDialog.DataContext = ViewModel;
        threadGroupsSource.Source = threadGroups;
        ViewModel.Threads.CollectionChanged += (_, _) => RebuildThreadView();
        LoadUiSettings();
        ApplyDensity();
        UpdateViewOptionChecks();
        ApplyMessageListViewOptions();
        RebuildThreadView();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateSyncProgressIndicator();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();
        UpdateContentPanels();
        SyncFolderTree();
        await PollForNewMailAsync();
        StartAutoSyncTimer();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        autoSyncTimer?.Stop();
        autoSyncTimer = null;
        SaveUiSettings();
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        isCompact = e.NewSize.Width < CompactReadingBreakpoint;
        FolderSplitView.DisplayMode = e.NewSize.Width < OverlayFoldersBreakpoint
            ? SplitViewDisplayMode.Overlay
            : SplitViewDisplayMode.Inline;

        if (FolderSplitView.DisplayMode == SplitViewDisplayMode.Overlay)
        {
            FolderSplitView.IsPaneOpen = false;
        }
        else if (!FolderSplitView.IsPaneOpen)
        {
            FolderSplitView.IsPaneOpen = true;
        }

        ApplyReadingLayout();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsConnected) or nameof(MainViewModel.SelectedThreadDetail))
        {
            UpdateContentPanels();
            ApplyReadingLayout();
        }
        else if (e.PropertyName == nameof(MainViewModel.SelectedFolder))
        {
            SyncFolderSelection();
        }
        else if (e.PropertyName == nameof(MainViewModel.IsSyncingAll))
        {
            UpdateSyncProgressIndicator();
        }
    }

    private void ToggleFolders_Click(object sender, RoutedEventArgs e)
    {
        FolderSplitView.IsPaneOpen = !FolderSplitView.IsPaneOpen;
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsConnected)
        {
            Account_Click(sender, e);
            return;
        }

        try
        {
            await ViewModel.ConnectAsync();
            SyncFolderTree();
            notificationBaselineEstablished = false;
            knownUnreadInboxThreads.Clear();
            await PollForNewMailAsync();
            StartAutoSyncTimer();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Gmail connection failed", ex.GetBaseException().Message);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshAsync();
        SyncFolderTree();
        await PollForNewMailAsync();
        StartAutoSyncTimer();
    }

    private async void SyncAll_Click(object sender, RoutedEventArgs e)
    {
        var succeeded = await ViewModel.SyncAllAsync();
        SyncFolderTree();
        UpdateContentPanels();
        if (succeeded)
        {
            await PollForNewMailAsync();
        }

        StartAutoSyncTimer();
    }

    private void CancelSyncAll_Click(object sender, RoutedEventArgs e) =>
        ViewModel.CancelSyncAll();

    private async void FolderTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        contextFolderNode = null;
        folderContextHasPointerTarget = null;
        var treeItem = args.InvokedItem switch
        {
            FolderTreeItem directItem => directItem,
            TreeViewNode { Content: FolderTreeItem nodeItem } => nodeItem,
            _ => null
        };

        if (treeItem?.Folder is not { } folder)
        {
            return;
        }

        await ViewModel.SelectFolderAsync(folder);
        SyncFolderTree();
        if (FolderSplitView.DisplayMode == SplitViewDisplayMode.Overlay)
        {
            FolderSplitView.IsPaneOpen = false;
        }
    }

    private void FolderTree_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var container = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        contextFolderNode = container is null ? null : FolderTree.NodeFromContainer(container);
        folderContextHasPointerTarget = contextFolderNode is not null;
        if (contextFolderNode is not null)
        {
            FolderTree.SelectedNode = contextFolderNode;
        }
    }

    private void FolderContextMenu_Opening(object sender, object e)
    {
        if (folderContextHasPointerTarget is not false)
        {
            contextFolderNode ??= FolderTree.SelectedNode;
        }

        var treeItem = contextFolderNode?.Content as FolderTreeItem;
        var folder = treeItem?.Folder;
        var hasBranch = contextFolderNode?.HasChildren == true;
        var isUserLabelPath = treeItem is not null && folder?.IsSystem != true;

        ContextOpenFolderItem.IsEnabled = folder is not null;
        ContextNewSubfolderItem.IsEnabled = ViewModel.IsConnected && isUserLabelPath;
        ContextRenameFolderItem.IsEnabled = ViewModel.IsConnected && folder?.CanDelete == true;
        ContextDeleteFolderItem.IsEnabled = ViewModel.IsConnected && folder?.CanDelete == true;
        ContextExpandBranchItem.IsEnabled = hasBranch;
        ContextCollapseBranchItem.IsEnabled = hasBranch;
    }

    private void FolderContextMenu_Closed(object sender, object e)
    {
        contextFolderNode = null;
        folderContextHasPointerTarget = null;
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextFolderItem()?.Folder is not { } folder)
        {
            return;
        }

        await ViewModel.SelectFolderAsync(folder);
        SyncFolderTree();
    }

    private async void NewSubfolder_Click(object sender, RoutedEventArgs e)
    {
        var parent = GetContextFolderItem();
        if (parent is null || parent.Folder?.IsSystem == true)
        {
            return;
        }

        var input = new TextBox
        {
            Header = "Subfolder name",
            PlaceholderText = "Planning",
            Description = $"This creates a Gmail label under {parent.FullName}. Use / for another nested level."
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"New subfolder under {parent.Name}",
            Content = input,
            PrimaryButtonText = "Create subfolder",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var childPath = input.Text.Trim().Trim('/');
        if (string.IsNullOrWhiteSpace(childPath))
        {
            await ShowMessageAsync("Enter a subfolder name", "The Gmail label needs a name beneath the selected folder.");
            return;
        }

        try
        {
            await ViewModel.CreateFolderAsync($"{parent.FullName}/{childPath}");
            SyncFolderTree();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Subfolder was not created", ex.GetBaseException().Message);
        }
    }

    private async void RenameFolder_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextFolderItem()?.Folder is not { CanDelete: true } folder)
        {
            return;
        }

        var input = new TextBox
        {
            Header = "Folder name",
            Text = folder.Name,
            Description = "Nested folders stay together when this Gmail label is renamed."
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Rename {folder.Name}",
            Content = input,
            PrimaryButtonText = "Rename label",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await ViewModel.RenameFolderAsync(folder, input.Text);
            SyncFolderTree();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Label was not renamed", ex.GetBaseException().Message);
        }
    }

    private async void DeleteContextFolder_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextFolderItem()?.Folder is { } folder)
        {
            await DeleteFolderWithConfirmationAsync(folder);
        }
    }

    private void ExpandFolderBranch_Click(object sender, RoutedEventArgs e)
    {
        if (contextFolderNode is { } node)
        {
            SetFolderExpansion(node, true);
        }
    }

    private void CollapseFolderBranch_Click(object sender, RoutedEventArgs e)
    {
        if (contextFolderNode is { } node)
        {
            SetFolderExpansion(node, false);
        }
    }

    private void ExpandAllFolders_Click(object sender, RoutedEventArgs e) =>
        SetFolderExpansion(FolderTree.RootNodes, true);

    private void CollapseAllFolders_Click(object sender, RoutedEventArgs e) =>
        SetFolderExpansion(FolderTree.RootNodes, false);

    private async void SyncFolders_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshAsync();
        SyncFolderTree();
    }

    private async void ThreadList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MailThreadSummary thread)
        {
            isUpdatingThreadSelectionBoxes = true;
            ThreadList.SelectedItems.Clear();
            ThreadList.SelectedItems.Add(thread);
            isUpdatingThreadSelectionBoxes = false;
            selectedThreadIds.Clear();
            selectedThreadIds.Add(thread.Id);
            ApplyRealizedThreadSelectionBoxes();
            UpdateThreadSelectionStatus();
            await ViewModel.SelectThreadAsync(thread);
        }
    }

    private void ThreadList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isRebuildingThreadView || isUpdatingThreadSelectionBoxes)
        {
            return;
        }

        selectedThreadIds.Clear();
        foreach (var thread in ThreadList.SelectedItems.OfType<MailThreadSummary>())
        {
            selectedThreadIds.Add(thread.Id);
        }

        UpdateThreadSelectionStatus();
        ApplyRealizedThreadSelectionBoxes();
    }

    private void ThreadSelectionCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        if (isUpdatingThreadSelectionBoxes
            || sender is not CheckBox { CommandParameter: MailThreadSummary thread })
        {
            return;
        }

        if (!ThreadList.SelectedItems.OfType<MailThreadSummary>().Any(item => item.Id == thread.Id))
        {
            ThreadList.SelectedItems.Add(thread);
        }
    }

    private void ThreadSelectionCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        if (isUpdatingThreadSelectionBoxes
            || sender is not CheckBox { CommandParameter: MailThreadSummary thread })
        {
            return;
        }

        var selected = ThreadList.SelectedItems.OfType<MailThreadSummary>()
            .FirstOrDefault(item => item.Id == thread.Id);
        if (selected is not null)
        {
            ThreadList.SelectedItems.Remove(selected);
        }
    }

    private void ThreadSelectionCheckBox_Tapped(object sender, TappedRoutedEventArgs e) =>
        e.Handled = true;

    private void SelectAllThreads_Click(object sender, RoutedEventArgs e) => ThreadList.SelectAll();

    private void ClearThreadSelection_Click(object sender, RoutedEventArgs e)
    {
        ThreadList.SelectedItems.Clear();
        selectedThreadIds.Clear();
        ViewModel.SelectedThread = null;
        ViewModel.SelectedThreadDetail = null;
        UpdateThreadSelectionStatus();
    }

    private void ThreadGroupHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: MailThreadGroup group })
        {
            return;
        }

        if (!collapsedThreadGroups.Add(group.Key))
        {
            collapsedThreadGroups.Remove(group.Key);
        }
        else
        {
            var hiddenIds = group.AllThreads.Select(thread => thread.Id).ToHashSet(StringComparer.Ordinal);
            selectedThreadIds.ExceptWith(hiddenIds);
            if (ViewModel.SelectedThread is { } selected && hiddenIds.Contains(selected.Id))
            {
                ViewModel.SelectedThread = null;
                ViewModel.SelectedThreadDetail = null;
            }
        }

        RebuildThreadView();
    }

    private void ThreadList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var item = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is MailThreadSummary thread)
        {
            threadContextHasPointerTarget = true;
            if (!selectedThreadIds.Contains(thread.Id))
            {
                ThreadList.SelectedItems.Clear();
                ThreadList.SelectedItems.Add(thread);
            }

            if (ViewModel.SelectedThread?.Id != thread.Id)
            {
                ViewModel.SelectedThread = thread;
                ViewModel.SelectedThreadDetail = null;
                UpdateContentPanels();
            }
        }
        else
        {
            threadContextHasPointerTarget = false;
        }
    }

    private async void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        ViewModel.SearchQuery = args.QueryText;
        await ViewModel.SearchAsync();
    }

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var input = new TextBox
        {
            Header = "Folder name",
            PlaceholderText = "Projects/GLook",
            Description = "Use / to create nested Gmail labels."
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Create Gmail folder",
            Content = input,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await ViewModel.CreateFolderAsync(input.Text);
            SyncFolderTree();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Folder was not created", ex.GetBaseException().Message);
        }
    }

    private async void DeleteFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = ViewModel.SelectedFolder;
        if (folder is null || !folder.CanDelete)
        {
            await ShowMessageAsync("This folder cannot be deleted", "Gmail system folders such as Inbox, Sent, and Trash are permanent.");
            return;
        }

        await DeleteFolderWithConfirmationAsync(folder);
    }

    private async Task DeleteFolderWithConfirmationAsync(MailFolder folder)
    {
        int threadCount;
        try
        {
            threadCount = await ViewModel.GetFolderThreadCountAsync(folder);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Label contents could not be checked", ex.GetBaseException().Message);
            return;
        }

        var hasMail = threadCount > 0;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Delete label {folder.FullName}?",
            Content = hasMail
                ? $"This label contains {threadCount:N0} conversation{(threadCount == 1 ? string.Empty : "s")}. Continuing will move every one of those conversations to Gmail Trash first, then remove the label. This affects the whole conversation even when it also has other labels. Nested labels are not deleted."
                : "This label is empty. The Gmail label will be removed. Nested labels are not deleted.",
            PrimaryButtonText = hasMail ? "Trash mail and delete label" : "Delete label",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            try
            {
                if (hasMail)
                {
                    await ViewModel.DeleteFolderAndTrashContentsAsync(folder);
                }
                else
                {
                    await ViewModel.DeleteFolderAsync(folder);
                }
                SyncFolderTree();
            }
            catch (Exception ex)
            {
                await ShowMessageAsync("Label was not deleted", ex.GetBaseException().Message);
            }
        }
    }

    private async void DeleteThread_Click(object sender, RoutedEventArgs e)
    {
        var threads = SelectedThreadsForAction();
        await ConfirmTrashThreadsAsync(threads);
    }

    private async void DeleteThreadCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: MailThreadSummary thread })
        {
            await ConfirmTrashThreadsAsync([thread]);
        }
    }

    private async Task ConfirmTrashThreadsAsync(IReadOnlyList<MailThreadSummary> threads)
    {
        if (threads.Count == 0)
        {
            return;
        }

        var plural = threads.Count != 1;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = plural
                ? $"Move {threads.Count} conversations to Trash?"
                : "Move this conversation to Trash?",
            Content = plural
                ? "The selected conversations will move to Gmail Trash on every device."
                : "The conversation will move to Gmail Trash on every device.",
            PrimaryButtonText = "Move to Trash",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.TrashThreadsAsync(threads);
            SyncFolderTree();
            UpdateContentPanels();
        }
    }

    private async void Account_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsConnected)
        {
            Connect_Click(sender, e);
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = ViewModel.AccountEmail,
            Content = "Disconnecting removes the encrypted OAuth session from this PC. Your Gmail data is not changed.",
            PrimaryButtonText = "Disconnect",
            CloseButtonText = "Keep connected",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.DisconnectAsync();
            notificationBaselineEstablished = false;
            knownUnreadInboxThreads.Clear();
            SyncFolderTree();
        }
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var autoSyncToggle = new ToggleSwitch
        {
            Header = "Automatic sync",
            OffContent = "Off",
            OnContent = "On",
            IsOn = autoSyncEnabled
        };
        var intervalBox = new NumberBox
        {
            Header = "Check Gmail every (minutes)",
            Minimum = MinimumAutoSyncIntervalMinutes,
            Maximum = MaximumAutoSyncIntervalMinutes,
            SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Value = autoSyncIntervalMinutes,
            IsEnabled = autoSyncEnabled
        };
        autoSyncToggle.Toggled += (_, _) => intervalBox.IsEnabled = autoSyncToggle.IsOn;

        var notificationStatus = new TextBlock
        {
            Text = GetNotificationStatusText(),
            TextWrapping = TextWrapping.Wrap
        };
        var testNotificationButton = new Button
        {
            Content = "Send test notification",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            testNotificationButton,
            "Send test Windows notification");
        testNotificationButton.Click += async (_, _) =>
        {
            testNotificationButton.IsEnabled = false;
            notificationStatus.Text = "Sending a test notification…";
            try
            {
                if (Application.Current is not App app || app.NotificationService is null)
                {
                    notificationStatus.Text = "The Windows notification service is not available.";
                    return;
                }

                var result = await app.NotificationService.ShowTestAsync();
                notificationStatus.Text = result.Message;
            }
            catch (Exception ex)
            {
                notificationStatus.Text = $"The notification test failed: {ex.GetBaseException().Message}";
            }
            finally
            {
                testNotificationButton.IsEnabled = true;
            }
        };

        var windowsSettingsButton = new Button
        {
            Content = "Open Windows notification settings",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        windowsSettingsButton.Click += async (_, _) =>
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:notifications"));
        };

        var syncSection = new StackPanel { Spacing = 8 };
        syncSection.Children.Add(new TextBlock
        {
            Text = "SYNC",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        syncSection.Children.Add(autoSyncToggle);
        syncSection.Children.Add(intervalBox);
        syncSection.Children.Add(new TextBlock
        {
            Text = "Automatic sync refreshes the selected folder and checks for new Inbox mail while GLook is running.",
            TextWrapping = TextWrapping.Wrap
        });

        var notificationSection = new StackPanel { Spacing = 8 };
        notificationSection.Children.Add(new TextBlock
        {
            Text = "WINDOWS NOTIFICATIONS",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        notificationSection.Children.Add(notificationStatus);
        notificationSection.Children.Add(testNotificationButton);
        notificationSection.Children.Add(windowsSettingsButton);

        var content = new StackPanel
        {
            MaxWidth = 460,
            Spacing = 20
        };
        content.Children.Add(syncSection);
        content.Children.Add(notificationSection);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "GLook settings",
            Content = content,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        autoSyncEnabled = autoSyncToggle.IsOn;
        var requestedInterval = double.IsNaN(intervalBox.Value)
            ? DefaultAutoSyncIntervalMinutes
            : (int)Math.Round(intervalBox.Value);
        autoSyncIntervalMinutes = Math.Clamp(
            requestedInterval,
            MinimumAutoSyncIntervalMinutes,
            MaximumAutoSyncIntervalMinutes);
        SaveUiSettings();
        StartAutoSyncTimer();
    }

    private static string GetNotificationStatusText()
    {
        if (Application.Current is not App app || app.NotificationService is null)
        {
            return "The Windows notification service is not available.";
        }

        if (!app.NotificationService.IsRegistered)
        {
            return app.NotificationService.RegistrationError
                ?? "GLook has not registered with Windows notifications.";
        }

        try
        {
            return app.NotificationService.StatusText;
        }
        catch (Exception ex)
        {
            return $"Windows notification status could not be read: {ex.GetBaseException().Message}";
        }
    }

    private async void Signatures_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.RefreshSignaturesAsync();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync(
                "Signatures could not be loaded",
                $"GLook needs Gmail settings access to read and edit signatures. {ex.GetBaseException().Message}");
            return;
        }

        var drafts = new ObservableCollection<SignatureDraft>(
            ViewModel.Signatures.Select(signature => new SignatureDraft(signature)));
        var signaturePicker = new ComboBox
        {
            Header = "Signature",
            ItemsSource = drafts,
            DisplayMemberPath = nameof(SignatureDraft.DisplayLabel),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = drafts.Count > 0 ? 0 : -1
        };
        var nameEditor = new TextBox
        {
            Header = "Signature name",
            PlaceholderText = "For example, Work or Personal",
            IsEnabled = drafts.FirstOrDefault()?.IsGmailBacked == false
        };
        var editor = new TextBox
        {
            Header = "Signature text",
            AcceptsReturn = true,
            IsSpellCheckEnabled = true,
            MinHeight = 180,
            TextWrapping = TextWrapping.Wrap
        };
        var sourceNote = new TextBlock
        {
            Style = (Style)Application.Current.Resources["GLookMetadataStyle"],
            TextWrapping = TextWrapping.Wrap
        };
        var newButton = new Button { Content = "New GLook signature" };
        var deleteButton = new Button
        {
            Content = "Delete local signature",
            IsEnabled = false,
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"]
        };
        var choices = new ObservableCollection<SignatureChoice>
        {
            new(null, "No signature")
        };
        foreach (var signature in ViewModel.Signatures)
        {
            choices.Add(new SignatureChoice(signature.Id, signature.DisplayLabel));
        }
        SignatureDraft? currentSignature = null;
        var loadingEditor = false;
        void LoadDraft(SignatureDraft? selected)
        {
            currentSignature = selected;
            loadingEditor = true;
            nameEditor.Text = selected?.DisplayName ?? string.Empty;
            editor.Text = selected?.Text ?? string.Empty;
            nameEditor.IsEnabled = selected is { IsGmailBacked: false };
            editor.IsEnabled = selected is not null;
            deleteButton.IsEnabled = selected is { IsGmailBacked: false };
            sourceNote.Text = selected is null
                ? "Create a GLook signature to get started."
                : selected.IsGmailBacked
                    ? "This signature is linked to the Gmail send-as address. Saving text changes updates Gmail."
                    : "This named signature is encrypted on this PC and is available in GLook only. Gmail's API does not expose Gmail web's multiple named-signature library.";
            loadingEditor = false;
        }

        signaturePicker.SelectionChanged += (_, _) =>
        {
            LoadDraft(signaturePicker.SelectedItem as SignatureDraft);
        };
        nameEditor.TextChanged += (_, _) =>
        {
            if (!loadingEditor && currentSignature is { IsGmailBacked: false })
            {
                currentSignature.DisplayName = nameEditor.Text;
                var choice = choices.FirstOrDefault(item => item.EmailAddress == currentSignature.Id);
                if (choice is not null)
                {
                    choice.Label = currentSignature.DisplayLabel;
                }
            }
        };
        editor.TextChanged += (_, _) =>
        {
            if (!loadingEditor && currentSignature is not null)
            {
                currentSignature.Text = editor.Text;
            }
        };

        var newDefault = CreateSignatureChoiceBox("New messages", choices, ViewModel.NewSignatureEmail);
        var replyDefault = CreateSignatureChoiceBox("Replies", choices, ViewModel.ReplySignatureEmail);
        var forwardDefault = CreateSignatureChoiceBox("Forwards", choices, ViewModel.ForwardSignatureEmail);
        newButton.Click += (_, _) =>
        {
            var number = 1;
            string name;
            do
            {
                name = number == 1 ? "New signature" : $"New signature {number}";
                number++;
            }
            while (drafts.Any(item => string.Equals(item.DisplayName, name, StringComparison.CurrentCultureIgnoreCase)));

            var draft = SignatureDraft.CreateLocal(name);
            drafts.Add(draft);
            choices.Add(new SignatureChoice(draft.Id, draft.DisplayLabel));
            signaturePicker.SelectedItem = draft;
            nameEditor.Focus(FocusState.Programmatic);
            nameEditor.SelectAll();
        };
        deleteButton.Click += (_, _) =>
        {
            if (currentSignature is not { IsGmailBacked: false } local)
            {
                return;
            }

            var removedChoice = choices.FirstOrDefault(choice => choice.EmailAddress == local.Id);
            if (removedChoice is not null)
            {
                if (ReferenceEquals(newDefault.SelectedItem, removedChoice)) newDefault.SelectedIndex = 0;
                if (ReferenceEquals(replyDefault.SelectedItem, removedChoice)) replyDefault.SelectedIndex = 0;
                if (ReferenceEquals(forwardDefault.SelectedItem, removedChoice)) forwardDefault.SelectedIndex = 0;
                choices.Remove(removedChoice);
            }

            var index = drafts.IndexOf(local);
            drafts.Remove(local);
            signaturePicker.SelectedIndex = drafts.Count == 0 ? -1 : Math.Min(index, drafts.Count - 1);
            LoadDraft(signaturePicker.SelectedItem as SignatureDraft);
        };

        var editorButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        editorButtons.Children.Add(newButton);
        editorButtons.Children.Add(deleteButton);
        var content = new StackPanel { Width = 680, Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = "Google exposes one signature per Gmail send-as address. You can also create multiple named GLook signatures and choose separate defaults for new mail, replies, and forwards; those extra signatures do not appear in Gmail web.",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(editorButtons);
        content.Children.Add(signaturePicker);
        content.Children.Add(sourceNote);
        content.Children.Add(nameEditor);
        content.Children.Add(editor);
        var defaultsGrid = new Grid { ColumnSpacing = 10 };
        defaultsGrid.ColumnDefinitions.Add(new ColumnDefinition());
        defaultsGrid.ColumnDefinitions.Add(new ColumnDefinition());
        defaultsGrid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(replyDefault, 1);
        Grid.SetColumn(forwardDefault, 2);
        defaultsGrid.Children.Add(newDefault);
        defaultsGrid.Children.Add(replyDefault);
        defaultsGrid.Children.Add(forwardDefault);
        content.Children.Add(defaultsGrid);
        LoadDraft(drafts.FirstOrDefault());

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Signatures",
            Content = new ScrollViewer { MaxHeight = 640, Content = content },
            PrimaryButtonText = "Save signatures",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await ViewModel.SaveSignatureSettingsAsync(
                drafts.Select(draft => draft.ToMailSignature()).ToList(),
                (newDefault.SelectedItem as SignatureChoice)?.EmailAddress,
                (replyDefault.SelectedItem as SignatureChoice)?.EmailAddress,
                (forwardDefault.SelectedItem as SignatureChoice)?.EmailAddress);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Signatures were not saved", ex.GetBaseException().Message);
        }
    }

    private static ComboBox CreateSignatureChoiceBox(
        string header,
        IReadOnlyList<SignatureChoice> choices,
        string? selectedEmail)
    {
        var box = new ComboBox
        {
            Header = header,
            ItemsSource = choices,
            DisplayMemberPath = nameof(SignatureChoice.Label),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        box.SelectedItem = choices.FirstOrDefault(choice =>
            string.Equals(choice.EmailAddress, selectedEmail, StringComparison.OrdinalIgnoreCase))
            ?? choices[0];
        return box;
    }

    private async void NewEmail_Click(object sender, RoutedEventArgs e) =>
        await OpenComposeAsync("New email", () =>
        {
            ViewModel.StartNewCompose();
            return Task.CompletedTask;
        });

    private async void Reply_Click(object sender, RoutedEventArgs e) =>
        await OpenComposeAsync("Reply", ViewModel.StartReplyAsync);

    private async void ReplyAll_Click(object sender, RoutedEventArgs e) =>
        await OpenComposeAsync("Reply all", ViewModel.StartReplyAllAsync);

    private async void Forward_Click(object sender, RoutedEventArgs e) =>
        await OpenComposeAsync("Forward", ViewModel.StartForwardAsync);

    private async Task OpenComposeAsync(string title, Func<Task> prepare)
    {
        if (composeDialogOpen || !ViewModel.IsConnected)
        {
            return;
        }

        await prepare();
        if (!ViewModel.IsComposeOpen)
        {
            return;
        }

        composeDialogOpen = true;
        ComposeDialog.Title = title;
        ComposeDialog.XamlRoot = XamlRoot;
        try
        {
            await ComposeDialog.ShowAsync();
            if (ViewModel.IsComposeOpen)
            {
                await ViewModel.CloseComposeAsync();
            }
        }
        finally
        {
            composeDialogOpen = false;
            SyncFolderTree();
        }
    }

    private async void ComposeDialog_PrimaryButtonClick(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            await ViewModel.SendComposeAsync();
            args.Cancel = ViewModel.IsComposeOpen;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void ComposeDialog_SecondaryButtonClick(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            await ViewModel.SaveDraftAsync();
            args.Cancel = !string.IsNullOrWhiteSpace(ViewModel.ComposeErrorText);
            if (!args.Cancel)
            {
                await ViewModel.CloseComposeAsync();
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void ComposeDialog_CloseButtonClick(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var hasContent = !string.IsNullOrWhiteSpace(ViewModel.ComposeTo)
                || !string.IsNullOrWhiteSpace(ViewModel.ComposeCc)
                || !string.IsNullOrWhiteSpace(ViewModel.ComposeBcc)
                || !string.IsNullOrWhiteSpace(ViewModel.ComposeSubject)
                || !string.IsNullOrWhiteSpace(ViewModel.ComposeBody)
                || ViewModel.ComposeAttachmentPaths.Count > 0
                || ViewModel.ComposeRetainedAttachments.Count > 0;
            if (hasContent)
            {
                await ViewModel.SaveDraftAsync();
                args.Cancel = !string.IsNullOrWhiteSpace(ViewModel.ComposeErrorText);
            }

            if (!args.Cancel)
            {
                await ViewModel.CloseComposeAsync();
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void DiscardCompose_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.CloseComposeAsync(discardDraft: true);
        ComposeDialog.Hide();
    }

    private async void AttachFiles_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        if (Application.Current is not App { MainWindow: { } window })
        {
            return;
        }

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        var files = await picker.PickMultipleFilesAsync();
        ViewModel.AddComposeAttachments(files.Select(file => file.Path));
    }

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: string path })
        {
            ViewModel.RemoveComposeAttachment(path);
        }
        else if (sender is Button { CommandParameter: MailAttachmentInput attachment })
        {
            ViewModel.RemoveComposeAttachment(attachment);
        }
    }

    private async void ArchiveThread_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ArchiveThreadsAsync(SelectedThreadsForAction());
        SyncFolderTree();
        UpdateContentPanels();
    }

    private async void ToggleReadThread_Click(object sender, RoutedEventArgs e)
    {
        var threads = SelectedThreadsForAction();
        await ViewModel.SetThreadsReadAsync(threads, threads.Any(thread => thread.IsUnread));
        SyncFolderTree();
    }

    private async void ToggleStarThread_Click(object sender, RoutedEventArgs e)
    {
        var threads = SelectedThreadsForAction();
        await ViewModel.SetThreadsStarredAsync(threads, threads.Any(thread => !thread.IsStarred));
        SyncFolderTree();
    }

    private async void JunkThread_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.MarkThreadsJunkAsync(SelectedThreadsForAction());
        SyncFolderTree();
        UpdateContentPanels();
    }

    private async void OpenConversation_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedThread is not { } thread)
        {
            return;
        }

        await ViewModel.SelectThreadAsync(thread);
        UpdateContentPanels();
    }

    private async void RestoreThread_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RestoreThreadsFromTrashAsync(SelectedThreadsForAction());
        SyncFolderTree();
        UpdateContentPanels();
    }

    private async void ToggleJunkThread_Click(object sender, RoutedEventArgs e)
    {
        var threads = SelectedThreadsForAction();
        var isSpam = ViewModel.SelectedFolder?.Id == "SPAM"
            || threads.All(thread => thread.LabelIds.Contains("SPAM", StringComparer.Ordinal));
        await ViewModel.MarkThreadsJunkAsync(threads, !isSpam);
        SyncFolderTree();
        UpdateContentPanels();
    }

    private async void OpenConversationInGmail_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedThread is not { } thread || !ViewModel.IsConnected)
        {
            return;
        }

        var account = Uri.EscapeDataString(ViewModel.AccountEmail);
        var threadId = Uri.EscapeDataString(thread.Id);
        await Windows.System.Launcher.LaunchUriAsync(
            new Uri($"https://mail.google.com/mail/u/{account}/#all/{threadId}"));
    }

    private void MoveMenu_Opening(object sender, object e)
    {
        if (sender is MenuFlyout menu)
        {
            PopulateMoveMenu(menu.Items);
        }
    }

    private void LabelsMenu_Opening(object sender, object e)
    {
        if (sender is MenuFlyout menu)
        {
            PopulateLabelsMenu(menu.Items);
        }
    }

    private void ThreadContextMenu_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout menu)
        {
            return;
        }

        var threads = SelectedThreadsForAction();
        var hasTarget = threadContextHasPointerTarget is not false && threads.Count > 0;
        var hasSingleTarget = hasTarget && threads.Count == 1;
        foreach (var item in menu.Items.OfType<MenuFlyoutItem>())
        {
            item.IsEnabled = hasTarget;
        }

        foreach (var submenu in menu.Items.OfType<MenuFlyoutSubItem>())
        {
            submenu.IsEnabled = hasTarget;
        }

        var thread = hasTarget ? threads[0] : null;
        var isInTrash = ViewModel.SelectedFolder?.Id == "TRASH"
            || (hasTarget && threads.All(item => item.LabelIds.Contains("TRASH", StringComparer.Ordinal)));
        var isSpam = ViewModel.SelectedFolder?.Id == "SPAM"
            || (hasTarget && threads.All(item => item.LabelIds.Contains("SPAM", StringComparer.Ordinal)));
        ContextOpenThreadItem.IsEnabled = hasSingleTarget;
        ContextReplyItem.IsEnabled = hasSingleTarget;
        ContextReplyAllItem.IsEnabled = hasSingleTarget;
        ContextForwardItem.IsEnabled = hasSingleTarget;
        ContextOpenGmailItem.IsEnabled = hasSingleTarget;
        ContextArchiveThreadItem.IsEnabled = threads.Any(item =>
            item.LabelIds.Contains("INBOX", StringComparer.Ordinal));
        ContextTrashThreadItem.Visibility = isInTrash ? Visibility.Collapsed : Visibility.Visible;
        ContextRestoreThreadItem.Visibility = isInTrash ? Visibility.Visible : Visibility.Collapsed;
        ContextReadThreadItem.Text = threads.Any(item => item.IsUnread) ? "Mark as read" : "Mark as unread";
        ContextStarThreadItem.Text = threads.Any(item => !item.IsStarred) ? "Add star" : "Remove star";
        ContextJunkThreadItem.Text = isSpam ? "Not spam" : "Mark as spam";
        ContextJunkThreadItem.IsEnabled = hasTarget && !isInTrash;
        ContextMoveMenu.IsEnabled = hasTarget && !isInTrash && !isSpam;

        foreach (var submenu in menu.Items.OfType<MenuFlyoutSubItem>())
        {
            if (submenu.Text == "Move to")
            {
                PopulateMoveMenu(submenu.Items);
            }
            else if (submenu.Text == "Labels")
            {
                PopulateLabelsMenu(submenu.Items);
            }
        }
    }

    private void ThreadContextMenu_Closed(object sender, object e) =>
        threadContextHasPointerTarget = null;

    private void PopulateMoveMenu(IList<MenuFlyoutItemBase> items)
    {
        items.Clear();
        if (ViewModel.SelectedFolder?.Id is "TRASH" or "SPAM")
        {
            items.Add(new MenuFlyoutItem
            {
                Text = ViewModel.SelectedFolder.Id == "TRASH"
                    ? "Restore the conversation before moving it"
                    : "Mark the conversation as not spam before moving it",
                IsEnabled = false
            });
            return;
        }

        foreach (var folder in ViewModel.Folders.Where(folder =>
                     folder.Id != ViewModel.SelectedFolder?.Id
                     && (!folder.IsSystem || folder.Id == "INBOX")))
        {
            var item = new MenuFlyoutItem { Text = folder.FullName, Tag = folder };
            item.Click += MoveToFolder_Click;
            items.Add(item);
        }

        if (items.Count == 0)
        {
            items.Add(new MenuFlyoutItem { Text = "No other folders", IsEnabled = false });
        }
    }

    private void PopulateLabelsMenu(IList<MenuFlyoutItemBase> items)
    {
        items.Clear();
        var threads = SelectedThreadsForAction();
        foreach (var folder in ViewModel.Folders.Where(folder => !folder.IsSystem))
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = folder.FullName,
                Tag = folder,
                IsChecked = threads.Count > 0
                    && threads.All(thread => thread.LabelIds.Contains(folder.Id, StringComparer.Ordinal))
            };
            item.Click += ToggleLabel_Click;
            items.Add(item);
        }

        if (items.Count == 0)
        {
            items.Add(new MenuFlyoutItem { Text = "No labels", IsEnabled = false });
        }
    }

    private async void MoveToFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: MailFolder folder })
        {
            await ViewModel.MoveThreadsAsync(SelectedThreadsForAction(), folder);
            SyncFolderTree();
            UpdateContentPanels();
        }
    }

    private async void ToggleLabel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem { Tag: MailFolder folder } item)
        {
            return;
        }

        if (item.IsChecked)
        {
            await ViewModel.AddLabelToThreadsAsync(SelectedThreadsForAction(), folder);
        }
        else
        {
            await ViewModel.RemoveLabelFromThreadsAsync(SelectedThreadsForAction(), folder);
        }

        SyncFolderTree();
    }

    private void FolderResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        FolderSplitView.OpenPaneLength = Math.Clamp(
            FolderSplitView.OpenPaneLength + e.HorizontalChange,
            MinimumFolderWidth,
            MaximumFolderWidth);
    }

    private void FolderResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e) => SaveUiSettings();

    private void ResizeThumb_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        ApplyResizePointer(sender);

    private void ResizeThumb_PointerMoved(object sender, PointerRoutedEventArgs e) =>
        ApplyResizePointer(sender);

    private void ResizeThumb_PointerExited(object sender, PointerRoutedEventArgs e) =>
        SetCursor(LoadCursor(IntPtr.Zero, new IntPtr(ArrowCursorId)));

    private void ApplyResizePointer(object sender)
    {
        var cursorId = ReferenceEquals(sender, MessageResizeThumb)
            && readingPaneMode == ReadingPaneMode.Bottom
                ? SizeNorthSouthCursorId
                : SizeWestEastCursorId;
        SetCursor(LoadCursor(IntPtr.Zero, new IntPtr(cursorId)));
    }

    private void MessageResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (readingPaneMode == ReadingPaneMode.Right)
        {
            var maximum = Math.Max(MinimumMessageListWidth, MailWorkspace.ActualWidth - MinimumReadingPaneWidth - 6);
            messageListWidth = Math.Clamp(messageListWidth + e.HorizontalChange, MinimumMessageListWidth, maximum);
            ThreadColumn.Width = new GridLength(messageListWidth);
        }
        else if (readingPaneMode == ReadingPaneMode.Bottom)
        {
            var maximum = Math.Max(MinimumMessageListHeight, MailWorkspace.ActualHeight - MinimumReadingPaneHeight - 6);
            messageListHeight = Math.Clamp(messageListHeight + e.VerticalChange, MinimumMessageListHeight, maximum);
            ThreadRow.Height = new GridLength(messageListHeight);
        }
    }

    private void MessageResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e) => SaveUiSettings();

    private void ReadingPaneRight_Click(object sender, RoutedEventArgs e) => SetReadingPaneMode(ReadingPaneMode.Right);
    private void ReadingPaneBottom_Click(object sender, RoutedEventArgs e) => SetReadingPaneMode(ReadingPaneMode.Bottom);
    private void ReadingPaneOff_Click(object sender, RoutedEventArgs e) => SetReadingPaneMode(ReadingPaneMode.Off);
    private void ComfortableDensity_Click(object sender, RoutedEventArgs e) => SetDensity(MessageDensity.Comfortable);
    private void CompactDensity_Click(object sender, RoutedEventArgs e) => SetDensity(MessageDensity.Compact);

    private void SetReadingPaneMode(ReadingPaneMode mode)
    {
        readingPaneMode = mode;
        UpdateViewOptionChecks();
        ApplyReadingLayout();
        SaveUiSettings();
    }

    private void SetDensity(MessageDensity density)
    {
        messageDensity = density;
        ApplyDensity();
        UpdateViewOptionChecks();
        SaveUiSettings();
    }

    private void ApplyDensity()
    {
        ThreadList.ItemContainerStyle = (Style)Resources[messageDensity == MessageDensity.Compact
            ? "CompactThreadItemStyle"
            : "ComfortableThreadItemStyle"];
        ApplyRealizedThreadPresentation();
    }

    private void UpdateViewOptionChecks()
    {
        ReadingPaneRightItem.IsChecked = readingPaneMode == ReadingPaneMode.Right;
        ReadingPaneBottomItem.IsChecked = readingPaneMode == ReadingPaneMode.Bottom;
        ReadingPaneOffItem.IsChecked = readingPaneMode == ReadingPaneMode.Off;
        ComfortableDensityItem.IsChecked = messageDensity == MessageDensity.Comfortable;
        CompactDensityItem.IsChecked = messageDensity == MessageDensity.Compact;
        UpdateMessageListViewChecks();
    }

    private void ThreadPaneContextMenu_Opening(object sender, object e) => UpdateMessageListViewChecks();

    private void ToggleMessageListToolbar_Click(object sender, RoutedEventArgs e)
    {
        showMessageListToolbar = !showMessageListToolbar;
        ApplyMessageListViewOptions();
        SaveUiSettings();
    }

    private void GroupDate_Click(object sender, RoutedEventArgs e) => SetThreadGrouping(ThreadGrouping.Date);
    private void GroupSender_Click(object sender, RoutedEventArgs e) => SetThreadGrouping(ThreadGrouping.Sender);
    private void GroupNone_Click(object sender, RoutedEventArgs e) => SetThreadGrouping(ThreadGrouping.None);
    private void SortNewest_Click(object sender, RoutedEventArgs e) => SetThreadSort(ThreadSort.Newest);
    private void SortOldest_Click(object sender, RoutedEventArgs e) => SetThreadSort(ThreadSort.Oldest);
    private void SortSenderAsc_Click(object sender, RoutedEventArgs e) => SetThreadSort(ThreadSort.SenderAscending);
    private void SortSenderDesc_Click(object sender, RoutedEventArgs e) => SetThreadSort(ThreadSort.SenderDescending);
    private void SortSubjectAsc_Click(object sender, RoutedEventArgs e) => SetThreadSort(ThreadSort.SubjectAscending);
    private void SortSubjectDesc_Click(object sender, RoutedEventArgs e) => SetThreadSort(ThreadSort.SubjectDescending);

    private void TogglePreview_Click(object sender, RoutedEventArgs e)
    {
        showMessagePreview = !showMessagePreview;
        ApplyMessageListViewOptions();
        SaveUiSettings();
    }

    private void ToggleAvatars_Click(object sender, RoutedEventArgs e)
    {
        showSenderAvatars = !showSenderAvatars;
        ApplyMessageListViewOptions();
        SaveUiSettings();
    }

    private void SetThreadGrouping(ThreadGrouping grouping)
    {
        threadGrouping = grouping;
        RebuildThreadView();
        UpdateMessageListViewChecks();
        SaveUiSettings();
    }

    private void SetThreadSort(ThreadSort sort)
    {
        threadSort = sort;
        RebuildThreadView();
        UpdateMessageListViewChecks();
        SaveUiSettings();
    }

    private void ApplyMessageListViewOptions()
    {
        MessageListCommandBar.Visibility = showMessageListToolbar ? Visibility.Visible : Visibility.Collapsed;
        ApplyRealizedThreadPresentation();
        UpdateMessageListViewChecks();
    }

    private void UpdateMessageListViewChecks()
    {
        ShowListToolbarContextItem.IsChecked = showMessageListToolbar;
        ContextGroupDateItem.IsChecked = threadGrouping == ThreadGrouping.Date;
        ContextGroupSenderItem.IsChecked = threadGrouping == ThreadGrouping.Sender;
        ContextGroupNoneItem.IsChecked = threadGrouping == ThreadGrouping.None;
        ToolbarGroupDateItem.IsChecked = threadGrouping == ThreadGrouping.Date;
        ToolbarGroupSenderItem.IsChecked = threadGrouping == ThreadGrouping.Sender;
        ToolbarGroupNoneItem.IsChecked = threadGrouping == ThreadGrouping.None;
        ContextSortNewestItem.IsChecked = threadSort == ThreadSort.Newest;
        ContextSortOldestItem.IsChecked = threadSort == ThreadSort.Oldest;
        ContextSortSenderAscItem.IsChecked = threadSort == ThreadSort.SenderAscending;
        ContextSortSenderDescItem.IsChecked = threadSort == ThreadSort.SenderDescending;
        ContextSortSubjectAscItem.IsChecked = threadSort == ThreadSort.SubjectAscending;
        ContextSortSubjectDescItem.IsChecked = threadSort == ThreadSort.SubjectDescending;
        ToolbarSortNewestItem.IsChecked = threadSort == ThreadSort.Newest;
        ToolbarSortOldestItem.IsChecked = threadSort == ThreadSort.Oldest;
        ToolbarSortSenderAscItem.IsChecked = threadSort == ThreadSort.SenderAscending;
        ToolbarSortSenderDescItem.IsChecked = threadSort == ThreadSort.SenderDescending;
        ToolbarSortSubjectAscItem.IsChecked = threadSort == ThreadSort.SubjectAscending;
        ToolbarSortSubjectDescItem.IsChecked = threadSort == ThreadSort.SubjectDescending;
        ContextShowPreviewItem.IsChecked = showMessagePreview;
        ContextShowAvatarsItem.IsChecked = showSenderAvatars;
        ToolbarShowPreviewItem.IsChecked = showMessagePreview;
        ToolbarShowAvatarsItem.IsChecked = showSenderAvatars;
        SortThreadsButton.Label = threadSort switch
        {
            ThreadSort.Newest => "Newest",
            ThreadSort.Oldest => "Oldest",
            ThreadSort.SenderAscending => "Sender A–Z",
            ThreadSort.SenderDescending => "Sender Z–A",
            ThreadSort.SubjectAscending => "Subject A–Z",
            _ => "Subject Z–A"
        };
        GroupThreadsButton.Label = threadGrouping switch
        {
            ThreadGrouping.Date => "By date",
            ThreadGrouping.Sender => "By sender",
            _ => "No groups"
        };
    }

    private void RebuildThreadView()
    {
        if (isRebuildingThreadView)
        {
            return;
        }

        isRebuildingThreadView = true;
        var sorted = SortThreads(ViewModel.Threads).ToList();
        try
        {
            selectedThreadIds.IntersectWith(sorted.Select(thread => thread.Id));
            if (threadGrouping == ThreadGrouping.None)
            {
                ThreadList.ItemsSource = sorted;
            }
            else
            {
                threadGroups.Clear();
                if (threadGrouping == ThreadGrouping.Date)
                {
                    var dateGroups = sorted
                        .GroupBy(DateGroupTitle)
                        .Select(group => new
                        {
                            Title = group.Key,
                            Items = group.ToList(),
                            SortDate = threadSort == ThreadSort.Oldest
                                ? group.Min(thread => thread.ReceivedAt)
                                : group.Max(thread => thread.ReceivedAt)
                        });
                    dateGroups = threadSort == ThreadSort.Oldest
                        ? dateGroups.OrderBy(group => group.SortDate)
                        : dateGroups.OrderByDescending(group => group.SortDate);
                    foreach (var group in dateGroups)
                    {
                        var key = $"date:{group.Title}";
                        threadGroups.Add(new MailThreadGroup(
                            key,
                            $"{group.Title}  ·  {group.Items.Count}",
                            group.Items,
                            collapsedThreadGroups.Contains(key)));
                    }
                }
                else
                {
                    var senderGroups = sorted.GroupBy(thread => SenderGroupTitle(thread.Sender));
                    senderGroups = threadSort == ThreadSort.SenderDescending
                        ? senderGroups.OrderByDescending(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
                        : senderGroups.OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase);
                    foreach (var group in senderGroups)
                    {
                        var items = group.ToList();
                        var key = $"sender:{group.Key}";
                        threadGroups.Add(new MailThreadGroup(
                            key,
                            $"{group.Key}  ·  {items.Count}",
                            items,
                            collapsedThreadGroups.Contains(key)));
                    }
                }

                ThreadList.ItemsSource = threadGroupsSource.View;
            }

            ThreadList.SelectedItems.Clear();
            var visibleThreads = threadGrouping == ThreadGrouping.None
                ? sorted
                : threadGroups.SelectMany(group => group).ToList();
            foreach (var thread in visibleThreads.Where(thread => selectedThreadIds.Contains(thread.Id)))
            {
                ThreadList.SelectedItems.Add(thread);
            }
        }
        finally
        {
            isRebuildingThreadView = false;
        }

        UpdateThreadSelectionStatus();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ApplyRealizedThreadPresentation);
    }

    private void UpdateThreadSelectionStatus()
    {
        var count = ThreadList.SelectedItems.OfType<MailThreadSummary>().Count();
        ViewModel.SelectedThreadCount = count;
        ThreadSelectionStatus.Text = count == 1 ? "1 selected" : $"{count} selected";
        ThreadSelectionStatus.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private IReadOnlyList<MailThreadSummary> SelectedThreadsForAction()
    {
        var selected = ThreadList.SelectedItems
            .OfType<MailThreadSummary>()
            .DistinctBy(thread => thread.Id)
            .ToList();
        if (selected.Count == 0 && ViewModel.SelectedThread is { } current)
        {
            selected.Add(current);
        }

        return selected;
    }

    private IEnumerable<MailThreadSummary> SortThreads(IEnumerable<MailThreadSummary> threads) => threadSort switch
    {
        ThreadSort.Oldest => threads.OrderBy(thread => thread.ReceivedAt),
        ThreadSort.SenderAscending => threads.OrderBy(thread => thread.Sender, StringComparer.CurrentCultureIgnoreCase)
            .ThenByDescending(thread => thread.ReceivedAt),
        ThreadSort.SenderDescending => threads.OrderByDescending(thread => thread.Sender, StringComparer.CurrentCultureIgnoreCase)
            .ThenByDescending(thread => thread.ReceivedAt),
        ThreadSort.SubjectAscending => threads.OrderBy(thread => thread.Subject, StringComparer.CurrentCultureIgnoreCase)
            .ThenByDescending(thread => thread.ReceivedAt),
        ThreadSort.SubjectDescending => threads.OrderByDescending(thread => thread.Subject, StringComparer.CurrentCultureIgnoreCase)
            .ThenByDescending(thread => thread.ReceivedAt),
        _ => threads.OrderByDescending(thread => thread.ReceivedAt)
    };

    private static string DateGroupTitle(MailThreadSummary thread)
    {
        var date = thread.ReceivedAt.ToLocalTime().Date;
        var today = DateTimeOffset.Now.Date;
        if (date == today)
        {
            return "Today";
        }

        if (date == today.AddDays(-1))
        {
            return "Yesterday";
        }

        var startOfWeek = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        if (date >= startOfWeek)
        {
            return "This week";
        }

        if (date >= startOfWeek.AddDays(-7))
        {
            return "Last week";
        }

        return date.Year == today.Year
            ? date.ToString("MMMM")
            : date.ToString("MMMM yyyy");
    }

    private static string SenderGroupTitle(string sender)
    {
        var bracket = sender.IndexOf('<');
        var display = (bracket > 0 ? sender[..bracket] : sender).Trim(' ', '"');
        return string.IsNullOrWhiteSpace(display) ? "Unknown sender" : display;
    }

    private void ThreadList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.ItemContainer is ListViewItem container)
        {
            ApplyThreadPresentation(container);
        }
    }

    private void ApplyRealizedThreadPresentation()
    {
        for (var index = 0; index < ThreadList.Items.Count; index++)
        {
            if (ThreadList.ContainerFromIndex(index) is ListViewItem container)
            {
                ApplyThreadPresentation(container);
            }
        }
    }

    private void ApplyThreadPresentation(ListViewItem container)
    {
        container.MinHeight = messageDensity == MessageDensity.Compact
            ? 64
            : showMessagePreview ? 92 : 72;
        SetTaggedPresentation(container);
        SetThreadSelectionBox(container);
    }

    private void ApplyRealizedThreadSelectionBoxes()
    {
        isUpdatingThreadSelectionBoxes = true;
        try
        {
            for (var index = 0; index < ThreadList.Items.Count; index++)
            {
                if (ThreadList.ContainerFromIndex(index) is ListViewItem container)
                {
                    SetThreadSelectionBox(container);
                }
            }
        }
        finally
        {
            isUpdatingThreadSelectionBoxes = false;
        }
    }

    private void SetThreadSelectionBox(ListViewItem container)
    {
        if (container.DataContext is not MailThreadSummary thread
            || FindDescendant<CheckBox>(container) is not { } checkBox)
        {
            return;
        }

        var wasUpdating = isUpdatingThreadSelectionBoxes;
        isUpdatingThreadSelectionBoxes = true;
        checkBox.IsChecked = selectedThreadIds.Contains(thread.Id);
        isUpdatingThreadSelectionBoxes = wasUpdating;
    }

    private void SetTaggedPresentation(DependencyObject parent)
    {
        if (parent is FrameworkElement { Tag: string tag } element)
        {
            if (tag == "ThreadPreview")
            {
                element.Visibility = showMessagePreview ? Visibility.Visible : Visibility.Collapsed;
            }
            else if (tag == "ThreadAvatar")
            {
                element.Visibility = showSenderAvatars ? Visibility.Visible : Visibility.Collapsed;
            }
            else if (tag == "ThreadLayout" && element is Grid grid && grid.ColumnDefinitions.Count > 1)
            {
                grid.ColumnDefinitions[1].Width = showSenderAvatars ? new GridLength(38) : new GridLength(0);
            }
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            SetTaggedPresentation(VisualTreeHelper.GetChild(parent, index));
        }
    }

    private void CompactBack_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedThread = null;
        ViewModel.SelectedThreadDetail = null;
        ThreadList.SelectedItem = null;
        ApplyReadingLayout();
        UpdateContentPanels();
    }

    private void ApplyReadingLayout()
    {
        ResetWorkspacePlacement();
        if (isCompact)
        {
            var showReader = readingPaneMode != ReadingPaneMode.Off
                && (!ViewModel.IsConnected || ViewModel.SelectedThread is not null);
            ThreadColumn.Width = showReader ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            DividerColumn.Width = new GridLength(0);
            ReadingColumn.Width = showReader ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            ThreadRow.Height = new GridLength(1, GridUnitType.Star);
            DividerRow.Height = new GridLength(0);
            ReadingRow.Height = new GridLength(0);
            ThreadListPane.Visibility = showReader ? Visibility.Collapsed : Visibility.Visible;
            ReadingPane.Visibility = showReader ? Visibility.Visible : Visibility.Collapsed;
            MessageResizeThumb.Visibility = Visibility.Collapsed;
            CompactBackButton.Visibility = showReader && ViewModel.IsConnected
                ? Visibility.Visible
                : Visibility.Collapsed;
            return;
        }

        CompactBackButton.Visibility = Visibility.Collapsed;
        ThreadListPane.Visibility = Visibility.Visible;
        if (readingPaneMode == ReadingPaneMode.Off)
        {
            ThreadColumn.Width = new GridLength(1, GridUnitType.Star);
            DividerColumn.Width = new GridLength(0);
            ReadingColumn.Width = new GridLength(0);
            ThreadRow.Height = new GridLength(1, GridUnitType.Star);
            DividerRow.Height = new GridLength(0);
            ReadingRow.Height = new GridLength(0);
            ReadingPane.Visibility = Visibility.Collapsed;
            MessageResizeThumb.Visibility = Visibility.Collapsed;
            return;
        }

        ReadingPane.Visibility = Visibility.Visible;
        MessageResizeThumb.Visibility = Visibility.Visible;
        if (readingPaneMode == ReadingPaneMode.Bottom)
        {
            Grid.SetColumn(ThreadListPane, 0);
            Grid.SetColumnSpan(ThreadListPane, 3);
            Grid.SetRow(ThreadListPane, 0);
            Grid.SetColumn(MessageResizeThumb, 0);
            Grid.SetColumnSpan(MessageResizeThumb, 3);
            Grid.SetRow(MessageResizeThumb, 1);
            Grid.SetColumn(ReadingPane, 0);
            Grid.SetColumnSpan(ReadingPane, 3);
            Grid.SetRow(ReadingPane, 2);
            ThreadColumn.Width = new GridLength(1, GridUnitType.Star);
            DividerColumn.Width = new GridLength(0);
            ReadingColumn.Width = new GridLength(0);
            ThreadRow.Height = new GridLength(Math.Max(MinimumMessageListHeight, messageListHeight));
            DividerRow.Height = new GridLength(6);
            ReadingRow.Height = new GridLength(1, GridUnitType.Star);
            return;
        }

        ThreadColumn.Width = new GridLength(Math.Max(MinimumMessageListWidth, messageListWidth));
        DividerColumn.Width = new GridLength(6);
        ReadingColumn.Width = new GridLength(1, GridUnitType.Star);
        ThreadRow.Height = new GridLength(1, GridUnitType.Star);
        DividerRow.Height = new GridLength(0);
        ReadingRow.Height = new GridLength(0);
    }

    private void ResetWorkspacePlacement()
    {
        Grid.SetRow(ThreadListPane, 0);
        Grid.SetColumn(ThreadListPane, 0);
        Grid.SetColumnSpan(ThreadListPane, 1);
        Grid.SetRow(MessageResizeThumb, 0);
        Grid.SetColumn(MessageResizeThumb, 1);
        Grid.SetColumnSpan(MessageResizeThumb, 1);
        Grid.SetRow(ReadingPane, 0);
        Grid.SetColumn(ReadingPane, 2);
        Grid.SetColumnSpan(ReadingPane, 1);
    }

    private void UpdateContentPanels()
    {
        var detail = ViewModel.SelectedThreadDetail;
        var hasDetail = ViewModel.IsConnected && detail is not null;
        if (!string.Equals(renderedThreadId, detail?.Id, StringComparison.Ordinal))
        {
            renderedThreadId = detail?.Id;
            showRemoteMessageContent = false;
        }

        ReadingTitle.Text = !ViewModel.IsConnected
            ? "Connect your Gmail account"
            : detail?.Subject ?? "Select a conversation";
        ReadingSubtitle.Text = !ViewModel.IsConnected
            ? "Sign in securely with your Google account."
            : detail is null
                ? "Messages render here with their original formatting."
                : "HTML formatting is preserved in a protected reading surface; scripts and unsafe navigation are disabled.";
        OnboardingPanel.Visibility = ViewModel.IsConnected ? Visibility.Collapsed : Visibility.Visible;
        EmptySelectionPanel.Visibility = ViewModel.IsConnected && detail is null
            ? Visibility.Visible
            : Visibility.Collapsed;
        MessageContentHost.Visibility = hasDetail
            ? Visibility.Visible
            : Visibility.Collapsed;
        ReadingCommandBar.Visibility = hasDetail
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (hasDetail)
        {
            _ = RenderSelectedThreadAsync(detail!);
        }
    }

    private async Task RenderSelectedThreadAsync(MailThreadDetail detail)
    {
        try
        {
            if (!messageWebViewInitialized)
            {
                await MessageWebView.EnsureCoreWebView2Async();
                var settings = MessageWebView.CoreWebView2.Settings;
                settings.IsScriptEnabled = false;
                settings.IsWebMessageEnabled = false;
                settings.AreDefaultScriptDialogsEnabled = false;
                settings.AreDevToolsEnabled = false;
                settings.IsStatusBarEnabled = true;
                MessageWebView.CoreWebView2.NavigationStarting += MessageWebView_NavigationStarting;
                MessageWebView.CoreWebView2.NewWindowRequested += MessageWebView_NewWindowRequested;
                messageWebViewInitialized = true;
            }

            if (ViewModel.SelectedThreadDetail?.Id != detail.Id)
            {
                return;
            }

            RemoteContentBanner.Visibility = MailHtmlRenderer.ContainsRemoteContent(detail)
                && !showRemoteMessageContent
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            MessageTextFallback.Visibility = Visibility.Collapsed;
            MessageWebView.Visibility = Visibility.Visible;
            MessageWebView.NavigateToString(MailHtmlRenderer.BuildDocument(detail, showRemoteMessageContent));
        }
        catch (Exception ex)
        {
            MessageWebView.Visibility = Visibility.Collapsed;
            RemoteContentBanner.Visibility = Visibility.Collapsed;
            MessageTextFallback.Visibility = Visibility.Visible;
            MessageTextFallbackContent.Text = string.Join(
                $"{Environment.NewLine}{Environment.NewLine}────────────────{Environment.NewLine}{Environment.NewLine}",
                detail.Messages.Select(message =>
                    $"{message.From}{Environment.NewLine}To: {message.To}{Environment.NewLine}{message.SentText}{Environment.NewLine}{Environment.NewLine}{message.BodyText}"));
            ReadingSubtitle.Text = $"HTML rendering was unavailable, so GLook is showing safe text. {ex.GetBaseException().Message}";
        }
    }

    private void ShowRemoteContent_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedThreadDetail is not { } detail)
        {
            return;
        }

        showRemoteMessageContent = true;
        _ = RenderSelectedThreadAsync(detail);
    }

    private async void MessageWebView_NavigationStarting(
        CoreWebView2 sender,
        CoreWebView2NavigationStartingEventArgs args)
    {
        if (args.Uri.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        args.Cancel = true;
        await OpenExternalMessageLinkAsync(args.Uri);
    }

    private async void MessageWebView_NewWindowRequested(
        CoreWebView2 sender,
        CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        await OpenExternalMessageLinkAsync(args.Uri);
    }

    private static async Task OpenExternalMessageLinkAsync(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https" or "mailto"))
        {
            return;
        }

        await Windows.System.Launcher.LaunchUriAsync(uri);
    }

    private void SyncFolderSelection()
    {
        FolderTree.SelectedNode = ViewModel.SelectedFolder is null
            ? null
            : FindFolderNode(FolderTree.RootNodes, ViewModel.SelectedFolder.Id);
    }

    private void SyncFolderTree()
    {
        contextFolderNode = null;
        var previousOffset = FindDescendant<ScrollViewer>(FolderTree)?.VerticalOffset ?? 0;
        var expandedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CaptureExpandedPaths(FolderTree.RootNodes, expandedPaths);
        var isInitialTree = FolderTree.RootNodes.Count == 0;

        FolderTree.RootNodes.Clear();
        foreach (var item in BuildFolderTreeItems())
        {
            FolderTree.RootNodes.Add(BuildFolderNode(item, expandedPaths, isInitialTree, 0));
        }

        SyncFolderSelection();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            FolderTree.UpdateLayout();
            FindDescendant<ScrollViewer>(FolderTree)?.ChangeView(null, previousOffset, null, true);
        });
    }

    private IReadOnlyList<FolderTreeItem> BuildFolderTreeItems()
    {
        var roots = ViewModel.Folders
            .Where(folder => folder.IsSystem)
            .Select(folder => new FolderTreeItem(folder.Name, folder.FullName, folder))
            .ToList();
        var userRoots = new List<FolderTreeItem>();
        var byPath = new Dictionary<string, FolderTreeItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in ViewModel.Folders
                     .Where(folder => !folder.IsSystem)
                     .OrderBy(folder => folder.FullName, StringComparer.CurrentCultureIgnoreCase))
        {
            var parts = folder.FullName.Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            FolderTreeItem? parent = null;

            for (var index = 0; index < parts.Length; index++)
            {
                var path = string.Join('/', parts.Take(index + 1));
                if (!byPath.TryGetValue(path, out var current))
                {
                    current = new FolderTreeItem(parts[index], path);
                    byPath.Add(path, current);
                    if (parent is null)
                    {
                        userRoots.Add(current);
                    }
                    else
                    {
                        parent.Children.Add(current);
                    }
                }

                if (index == parts.Length - 1)
                {
                    current.Folder = folder;
                }

                parent = current;
            }
        }

        SortFolderChildren(userRoots);
        roots.AddRange(userRoots);
        return roots;
    }

    private static void SortFolderChildren(List<FolderTreeItem> items)
    {
        items.Sort((left, right) =>
            StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name));
        foreach (var item in items)
        {
            SortFolderChildren(item.Children);
        }
    }

    private static TreeViewNode BuildFolderNode(
        FolderTreeItem item,
        ISet<string> expandedPaths,
        bool isInitialTree,
        int depth)
    {
        var node = new TreeViewNode { Content = item };
        foreach (var child in item.Children)
        {
            node.Children.Add(BuildFolderNode(child, expandedPaths, isInitialTree, depth + 1));
        }

        node.IsExpanded = expandedPaths.Contains(item.FullName)
            || (isInitialTree && depth == 0 && node.HasChildren);
        return node;
    }

    private static void CaptureExpandedPaths(
        IEnumerable<TreeViewNode> nodes,
        ISet<string> expandedPaths)
    {
        foreach (var node in nodes)
        {
            if (node.IsExpanded && node.Content is FolderTreeItem item)
            {
                expandedPaths.Add(item.FullName);
            }

            CaptureExpandedPaths(node.Children, expandedPaths);
        }
    }

    private static TreeViewNode? FindFolderNode(IEnumerable<TreeViewNode> nodes, string folderId)
    {
        foreach (var node in nodes)
        {
            if (node.Content is FolderTreeItem { Folder: { } folder } && folder.Id == folderId)
            {
                return node;
            }

            var child = FindFolderNode(node.Children, folderId);
            if (child is not null)
            {
                return child;
            }
        }

        return null;
    }

    private FolderTreeItem? GetContextFolderItem() =>
        (contextFolderNode ?? FolderTree.SelectedNode)?.Content as FolderTreeItem;

    private static void SetFolderExpansion(IEnumerable<TreeViewNode> nodes, bool isExpanded)
    {
        foreach (var node in nodes)
        {
            SetFolderExpansion(node, isExpanded);
        }
    }

    private static void SetFolderExpansion(TreeViewNode node, bool isExpanded)
    {
        node.IsExpanded = isExpanded;
        foreach (var child in node.Children)
        {
            SetFolderExpansion(child, isExpanded);
        }
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match)
            {
                return match;
            }

            child = VisualTreeHelper.GetParent(child);
        }

        return null;
    }

    private void LoadUiSettings()
    {
        try
        {
            if (!File.Exists(uiSettingsPath))
            {
                return;
            }

            var settings = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(uiSettingsPath));
            if (settings is null)
            {
                return;
            }

            readingPaneMode = Enum.TryParse<ReadingPaneMode>(settings.ReadingPane, true, out var paneMode)
                ? paneMode
                : ReadingPaneMode.Right;
            messageDensity = Enum.TryParse<MessageDensity>(settings.MessageDensity, true, out var density)
                ? density
                : MessageDensity.Comfortable;
            threadGrouping = Enum.TryParse<ThreadGrouping>(settings.ThreadGrouping, true, out var grouping)
                ? grouping
                : ThreadGrouping.Date;
            threadSort = Enum.TryParse<ThreadSort>(settings.ThreadSort, true, out var sort)
                ? sort
                : ThreadSort.Newest;
            showMessageListToolbar = settings.ShowMessageListToolbar;
            showMessagePreview = settings.ShowMessagePreview;
            showSenderAvatars = settings.ShowSenderAvatars;
            autoSyncEnabled = settings.AutoSyncEnabled;
            autoSyncIntervalMinutes = Math.Clamp(
                settings.AutoSyncIntervalMinutes <= 0
                    ? DefaultAutoSyncIntervalMinutes
                    : settings.AutoSyncIntervalMinutes,
                MinimumAutoSyncIntervalMinutes,
                MaximumAutoSyncIntervalMinutes);
            FolderSplitView.OpenPaneLength = Math.Clamp(settings.FolderWidth, MinimumFolderWidth, MaximumFolderWidth);
            messageListWidth = Math.Max(MinimumMessageListWidth, settings.MessageListWidth);
            messageListHeight = Math.Max(MinimumMessageListHeight, settings.MessageListHeight);
        }
        catch
        {
            // Invalid or older UI settings fall back to the safe defaults above.
        }
    }

    private void SaveUiSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(uiSettingsPath)!);
            var settings = new UiSettings
            {
                ReadingPane = readingPaneMode.ToString(),
                MessageDensity = messageDensity.ToString(),
                ThreadGrouping = threadGrouping.ToString(),
                ThreadSort = threadSort.ToString(),
                ShowMessageListToolbar = showMessageListToolbar,
                ShowMessagePreview = showMessagePreview,
                ShowSenderAvatars = showSenderAvatars,
                AutoSyncEnabled = autoSyncEnabled,
                AutoSyncIntervalMinutes = autoSyncIntervalMinutes,
                FolderWidth = FolderSplitView.OpenPaneLength,
                MessageListWidth = messageListWidth,
                MessageListHeight = messageListHeight
            };
            File.WriteAllText(uiSettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
        catch
        {
            // A locked settings file must never interrupt mail work.
        }
    }

    private void NewEmail_AcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        NewEmail_Click(this, new RoutedEventArgs());
    }

    private void Reply_AcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Reply_Click(this, new RoutedEventArgs());
    }

    private void ReplyAll_AcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ReplyAll_Click(this, new RoutedEventArgs());
    }

    private void Forward_AcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Forward_Click(this, new RoutedEventArgs());
    }

    private void Search_AcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SearchBox.Focus(FocusState.Keyboard);
    }

    private void Sync_AcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Refresh_Click(this, new RoutedEventArgs());
    }

    private void SyncAll_AcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SyncAll_Click(this, new RoutedEventArgs());
    }

    private void Delete_AcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.SelectedThread is null)
        {
            return;
        }

        args.Handled = true;
        DeleteThread_Click(this, new RoutedEventArgs());
    }

    private void StartAutoSyncTimer()
    {
        autoSyncTimer?.Stop();
        if (!autoSyncEnabled)
        {
            return;
        }

        autoSyncTimer ??= DispatcherQueue.CreateTimer();
        autoSyncTimer.Interval = TimeSpan.FromMinutes(autoSyncIntervalMinutes);
        autoSyncTimer.IsRepeating = true;
        autoSyncTimer.Tick -= AutoSyncTimer_Tick;
        autoSyncTimer.Tick += AutoSyncTimer_Tick;
        autoSyncTimer.Start();
    }

    private void UpdateSyncProgressIndicator()
    {
        var visibility = ViewModel.IsSyncingAll
            ? Visibility.Visible
            : Visibility.Collapsed;
        SyncAllProgressBar.Visibility = visibility;
        CancelSyncAllButton.Visibility = visibility;
    }

    private async void AutoSyncTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (autoSyncInProgress || ViewModel.IsBusy || !ViewModel.IsConnected)
        {
            return;
        }

        autoSyncInProgress = true;
        try
        {
            await ViewModel.RefreshAsync();
            SyncFolderTree();
            UpdateContentPanels();
            await PollForNewMailAsync();
        }
        finally
        {
            autoSyncInProgress = false;
        }
    }

    private async Task PollForNewMailAsync()
    {
        if (notificationPollInProgress || ViewModel.IsBusy || !ViewModel.IsConnected)
        {
            return;
        }

        notificationPollInProgress = true;
        try
        {
            var unread = await ViewModel.GetUnreadInboxSampleAsync();
            var currentIds = unread.Select(thread => thread.Id).ToHashSet(StringComparer.Ordinal);
            if (notificationBaselineEstablished && Application.Current is App app)
            {
                foreach (var thread in unread
                             .Where(thread => !knownUnreadInboxThreads.Contains(thread.Id))
                             .OrderBy(thread => thread.ReceivedAt)
                             .TakeLast(3))
                {
                    app.NotificationService?.ShowNewMail(thread);
                }
            }

            knownUnreadInboxThreads.Clear();
            knownUnreadInboxThreads.UnionWith(currentIds);
            notificationBaselineEstablished = true;
        }
        catch
        {
            // The regular sync status remains the user-facing source for network errors.
        }
        finally
        {
            notificationPollInProgress = false;
        }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "Close"
        };
        await dialog.ShowAsync();
    }

    private enum ReadingPaneMode
    {
        Right,
        Bottom,
        Off
    }

    private enum MessageDensity
    {
        Comfortable,
        Compact
    }

    private enum ThreadGrouping
    {
        Date,
        Sender,
        None
    }

    private enum ThreadSort
    {
        Newest,
        Oldest,
        SenderAscending,
        SenderDescending,
        SubjectAscending,
        SubjectDescending
    }

    private sealed class UiSettings
    {
        public string ReadingPane { get; set; } = nameof(ReadingPaneMode.Right);
        public string MessageDensity { get; set; } = nameof(MainPage.MessageDensity.Comfortable);
        public string ThreadGrouping { get; set; } = nameof(MainPage.ThreadGrouping.Date);
        public string ThreadSort { get; set; } = nameof(MainPage.ThreadSort.Newest);
        public bool ShowMessageListToolbar { get; set; } = true;
        public bool ShowMessagePreview { get; set; } = true;
        public bool ShowSenderAvatars { get; set; } = true;
        public bool AutoSyncEnabled { get; set; } = true;
        public int AutoSyncIntervalMinutes { get; set; } = DefaultAutoSyncIntervalMinutes;
        public double FolderWidth { get; set; } = 252;
        public double MessageListWidth { get; set; } = 390;
        public double MessageListHeight { get; set; } = 330;
    }

    private sealed class SignatureDraft : INotifyPropertyChanged
    {
        private string displayName;
        private string text;

        public SignatureDraft(MailSignature signature)
        {
            Id = string.IsNullOrWhiteSpace(signature.Id) ? signature.EmailAddress : signature.Id;
            EmailAddress = signature.EmailAddress;
            displayName = signature.DisplayName;
            Html = signature.Html;
            text = signature.Text;
            IsPrimary = signature.IsPrimary;
            IsDefault = signature.IsDefault;
            IsGmailBacked = signature.IsGmailBacked;
        }

        private SignatureDraft(string id, string name)
        {
            Id = id;
            EmailAddress = string.Empty;
            displayName = name;
            Html = string.Empty;
            text = string.Empty;
            IsGmailBacked = false;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Id { get; }
        public string EmailAddress { get; }
        public string Html { get; }
        public bool IsPrimary { get; }
        public bool IsDefault { get; }
        public bool IsGmailBacked { get; }

        public string DisplayName
        {
            get => displayName;
            set
            {
                if (displayName == value)
                {
                    return;
                }

                displayName = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayLabel)));
            }
        }

        public string Text
        {
            get => text;
            set => text = value;
        }

        public string DisplayLabel => IsGmailBacked
            ? string.IsNullOrWhiteSpace(DisplayName)
                ? EmailAddress
                : $"{DisplayName} <{EmailAddress}>"
            : $"{(string.IsNullOrWhiteSpace(DisplayName) ? "Unnamed signature" : DisplayName)} (GLook only)";

        public static SignatureDraft CreateLocal(string name) =>
            new($"local:{Guid.NewGuid():N}", name);

        public MailSignature ToMailSignature() =>
            new(
                EmailAddress,
                DisplayName.Trim(),
                Html,
                Text.Trim(),
                IsPrimary,
                IsDefault)
            {
                Id = Id,
                IsGmailBacked = IsGmailBacked
            };
    }

    private sealed class SignatureChoice : INotifyPropertyChanged
    {
        private string label;

        public SignatureChoice(string? emailAddress, string label)
        {
            EmailAddress = emailAddress;
            this.label = label;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string? EmailAddress { get; }

        public string Label
        {
            get => label;
            set
            {
                if (label == value)
                {
                    return;
                }

                label = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);

}
