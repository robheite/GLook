using System.Collections.ObjectModel;
using GLook.Models;
using GLook.Services;
using Microsoft.UI.Xaml.Automation;

namespace GLook.Views;

public sealed partial class BoardView : UserControl
{
    private readonly List<BoardCardItem> cards = [];
    private bool suppressMailboxSelection;

    public BoardView()
    {
        InitializeComponent();
    }

    public ObservableCollection<BoardMailboxOption> Mailboxes { get; } = [];

    public ObservableCollection<BoardLaneViewModel> Lanes { get; } = [];

    public BoardSettingsStore? SettingsStore { get; set; }

    public BoardSettingsSnapshot Configuration { get; private set; } = BoardSettingsSnapshot.Empty;

    public BoardDefinition? ActiveBoard { get; private set; }

    public Guid? SelectedAccountId => ActiveBoard?.SelectedMailbox?.AccountId;

    public event EventHandler? ManageBoardRequested;

    public event EventHandler<BoardMailboxChangedEventArgs>? MailboxChanged;

    public event EventHandler<BoardColumnRenamedEventArgs>? ColumnRenamed;

    public event EventHandler<BoardColumnBindingRequestedEventArgs>? ColumnBindingRequested;

    public event EventHandler<BoardCardRequestedEventArgs>? CardOpenRequested;

    public event EventHandler<BoardCardActionRequestedEventArgs>? CardActionRequested;

    public async Task LoadAsync(Guid? boardId = null, CancellationToken cancellationToken = default)
    {
        if (SettingsStore is null)
        {
            throw new InvalidOperationException("Assign a BoardSettingsStore before loading a board.");
        }

        var snapshot = await SettingsStore.LoadAsync(cancellationToken);
        if (snapshot.Boards.Count == 0)
        {
            (snapshot, _) = BoardConfigurationEditor.AddDefaultBoard(snapshot);
            await SettingsStore.SaveAsync(snapshot, cancellationToken);
        }

        SetConfiguration(snapshot, boardId ?? snapshot.Boards[0].BoardId);
    }

    public void SetConfiguration(BoardSettingsSnapshot snapshot, Guid boardId)
    {
        BoardConfigurationValidator.Validate(snapshot);
        var board = snapshot.Boards.FirstOrDefault(candidate => candidate.BoardId == boardId)
            ?? throw new KeyNotFoundException($"Board {boardId} was not found.");
        Configuration = snapshot;
        ActiveBoard = board;
        SelectMailboxInControl(board.SelectedMailbox?.AccountId);
        RebuildLanes();
    }

    public async Task SetMailboxesAsync(
        IEnumerable<BoardMailboxOption> mailboxes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mailboxes);
        var options = mailboxes.ToArray();
        if (options.Any(option => option.AccountId == Guid.Empty)
            || options.Select(option => option.AccountId).Distinct().Count() != options.Length)
        {
            throw new ArgumentException("Every mailbox must have a unique, non-empty account ID.", nameof(mailboxes));
        }

        suppressMailboxSelection = true;
        try
        {
            Mailboxes.Clear();
            foreach (var mailbox in options)
            {
                Mailboxes.Add(mailbox);
            }
        }
        finally
        {
            suppressMailboxSelection = false;
        }

        if (ActiveBoard is null)
        {
            return;
        }

        var selectedAccountId = ActiveBoard.SelectedMailbox?.AccountId;
        var availableSelection = options.FirstOrDefault(option => option.AccountId == selectedAccountId);
        var nextSelection = availableSelection ?? options.FirstOrDefault();
        if (nextSelection is null)
        {
            if (selectedAccountId is not null)
            {
                await ApplyConfigurationAsync(
                    BoardConfigurationEditor.ClearSelectedMailbox(Configuration, ActiveBoard.BoardId),
                    cancellationToken);
            }

            SelectMailboxInControl(null);
            RebuildLanes();
            return;
        }

        if (selectedAccountId != nextSelection.AccountId)
        {
            await ApplyConfigurationAsync(
                BoardConfigurationEditor.RememberSelectedMailbox(
                    Configuration,
                    ActiveBoard.BoardId,
                    nextSelection.AccountId),
                cancellationToken);
        }

        SelectMailboxInControl(nextSelection.AccountId);
        RebuildLanes();
    }

    public void SetCards(IEnumerable<BoardCardItem> boardCards)
    {
        ArgumentNullException.ThrowIfNull(boardCards);
        cards.Clear();
        cards.AddRange(boardCards);
        RebuildLanes();
    }

    public void AnnounceStatus(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        BoardStatusText.Text = message;
    }

    private async void MailboxSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressMailboxSelection
            || ActiveBoard is null
            || MailboxSelector.SelectedItem is not BoardMailboxOption mailbox
            || ActiveBoard.SelectedMailbox?.AccountId == mailbox.AccountId)
        {
            return;
        }

        var prior = Configuration;
        var boardId = ActiveBoard.BoardId;
        try
        {
            var updated = BoardConfigurationEditor.RememberSelectedMailbox(
                Configuration,
                boardId,
                mailbox.AccountId);
            await ApplyConfigurationAsync(updated, CancellationToken.None);
        }
        catch (Exception exception)
        {
            Configuration = prior;
            ActiveBoard = prior.Boards.FirstOrDefault(board => board.BoardId == boardId);
            SelectMailboxInControl(ActiveBoard?.SelectedMailbox?.AccountId);
            AnnounceStatus($"Could not change mailbox: {exception.Message}");
            return;
        }

        RebuildLanes();
        AnnounceStatus($"Showing {mailbox.DisplayText} on {ActiveBoard.Name}.");
        MailboxChanged?.Invoke(this, new BoardMailboxChangedEventArgs(boardId, mailbox.AccountId));
    }

    private void ManageBoard_Click(object sender, RoutedEventArgs e) =>
        ManageBoardRequested?.Invoke(this, EventArgs.Empty);

    private async void RenameColumn_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveBoard is null
            || sender is not Button { CommandParameter: BoardLaneViewModel lane })
        {
            return;
        }

        var titleBox = new TextBox
        {
            Header = "Column name",
            Text = lane.Definition.DisplayTitle,
            MaxLength = BoardConfigurationValidator.MaximumColumnTitleLength,
            SelectionStart = 0,
            SelectionLength = lane.Definition.DisplayTitle.Length,
        };
        AutomationProperties.SetName(titleBox, "Board column name");

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Rename board column",
            Content = titleBox,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        string renamedTitle;
        try
        {
            var updated = BoardConfigurationEditor.RenameColumn(
                Configuration,
                ActiveBoard.BoardId,
                lane.Definition.ColumnId,
                titleBox.Text);
            await ApplyConfigurationAsync(updated, CancellationToken.None);
            RebuildLanes();
            renamedTitle = ActiveBoard.Columns
                .Single(column => column.ColumnId == lane.Definition.ColumnId)
                .DisplayTitle;
        }
        catch (Exception exception)
        {
            AnnounceStatus($"Could not rename column: {exception.Message}");
            return;
        }

        AnnounceStatus($"Column renamed to {renamedTitle}.");
        ColumnRenamed?.Invoke(
            this,
            new BoardColumnRenamedEventArgs(ActiveBoard.BoardId, lane.Definition.ColumnId, renamedTitle));
    }

    private void ChooseLabel_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveBoard is null
            || SelectedAccountId is not Guid accountId
            || sender is not Button { CommandParameter: BoardLaneViewModel lane })
        {
            return;
        }

        ColumnBindingRequested?.Invoke(
            this,
            new BoardColumnBindingRequestedEventArgs(
                ActiveBoard.BoardId,
                accountId,
                lane.Definition.ColumnId));
    }

    private void CardList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is BoardCardItem card)
        {
            CardOpenRequested?.Invoke(this, new BoardCardRequestedEventArgs(card));
        }
    }

    private void MoveCard_Click(object sender, RoutedEventArgs e) =>
        ShowCardActionMenu(sender as Button, BoardDragBehavior.Move);

    private void AddLabelCard_Click(object sender, RoutedEventArgs e) =>
        ShowCardActionMenu(sender as Button, BoardDragBehavior.AddLabel);

    private void ShowCardActionMenu(Button? button, BoardDragBehavior behavior)
    {
        if (button?.CommandParameter is not BoardCardItem card || ActiveBoard is null)
        {
            return;
        }

        var binding = GetSelectedAccountBinding();
        var sourceBinding = binding?.ColumnBindings.FirstOrDefault(candidate =>
            candidate.ColumnId == card.ColumnId);
        if (sourceBinding is null)
        {
            AnnounceStatus("Map this column to a Gmail label before changing the conversation.");
            return;
        }

        var targetColumns = ActiveBoard.Columns
            .Where(column => column.ColumnId != card.ColumnId)
            .Select(column => new
            {
                Column = column,
                Binding = binding?.ColumnBindings.FirstOrDefault(candidate =>
                    candidate.ColumnId == column.ColumnId),
            })
            .Where(target => target.Binding is not null)
            .OrderBy(target => target.Column.Position)
            .ToArray();
        if (targetColumns.Length == 0)
        {
            AnnounceStatus("Map another board column before moving or labeling this conversation.");
            return;
        }

        var flyout = new MenuFlyout();
        foreach (var target in targetColumns)
        {
            var item = new MenuFlyoutItem
            {
                Text = target.Column.DisplayTitle,
                Tag = new CardActionTarget(
                    card,
                    sourceBinding.GmailLabelId,
                    target.Column.ColumnId,
                    target.Binding!.GmailLabelId,
                    behavior),
            };
            item.Click += CardActionTarget_Click;
            flyout.Items.Add(item);
        }

        flyout.ShowAt(button);
    }

    private void CardActionTarget_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: CardActionTarget target })
        {
            return;
        }

        CardActionRequested?.Invoke(
            this,
            new BoardCardActionRequestedEventArgs(
                target.Card,
                target.Card.ColumnId,
                target.SourceGmailLabelId,
                target.TargetColumnId,
                target.TargetGmailLabelId,
                target.Behavior));
        var action = target.Behavior == BoardDragBehavior.Move ? "Move" : "Add label";
        AnnounceStatus($"{action} requested for {target.Card.Subject}.");
    }

    private void BoardScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        LaneItemsControl.Height = Math.Max(320, e.NewSize.Height - 24);
    }

    private async Task ApplyConfigurationAsync(
        BoardSettingsSnapshot updated,
        CancellationToken cancellationToken)
    {
        BoardConfigurationValidator.Validate(updated);
        if (SettingsStore is not null)
        {
            await SettingsStore.SaveAsync(updated, cancellationToken);
        }

        var boardId = ActiveBoard?.BoardId
            ?? throw new InvalidOperationException("No board is selected.");
        Configuration = updated;
        ActiveBoard = updated.Boards.Single(board => board.BoardId == boardId);
    }

    private void SelectMailboxInControl(Guid? accountId)
    {
        suppressMailboxSelection = true;
        try
        {
            MailboxSelector.SelectedItem = accountId is null
                ? null
                : Mailboxes.FirstOrDefault(mailbox => mailbox.AccountId == accountId);
        }
        finally
        {
            suppressMailboxSelection = false;
        }
    }

    private BoardAccountBinding? GetSelectedAccountBinding()
    {
        if (ActiveBoard is null || SelectedAccountId is not Guid accountId)
        {
            return null;
        }

        return Configuration.AccountBindings.FirstOrDefault(binding =>
            binding.BoardId == ActiveBoard.BoardId && binding.AccountId == accountId);
    }

    private void RebuildLanes()
    {
        Lanes.Clear();
        if (ActiveBoard is null || SelectedAccountId is not Guid accountId)
        {
            AnnounceStatus(Mailboxes.Count == 0 ? "Connect a Gmail account to use Board view." : "Choose a mailbox.");
            return;
        }

        var binding = GetSelectedAccountBinding();
        foreach (var column in ActiveBoard.Columns.OrderBy(candidate => candidate.Position))
        {
            var labelId = binding?.ColumnBindings
                .FirstOrDefault(candidate => candidate.ColumnId == column.ColumnId)
                ?.GmailLabelId;
            var laneCards = cards
                .Where(card => card.AccountId == accountId && card.ColumnId == column.ColumnId)
                .ToArray();
            Lanes.Add(new BoardLaneViewModel(column, labelId, laneCards));
        }
    }

    private sealed record CardActionTarget(
        BoardCardItem Card,
        string SourceGmailLabelId,
        Guid TargetColumnId,
        string TargetGmailLabelId,
        BoardDragBehavior Behavior);
}
