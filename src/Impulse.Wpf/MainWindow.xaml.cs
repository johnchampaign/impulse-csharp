using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using IOPath = System.IO.Path;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SystemColor = System.Windows.Media.Color;
using System.Windows.Shapes;
using Impulse.Core;
using Impulse.Core.Cards;
using Impulse.Core.Controllers;
using Impulse.Core.Effects;
using Impulse.Core.Engine;
using Impulse.Core.Map;
using Impulse.Core.Players;

namespace Impulse.Wpf;

public partial class MainWindow : Window
{
    private GameState _g = null!;
    private GameRunner _runner = null!;
    private HumanController _human = null!;
    // Bootstrapping params remembered for state-codec emission and reload.
    private int _seed;
    private int _playerCount;
    private string[] _aiPolicies = Array.Empty<string>();
    private const double HexRadius = 46;

    // Click-handler dispatch state. When the human controller is awaiting input,
    // these are set; clicks consult them.
    private Func<int, bool>? _onHandCardClick;
    private Func<int, bool>? _onMineralCardClick;
    private Func<ShipLocation, bool>? _onMapLocClick;
    private HashSet<NodeId> _highlightNodes = new();
    private HashSet<GateId> _highlightGates = new();
    private HashSet<int>? _legalHandCardIds; // null = all hand cards visually equal
    private string? _handPromptOverride;
    private bool _showMineralsInsteadOfHand;
    private IReadOnlyList<int>? _mineralsToShow;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => StartGame();
        SizeChanged += (_, _) => Dispatcher.InvokeAsync(RenderMap);
        PreviewMouseRightButtonDown += OnRightClick;
    }

    private void OnRightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Right-click used to cancel the active prompt by setting
        // PendingChoice.Cancelled. The engine cleanup then nulls the outer
        // ctx.HandlerState — but for activated map cards / sub-effects, the
        // outer effect may have already committed irreversible mutations
        // (ships moved, cards drawn). Cancellation in that scenario leaves
        // the player in a broken state with the outer effect restarting on
        // a mutated game state. Stepwise prompts have explicit DONE/UNDO
        // buttons; other prompts have a DONE option where applicable.
        // No-op for now — keep the right-click handler so future work can
        // re-introduce a safe cancel scoped to a specific prompt.
        e.Handled = false;
    }

    private const int MaxReveals = 12;

    private void ShowReveal(RevealEvent rev)
    {
        var card = _g.CardsById[rev.CardId];
        var (badgeText, badgeBrush) = rev.Outcome switch
        {
            RevealOutcome.Kept      => ("KEPT",      (Brush)FindResource("CardGreen")),
            RevealOutcome.Mined     => ("MINED",     (Brush)FindResource("CardRed")),
            RevealOutcome.Scored    => ("SCORED",    (Brush)FindResource("Accent")),
            RevealOutcome.Discarded => ("DISCARD",   (Brush)FindResource("FgMuted")),
            _                       => ("?",         (Brush)FindResource("FgMuted")),
        };

        var border = new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = (Brush)FindResource("BgPanel2"),
            BorderBrush = CardBrush(card.Color),
            BorderThickness = new Thickness(2),
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 0, 0, 4),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        HookCardHover(border, card);

        var stack = new StackPanel();
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(new Border
        {
            Width = 8, Height = 8, CornerRadius = new CornerRadius(4),
            Background = CardBrush(card.Color),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        });
        top.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Body"),
            Text = $"#{card.Id} {card.ActionType}/{card.Size}",
            VerticalAlignment = VerticalAlignment.Center,
        });
        var badge = new Border
        {
            Background = badgeBrush,
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(4, 1, 4, 1),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        badge.Child = new TextBlock
        {
            Text = badgeText,
            Foreground = (Brush)FindResource("BgDark"),
            FontFamily = new FontFamily("Segoe UI"),
            FontWeight = FontWeights.Bold,
            FontSize = 9,
        };
        top.Children.Add(badge);
        stack.Children.Add(top);

        if (!string.IsNullOrEmpty(rev.Detail))
        {
            stack.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("Label"),
                Text = rev.Detail,
                FontSize = 9,
                Margin = new Thickness(0, 1, 0, 0),
            });
        }

        border.Child = stack;
        // newest at top
        RevealsPanel.Children.Insert(0, border);
        while (RevealsPanel.Children.Count > MaxReveals)
            RevealsPanel.Children.RemoveAt(RevealsPanel.Children.Count - 1);
    }

    private void ShowTechDetail(Tech tech)
    {
        switch (tech)
        {
            case Tech.Researched r:
                ShowCardDetail(_g.CardsById[r.CardId]);
                break;
            case Tech.BasicCommon:
                ShowGenericDetail("Basic Common", "#7E8AA8", Tech.BasicCommon.Text);
                break;
            case Tech.BasicUnique bu:
                ShowGenericDetail($"Basic ({bu.Race.Name})", "#7E8AA8", bu.Race.BasicUniqueTechText);
                break;
        }
    }

    private void ShowGenericDetail(string title, string colorHex, string body)
    {
        DetailContainer.Children.Clear();
        var brush = new SolidColorBrush((System.Windows.Media.Color)
            ColorConverter.ConvertFromString(colorHex));
        var border = new Border
        {
            Width = 256, MinHeight = 180,
            CornerRadius = new CornerRadius(8),
            Background = (Brush)FindResource("BgPanel2"),
            BorderBrush = brush,
            BorderThickness = new Thickness(3),
            Padding = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title.ToUpperInvariant(),
            Foreground = brush,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 18, FontWeight = FontWeights.Bold,
        });
        stack.Children.Add(new Border
        {
            Height = 1, Background = (Brush)FindResource("GateStroke"),
            Margin = new Thickness(0, 8, 0, 8),
        });
        stack.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Body"),
            Text = body,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13, LineHeight = 18,
        });
        border.Child = stack;
        DetailContainer.Children.Add(border);
    }

    private void ShowPlayerDetail(PlayerState p)
    {
        DetailContainer.Children.Clear();
        var container = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
        };

        // Header
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new Border
        {
            Width = 14, Height = 14, CornerRadius = new CornerRadius(7),
            Background = PlayerBrush(p.Color),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });
        header.Children.Add(new TextBlock
        {
            Text = $"{p.Id} {p.Race.Name}",
            Foreground = (Brush)FindResource("FgPrimary"),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 16, FontWeight = FontWeights.Bold,
        });
        container.Children.Add(header);

        container.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Label"),
            Text = $"prestige {p.Prestige}   ships avail {p.ShipsAvailable}   hand {p.Hand.Count}",
            Margin = new Thickness(0, 4, 0, 0),
        });

        AddPlayerDetailSection(container, "TECHS", new[] { p.Techs.Left, p.Techs.Right }, t =>
        {
            var chip = BuildTechChip(t, clickable: false, onClick: null);
            return chip;
        });

        if (p.Plan.Count > 0)
            AddPlayerDetailCardSection(container, $"PLAN ({p.Plan.Count})", p.Plan);
        else
            AddEmptySection(container, "PLAN");

        if (p.Minerals.Count > 0)
            AddPlayerDetailCardSection(container, $"MINERALS ({p.Minerals.Count} cards)", p.Minerals);
        else
            AddEmptySection(container, "MINERALS");

        DetailContainer.Children.Add(container);
    }

    private void AddPlayerDetailSection<T>(StackPanel parent, string label, IEnumerable<T> items, Func<T, FrameworkElement> render)
    {
        parent.Children.Add(new TextBlock { Style = (Style)FindResource("Label"), Text = label, Margin = new Thickness(0, 8, 0, 4) });
        var row = new WrapPanel();
        foreach (var item in items)
            row.Children.Add(render(item));
        parent.Children.Add(row);
    }

    private void AddPlayerDetailCardSection(StackPanel parent, string label, IEnumerable<int> cardIds)
    {
        parent.Children.Add(new TextBlock { Style = (Style)FindResource("Label"), Text = label, Margin = new Thickness(0, 8, 0, 4) });
        var row = new WrapPanel();
        foreach (var id in cardIds)
        {
            var c = _g.CardsById[id];
            row.Children.Add(BuildMiniCard(c, isCursor: false));
        }
        parent.Children.Add(row);
    }

    private void AddEmptySection(StackPanel parent, string label)
    {
        parent.Children.Add(new TextBlock { Style = (Style)FindResource("Label"), Text = $"{label} (empty)", Margin = new Thickness(0, 8, 0, 0) });
    }

    private FrameworkElement BuildTechChip(Tech tech, bool clickable, Action? onClick)
    {
        Brush borderBrush;
        string label;
        if (tech is Tech.Researched r)
        {
            var card = _g.CardsById[r.CardId];
            borderBrush = CardBrush(card.Color);
            label = $"#{r.CardId} {card.ActionType}/{card.Size}";
        }
        else if (tech is Tech.BasicCommon)
        {
            borderBrush = (Brush)FindResource("FgMuted");
            label = "Basic Common";
        }
        else if (tech is Tech.BasicUnique bu)
        {
            borderBrush = (Brush)FindResource("FgMuted");
            label = $"Basic ({bu.Race.Name})";
        }
        else
        {
            borderBrush = (Brush)FindResource("FgMuted");
            label = "?";
        }

        var border = new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = (Brush)FindResource("BgPanel"),
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(2),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = clickable ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow,
        };
        border.MouseEnter += (_, _) => ShowTechDetail(tech);
        if (clickable && onClick is not null)
            border.MouseLeftButtonDown += (_, _) => onClick();

        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Label"),
            Text = label,
            FontSize = 10,
        });
        border.Child = sp;
        return border;
    }

    private void ShowCardDetail(Card? c)
    {
        DetailContainer.Children.Clear();
        if (c is null)
        {
            DetailContainer.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("Label"),
                Text = "Hover a card for details.",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 8, 0, 0),
            });
            return;
        }

        var border = new Border
        {
            Width = 256, MinHeight = 180,
            CornerRadius = new CornerRadius(8),
            Background = (Brush)FindResource("BgPanel2"),
            BorderBrush = CardBrush(c.Color),
            BorderThickness = new Thickness(3),
            Padding = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var stack = new StackPanel();

        var headerRow = new StackPanel { Orientation = Orientation.Horizontal };
        headerRow.Children.Add(new Border
        {
            Width = 14, Height = 14, CornerRadius = new CornerRadius(7),
            Background = CardBrush(c.Color),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });
        headerRow.Children.Add(new TextBlock
        {
            Text = c.ActionType.ToString().ToUpperInvariant(),
            Foreground = CardBrush(c.Color),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 18,
            FontWeight = FontWeights.Bold,
        });
        stack.Children.Add(headerRow);

        var meta = new TextBlock
        {
            Style = (Style)FindResource("Label"),
            Text = $"size {c.Size}   boost {c.BoostNumber}   color {c.Color}   #{c.Id}",
            Margin = new Thickness(0, 4, 0, 0),
        };
        stack.Children.Add(meta);

        var divider = new Border
        {
            Height = 1, Background = (Brush)FindResource("GateStroke"),
            Margin = new Thickness(0, 8, 0, 8),
        };
        stack.Children.Add(divider);

        stack.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Body"),
            Text = c.EffectText,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            LineHeight = 18,
        });

        stack.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Label"),
            Text = c.EffectFamily,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 9,
            Margin = new Thickness(0, 12, 0, 0),
        });

        border.Child = stack;
        DetailContainer.Children.Add(border);
    }

    private void HookCardHover(FrameworkElement el, Card c)
    {
        el.MouseEnter += (_, _) => ShowCardDetail(c);
        // Don't clear on leave — keep last-hovered card visible (lets you read it
        // without keeping the cursor on the small card).
    }

    // When set before StartGame, the game starts from this snapshot file
    // instead of going through the normal lobby setup. Used by Load State.
    private static string? PendingSnapshotPath;

    private void SeeLogButton_Click(object sender, RoutedEventArgs e)
    {
        var path = _g?.Log?.FilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            MessageBox.Show(this, "Log file not available yet.", "Impulse",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't open log: {ex.Message}", "Impulse",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void LoadStateButton_Click(object sender, RoutedEventArgs e)
    {
        ShowPasteCodecDialog();
    }

    // Variant called from the lobby: closes the given lobby on success.
    // Sets _loadedFromLobby so ShowLobby's caller knows to exit early.
    private bool _loadedFromLobby;
    private void ShowPasteCodecDialogFromLobby(Window lobby)
    {
        if (TryShowPasteCodec())
        {
            _loadedFromLobby = true;
            lobby.Close();
        }
    }

    private void ShowPasteCodecDialog()
    {
        // TryShowPasteCodec performs the reload in-place; nothing else to do.
        TryShowPasteCodec();
    }

    // Returns true if user successfully pasted a codec and the in-place
    // reload (replacing _g and _runner) was performed.
    private bool TryShowPasteCodec()
    {
        var dlg = new Window
        {
            Title = "Load Impulse state",
            Width = 620, Height = 340,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = (Brush)FindResource("BgDark"),
            Foreground = (Brush)FindResource("FgPrimary"),
            FontFamily = new FontFamily("Segoe UI"),
            ResizeMode = ResizeMode.NoResize,
        };
        var stack = new StackPanel { Margin = new Thickness(16) };
        stack.Children.Add(new TextBlock
        {
            Text = "Paste a state codec (the base64 string after `[state turn=…] ` in the log). " +
                   "You can also paste the entire log line — the prefix is stripped automatically.",
            Margin = new Thickness(0, 0, 0, 6),
            Foreground = (Brush)FindResource("FgPrimary"),
            TextWrapping = TextWrapping.Wrap,
        });
        var box = new TextBox
        {
            Height = 180,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 11,
        };
        stack.Children.Add(box);
        var status = new TextBlock
        {
            Foreground = (Brush)FindResource("Accent"),
            Margin = new Thickness(0, 6, 0, 6),
            TextWrapping = TextWrapping.Wrap,
        };
        stack.Children.Add(status);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var loadBtn = new Button { Content = "Load", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 6, 0) };
        var cancelBtn = new Button { Content = "Cancel", Padding = new Thickness(14, 4, 14, 4) };
        buttons.Children.Add(loadBtn);
        buttons.Children.Add(cancelBtn);
        stack.Children.Add(buttons);
        dlg.Content = stack;

        cancelBtn.Click += (_, _) => dlg.Close();
        bool success = false;
        loadBtn.Click += (_, _) =>
        {
            var encoded = box.Text.Trim();
            // Strip a `[state turn=… active=…] ` prefix if the user pasted
            // the whole log line.
            if (encoded.StartsWith("[state ", StringComparison.Ordinal))
            {
                int idx = encoded.IndexOf(']');
                if (idx > 0) encoded = encoded[(idx + 1)..].Trim();
            }
            if (encoded.Length == 0) { status.Text = "Paste a codec string first."; return; }
            try
            {
                _ = GameSnapshot.DecodeFromString(encoded); // validate
                string tmp = IOPath.Combine(IOPath.GetTempPath(), "impulse-pending-load.snap");
                File.WriteAllText(tmp, encoded);
                PendingSnapshotPath = tmp;
                success = true;
                dlg.Close();
            }
            catch (Exception ex)
            {
                status.Text = $"Decode failed: {ex.Message}";
            }
        };
        box.Focus();
        dlg.ShowDialog();
        if (!success) return false;
        // In-place reload: don't open a new window. Replace _g and _runner
        // here in the current MainWindow. The old runner thread (if any)
        // is left blocked on its TCS — it leaks but is harmless.
        try
        {
            ReloadFromPendingSnapshot();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Load State failed: {ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}",
                "Impulse — Load State", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        return true;
    }

    private void ReloadFromPendingSnapshot()
    {
        if (PendingSnapshotPath is not { } path || !File.Exists(path))
            throw new InvalidOperationException("No pending snapshot file.");
        var snap = GameSnapshot.Load(path);
        PendingSnapshotPath = null;

        int seed = snap.Seed;
        int playerCount = snap.PlayerCount;
        var aiPolicies = snap.AiPolicies.Length == playerCount - 1
            ? snap.AiPolicies.Select(p => Enum.Parse<AiPolicy>(p)).ToArray()
            : Enumerable.Repeat(AiPolicy.Greedy, Math.Max(0, playerCount - 1)).ToArray();
        _seed = seed;
        _playerCount = playerCount;
        _aiPolicies = aiPolicies.Select(p => p.ToString()).ToArray();

        // Dispose previous log, then fresh registry + game.
        try { _g?.Log?.Dispose(); } catch { /* best effort */ }

        var registry = new EffectRegistry();
        CommandRegistrations.RegisterAll(registry);
        BuildRegistrations.RegisterAll(registry);
        MineRegistrations.RegisterAll(registry);
        RefineRegistrations.RegisterAll(registry);
        DrawRegistrations.RegisterAll(registry);
        TradeRegistrations.RegisterAll(registry);
        PlanRegistrations.RegisterAll(registry);
        ResearchRegistrations.RegisterAll(registry);
        SabotageRegistrations.RegisterAll(registry);
        ExecuteRegistrations.RegisterAll(registry);

        _g = SetupFactory.NewGame(new SetupOptions(PlayerCount: playerCount, Seed: seed), registry);
        snap.RestoreInto(_g);

        // New controllers — old _human is abandoned; the runner blocked on
        // its TCS will leak but does no harm.
        _human = new HumanController(new PlayerId(1));
        _human.ActionNeeded += OnActionNeeded;
        _human.ChoiceNeeded += OnChoiceNeeded;

        var controllers = new List<IPlayerController> { _human };
        for (int i = 1; i < _g.Players.Count; i++)
            controllers.Add(new PolicyController(_g.Players[i].Id, seed: 100 + i, policy: aiPolicies[i - 1]));

        _runner = new GameRunner(_g, registry, controllers)
        {
            StateEncoder = state => GameSnapshot
                .Capture(state, _seed, _playerCount, _aiPolicies)
                .EncodeToString(),
        };

        // New log file (rotates the previous one).
        _g.Log.OpenFile();
        _g.Log.Write($"# log file: {_g.Log.FilePath}");
        _g.Log.Write($"# resumed from snapshot — turn {snap.CurrentTurn}, active P{snap.ActivePlayer}");
        _g.Log.OnLine += line => Dispatcher.InvokeAsync(() => AppendLog(line));
        _g.Log.OnReveal += rev => Dispatcher.InvokeAsync(() => ShowReveal(rev));
        _g.Log.OnAlert += msg => Dispatcher.InvokeAsync(() =>
            MessageBox.Show(this, msg, "Impulse", MessageBoxButton.OK, MessageBoxImage.Warning));

        _gameOverShown = false;
        LogText.Text = "";
        Render();
        ShowCardDetail(null);

        Task.Run(() =>
        {
            try { _runner.RunUntilDone(maxTurns: 200); }
            catch (Exception ex)
            {
                _g.Log.Write($"!! ENGINE CRASH: {ex}");
                Dispatcher.InvokeAsync(() =>
                    MessageBox.Show(this, $"Engine crashed: {ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}",
                        "Impulse — Engine Crash", MessageBoxButton.OK, MessageBoxImage.Error));
            }
            Dispatcher.InvokeAsync(ShowGameOverIfDone);
        });
    }


    // Returns (playerCount, perAiPolicy[]). aiPolicies[i] is the policy for
    // the (i+1)-th AI seat (seat 0 is the human). Returns null when the
    // user clicked "Load State…" instead of Start — caller should abandon
    // this StartGame; a new MainWindow has already been opened to handle
    // the pending snapshot.
    private (int, AiPolicy[])? ShowLobby()
    {
        var lobby = new Window
        {
            Title = "Impulse — New Game",
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            Background = (Brush)FindResource("BgDark"),
            Foreground = (Brush)FindResource("FgPrimary"),
            FontFamily = new FontFamily("Segoe UI"),
        };
        var root = new StackPanel { Margin = new Thickness(20), MinWidth = 380 };
        root.Children.Add(new TextBlock
        {
            Text = "New Game",
            FontSize = 22, FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("Accent"),
            Margin = new Thickness(0, 0, 0, 12),
        });
        var prefs = LobbyPrefs.Load();

        root.Children.Add(new TextBlock { Style = (Style)FindResource("Label"), Text = "PLAYERS (2–6)" });
        var pcCombo = new ComboBox { Margin = new Thickness(0, 4, 0, 12), Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
        for (int n = 2; n <= 6; n++) pcCombo.Items.Add(n);
        // Default 4 players, or whatever the user chose last run.
        pcCombo.SelectedIndex = Math.Clamp(prefs.PlayerCount - 2, 0, 4);
        root.Children.Add(pcCombo);

        root.Children.Add(new TextBlock { Style = (Style)FindResource("Label"), Text = "AI OPPONENTS" });
        var aiPanel = new StackPanel { Margin = new Thickness(0, 4, 0, 12) };
        root.Children.Add(aiPanel);

        var aiCombos = new List<ComboBox>();
        var rng = new Random();
        void RebuildAiRows()
        {
            aiPanel.Children.Clear();
            aiCombos.Clear();
            int n = (int)pcCombo.SelectedItem!;
            for (int i = 1; i < n; i++)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
                row.Children.Add(new TextBlock
                {
                    Text = $"P{i + 1}:",
                    Width = 36, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource("FgPrimary"),
                });
                var cb = new ComboBox { Width = 140 };
                cb.Items.Add("Random");
                foreach (var p in Enum.GetValues<AiPolicy>()) cb.Items.Add(p.ToString());
                // Restore the previously-saved selection for this seat if
                // available; otherwise default to Greedy.
                int seatIdx = i - 1;
                // First-run default: CoreRush — the strongest baseline by
                // bench (~24-66% win rate across 2/4/6 player counts; the
                // others trail). Once the user picks anything else, the
                // saved selection from LobbyPrefs takes precedence below.
                int desiredIdx = 1 + (int)AiPolicy.CoreRush;
                if (seatIdx < prefs.AiSelections.Length)
                {
                    var saved = prefs.AiSelections[seatIdx];
                    int found = -1;
                    for (int k = 0; k < cb.Items.Count; k++)
                        if ((string)cb.Items[k]! == saved) { found = k; break; }
                    if (found >= 0) desiredIdx = found;
                }
                cb.SelectedIndex = desiredIdx;
                row.Children.Add(cb);
                aiCombos.Add(cb);
                aiPanel.Children.Add(row);
            }
        }
        pcCombo.SelectionChanged += (_, _) => RebuildAiRows();
        RebuildAiRows();

        bool started = false;
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var loadFromLobbyBtn = new Button
        {
            Content = "Load State…", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0),
            Background = (Brush)FindResource("BgPanel2"),
            Foreground = (Brush)FindResource("FgPrimary"),
            BorderBrush = (Brush)FindResource("Accent"), BorderThickness = new Thickness(1),
        };
        loadFromLobbyBtn.Click += (_, _) =>
        {
            // If user successfully pastes a codec, ShowPasteCodecDialog will
            // set PendingSnapshotPath and open a new MainWindow + close this
            // one. The lobby never returns. Otherwise the dialog just closes
            // and the user can still click Start.
            ShowPasteCodecDialogFromLobby(lobby);
        };
        btnRow.Children.Add(loadFromLobbyBtn);
        var startBtn = new Button
        {
            Content = "Start", Padding = new Thickness(16, 6, 16, 6),
            Background = (Brush)FindResource("Accent"),
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            BorderThickness = new Thickness(0),
        };
        startBtn.Click += (_, _) => { started = true; lobby.Close(); };
        btnRow.Children.Add(startBtn);
        root.Children.Add(btnRow);
        lobby.Content = root;
        lobby.ShowDialog();
        if (!started)
        {
            // User clicked "Load State…" — reload happened in-place; tell
            // StartGame to bail out without further setup.
            if (_loadedFromLobby) return null;
            Environment.Exit(0); // user closed without choosing
        }

        int playerCount = (int)pcCombo.SelectedItem!;
        var policies = new AiPolicy[playerCount - 1];
        var savedSelections = new string[aiCombos.Count];
        for (int i = 0; i < aiCombos.Count; i++)
        {
            // Index 0 = "Random" → pick random; otherwise i-th enum value (offset by 1).
            int sel = aiCombos[i].SelectedIndex;
            savedSelections[i] = (string)aiCombos[i].Items[sel]!;
            if (sel == 0)
                policies[i] = (AiPolicy)rng.Next(Enum.GetValues<AiPolicy>().Length);
            else
                policies[i] = (AiPolicy)(sel - 1);
        }
        // Persist for next launch.
        new LobbyPrefs { PlayerCount = playerCount, AiSelections = savedSelections }.Save();
        return (playerCount, policies);
    }

    private void StartGame()
    {
        int playerCount;
        AiPolicy[] aiPolicies;
        int seed;
        GameSnapshot? pendingSnap = null;
        // If a snapshot is pending (set by Load State), skip the lobby and
        // bootstrap from it directly.
        if (PendingSnapshotPath is { } loadPath && File.Exists(loadPath))
        {
            try
            {
                pendingSnap = GameSnapshot.Load(loadPath);
                seed = pendingSnap.Seed;
                playerCount = pendingSnap.PlayerCount;
                aiPolicies = pendingSnap.AiPolicies.Length == pendingSnap.PlayerCount - 1
                    ? pendingSnap.AiPolicies.Select(p => Enum.Parse<AiPolicy>(p)).ToArray()
                    : Enumerable.Repeat(AiPolicy.Greedy, Math.Max(0, playerCount - 1)).ToArray();
                PendingSnapshotPath = null;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Couldn't load snapshot: {ex.Message}\nFalling back to lobby.",
                    "Impulse", MessageBoxButton.OK, MessageBoxImage.Warning);
                pendingSnap = null;
                PendingSnapshotPath = null;
                var lobby = ShowLobby();
                if (lobby is null) { Close(); return; }
                (playerCount, aiPolicies) = lobby.Value;
                seed = Environment.TickCount;
            }
        }
        else
        {
            var lobby = ShowLobby();
            if (lobby is null)
            {
                // Lobby returned null — either user closed it (handled by
                // Environment.Exit) or "Load State…" was clicked and the
                // reload already populated _g. In the load case, just exit
                // StartGame; the new state is already running.
                return;
            }
            (playerCount, aiPolicies) = lobby.Value;
            seed = Environment.TickCount;
        }
        _seed = seed;
        _playerCount = playerCount;
        _aiPolicies = aiPolicies.Select(p => p.ToString()).ToArray();

        var registry = new EffectRegistry();
        CommandRegistrations.RegisterAll(registry);
        BuildRegistrations.RegisterAll(registry);
        MineRegistrations.RegisterAll(registry);
        RefineRegistrations.RegisterAll(registry);
        DrawRegistrations.RegisterAll(registry);
        TradeRegistrations.RegisterAll(registry);
        PlanRegistrations.RegisterAll(registry);
        ResearchRegistrations.RegisterAll(registry);
        SabotageRegistrations.RegisterAll(registry);
        ExecuteRegistrations.RegisterAll(registry);

        _g = SetupFactory.NewGame(new SetupOptions(PlayerCount: playerCount, Seed: seed), registry);

        // If we loaded a snapshot earlier, apply its dynamic state on top of
        // the freshly-built game.
        if (pendingSnap is not null)
        {
            try
            {
                pendingSnap.RestoreInto(_g);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Snapshot apply failed: {ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}",
                    "Impulse — Load State", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
                return;
            }
        }

        _human = new HumanController(new PlayerId(1));
        _human.ActionNeeded += OnActionNeeded;
        _human.ChoiceNeeded += OnChoiceNeeded;

        var controllers = new List<IPlayerController> { _human };
        for (int i = 1; i < _g.Players.Count; i++)
            controllers.Add(new PolicyController(_g.Players[i].Id, seed: 100 + i, policy: aiPolicies[i - 1]));

        _runner = new GameRunner(_g, registry, controllers)
        {
            StateEncoder = state => GameSnapshot
                .Capture(state, _seed, _playerCount, _aiPolicies)
                .EncodeToString(),
        };
        _g.Log.OpenFile();
        _g.Log.Write($"# log file: {_g.Log.FilePath}");
        _g.Log.OnLine += line => Dispatcher.InvokeAsync(() => AppendLog(line));
        _g.Log.OnReveal += rev => Dispatcher.InvokeAsync(() => ShowReveal(rev));
        _g.Log.OnAlert += msg => Dispatcher.InvokeAsync(() =>
            MessageBox.Show(this, msg, "Impulse", MessageBoxButton.OK, MessageBoxImage.Warning));
        Closed += (_, _) => _g.Log.Dispose();

        Render();
        ShowCardDetail(null);
        Task.Run(() =>
        {
            try { _runner.RunUntilDone(maxTurns: 200); }
            catch (Exception ex)
            {
                _g.Log.Write($"!! ENGINE CRASH: {ex}");
                Dispatcher.InvokeAsync(() =>
                    MessageBox.Show(this, $"Engine crashed: {ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}",
                        "Impulse — Engine Crash", MessageBoxButton.OK, MessageBoxImage.Error));
            }
            Dispatcher.InvokeAsync(ShowGameOverIfDone);
        });
    }

    // ---------- Human prompt callbacks (engine thread → UI thread) ----------
    private void OnActionNeeded(GameState g, IReadOnlyList<PlayerAction> legal) =>
        Dispatcher.InvokeAsync(() => HandleActionPrompt(legal));

    private void OnChoiceNeeded(GameState g, ChoiceRequest request) =>
        Dispatcher.InvokeAsync(() => HandleChoicePrompt(request));

    private void HandleActionPrompt(IReadOnlyList<PlayerAction> legal)
    {
        _human.TrackChoice(null);
        Render();
        if (legal[0] is PlayerAction.PlaceImpulse)
        {
            PromptText.Text = "Pick a card from your hand to add to the Impulse track.";
            var validIds = legal.OfType<PlayerAction.PlaceImpulse>().Select(a => a.CardIdFromHand).ToHashSet();
            _onHandCardClick = id =>
            {
                if (!validIds.Contains(id)) return false;
                ClearPrompt();
                _human.SubmitAction(new PlayerAction.PlaceImpulse(id));
                return true;
            };
        }
        else if (legal.OfType<PlayerAction.UseImpulseCard>().Any() &&
                 legal.OfType<PlayerAction.SkipImpulseCard>().Any())
        {
            PromptText.Text = $"Use or skip this card?";
            ImpulseActionPanel.Children.Clear();
            ImpulseActionPanel.Children.Add(BuildButton("USE", () =>
            {
                ClearPrompt();
                _human.SubmitAction(new PlayerAction.UseImpulseCard());
            }));
            ImpulseActionPanel.Children.Add(BuildButton("SKIP", () =>
            {
                ClearPrompt();
                _human.SubmitAction(new PlayerAction.SkipImpulseCard());
            }));
        }
        else if (legal.OfType<PlayerAction.UseTech>().Any() ||
                 legal.OfType<PlayerAction.SkipTech>().Any())
        {
            PromptText.Text = "Use a tech, or skip.";
            ImpulseActionPanel.Children.Clear();
            var p = _g.Player(_human.Seat);
            var leftWrap = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 4) };
            leftWrap.Children.Add(new TextBlock { Style = (Style)FindResource("Label"), Text = "USE LEFT" });
            leftWrap.Children.Add(BuildTechChip(p.Techs.Left, clickable: true, onClick: () =>
            {
                ClearPrompt();
                _human.SubmitAction(new PlayerAction.UseTech(TechSlot.Left));
            }));
            ImpulseActionPanel.Children.Add(leftWrap);

            var rightWrap = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 4) };
            rightWrap.Children.Add(new TextBlock { Style = (Style)FindResource("Label"), Text = "USE RIGHT" });
            rightWrap.Children.Add(BuildTechChip(p.Techs.Right, clickable: true, onClick: () =>
            {
                ClearPrompt();
                _human.SubmitAction(new PlayerAction.UseTech(TechSlot.Right));
            }));
            ImpulseActionPanel.Children.Add(rightWrap);

            ImpulseActionPanel.Children.Add(BuildButton("SKIP", () =>
            {
                ClearPrompt();
                _human.SubmitAction(new PlayerAction.SkipTech());
            }));
        }
        else if (legal.OfType<PlayerAction.UsePlan>().Any() &&
                 legal.OfType<PlayerAction.SkipPlan>().Any())
        {
            PromptText.Text = "Use or delay your Plan?";
            ImpulseActionPanel.Children.Clear();
            ImpulseActionPanel.Children.Add(BuildButton("USE PLAN", () =>
            {
                ClearPrompt();
                _human.SubmitAction(new PlayerAction.UsePlan());
            }));
            ImpulseActionPanel.Children.Add(BuildButton("DELAY", () =>
            {
                ClearPrompt();
                _human.SubmitAction(new PlayerAction.SkipPlan());
            }));
        }
        else
        {
            // Unknown — submit the first legal action as a stub.
            _human.SubmitAction(legal[0]);
        }
    }

    private void HandleChoicePrompt(ChoiceRequest request)
    {
        _human.TrackChoice(request);
        Render();
        switch (request)
        {
            case SelectFleetRequest f:
                PromptText.Text = "Click a node or gate to select your fleet's location.";
                HighlightLocations(f.LegalLocations);
                _onMapLocClick = loc =>
                {
                    if (!ContainsLoc(f.LegalLocations, loc)) return false;
                    f.Chosen = loc;
                    ClearPrompt();
                    _human.CompleteChoice();
                    return true;
                };
                break;

            case DeclareMoveRequest m:
                BeginStepwiseMove(m);
                break;

            case SelectFromOptionsRequest opt:
                PromptText.Text = opt.Prompt.Length > 0 ? opt.Prompt : "Choose:";
                ImpulseActionPanel.Children.Clear();
                for (int i = 0; i < opt.Options.Count; i++)
                {
                    int captured = i;
                    ImpulseActionPanel.Children.Add(BuildButton(opt.Options[i], () =>
                    {
                        opt.Chosen = captured;
                        ClearPrompt();
                        _human.CompleteChoice();
                    }));
                }
                break;

            case SelectTechSlotRequest ts:
                PromptText.Text = ts.Prompt.Length > 0 ? ts.Prompt : "Choose a tech slot.";
                ImpulseActionPanel.Children.Clear();
                // Show the incoming card prominently so the player sees
                // exactly what they're researching.
                if (ts.IncomingCardId is int incoming && _g.CardsById.TryGetValue(incoming, out var incomingCard))
                {
                    ImpulseActionPanel.Children.Add(new TextBlock
                    {
                        Style = (Style)FindResource("Label"),
                        Text = "▶ INCOMING CARD",
                        Foreground = (Brush)FindResource("Accent"),
                        FontWeight = FontWeights.Bold,
                        Margin = new Thickness(0, 0, 0, 4),
                    });
                    ImpulseActionPanel.Children.Add(BuildPlanCard(incoming, resolving: true));
                    ImpulseActionPanel.Children.Add(new TextBlock
                    {
                        Style = (Style)FindResource("Label"),
                        Text = "Replace which slot?",
                        Margin = new Thickness(0, 6, 0, 4),
                    });
                }
                // Each slot button labels the tech currently there so the
                // player knows what they'd be overwriting.
                var humanPlayer = _g.Player(_human.Seat);
                string TechLabel(Tech t) => t switch
                {
                    Tech.Researched r => $"#{r.CardId} {_g.CardsById[r.CardId].ActionType}/{_g.CardsById[r.CardId].Size}",
                    Tech.BasicCommon => "Basic Common",
                    Tech.BasicUnique bu => $"Basic ({bu.Race.Name})",
                    _ => "?",
                };
                ImpulseActionPanel.Children.Add(BuildButton($"LEFT — currently: {TechLabel(humanPlayer.Techs.Left)}", () =>
                {
                    ts.Chosen = TechSlot.Left;
                    ClearPrompt();
                    _human.CompleteChoice();
                }));
                ImpulseActionPanel.Children.Add(BuildButton($"RIGHT — currently: {TechLabel(humanPlayer.Techs.Right)}", () =>
                {
                    ts.Chosen = TechSlot.Right;
                    ClearPrompt();
                    _human.CompleteChoice();
                }));
                if (ts.AllowSkip)
                {
                    ImpulseActionPanel.Children.Add(BuildButton("SKIP (don't research)", () =>
                    {
                        ts.Chosen = null;
                        ClearPrompt();
                        _human.CompleteChoice();
                    }));
                }
                break;

            case SelectFleetSizeRequest fs:
                PromptText.Text = fs.Prompt.Length > 0 ? fs.Prompt : $"How many ships to move? ({fs.Min}–{fs.Max})";
                ImpulseActionPanel.Children.Clear();
                for (int n = fs.Min; n <= fs.Max; n++)
                {
                    int captured = n;
                    ImpulseActionPanel.Children.Add(BuildButton($"{captured}", () =>
                    {
                        fs.Chosen = captured;
                        ClearPrompt();
                        _human.CompleteChoice();
                    }));
                }
                break;

            case SelectShipPlacementRequest sp:
                PromptText.Text = sp.Prompt.Length > 0 ? sp.Prompt : "Click a location to Build at.";
                HighlightLocations(sp.LegalLocations);
                _onMapLocClick = loc =>
                {
                    if (!ContainsLoc(sp.LegalLocations, loc)) return false;
                    sp.Chosen = loc;
                    ClearPrompt();
                    _human.CompleteChoice();
                    return true;
                };
                break;

            case SelectSabotageTargetRequest sab:
                PromptText.Text = sab.Prompt.Length > 0 ? sab.Prompt : "Pick an enemy fleet to sabotage.";
                HighlightLocations(sab.LegalTargets.Select(t => t.Location).Distinct(new LocComparer()));
                _onMapLocClick = loc =>
                {
                    var matching = sab.LegalTargets.Where(t => Key(t.Location) == Key(loc)).ToList();
                    if (matching.Count == 0) return false;
                    if (matching.Count == 1)
                    {
                        sab.Chosen = matching[0];
                        ClearPrompt();
                        _human.CompleteChoice();
                        return true;
                    }
                    // Multiple enemy owners share this location → secondary picker
                    PromptText.Text = "Multiple enemy fleets here — pick whose to sabotage.";
                    ImpulseActionPanel.Children.Clear();
                    foreach (var t in matching)
                    {
                        var captured = t;
                        ImpulseActionPanel.Children.Add(BuildButton(captured.Owner.ToString(), () =>
                        {
                            sab.Chosen = captured;
                            ClearPrompt();
                            _human.CompleteChoice();
                        }));
                    }
                    return true;
                };
                break;

            case SelectHandCardRequest h:
                PromptText.Text = h.Prompt.Length > 0 ? h.Prompt : "Pick a card from your hand.";
                _handPromptOverride = "→ " + (h.Prompt.Length > 0 ? h.Prompt : "Pick a card from your hand.");
                _legalHandCardIds = h.LegalCardIds.ToHashSet();
                _onHandCardClick = id =>
                {
                    if (!_legalHandCardIds.Contains(id)) return false;
                    h.ChosenCardId = id;
                    ClearPrompt();
                    _human.CompleteChoice();
                    return true;
                };
                if (h.AllowNone)
                {
                    ImpulseActionPanel.Children.Clear();
                    ImpulseActionPanel.Children.Add(BuildButton("DONE", () =>
                    {
                        h.ChosenCardId = null;
                        ClearPrompt();
                        _human.CompleteChoice();
                    }));
                }
                RenderHand(_g.Player(_human.Seat));
                break;

            case SelectMineralCardRequest m:
                PromptText.Text = m.Prompt.Length > 0 ? m.Prompt : "Pick a mineral card.";
                _showMineralsInsteadOfHand = true;
                _mineralsToShow = m.LegalCardIds;
                _onMineralCardClick = id =>
                {
                    if (!m.LegalCardIds.Contains(id)) return false;
                    m.ChosenCardId = id;
                    ClearPrompt();
                    _human.CompleteChoice();
                    return true;
                };
                RenderHand(_g.Player(_human.Seat));
                break;
        }
    }

    // Stepwise move UX: instead of asking the player to pick a final
    // destination in one click, show the next-step options after each click
    // so they can plan one hop at a time. Internally we still commit a
    // single complete path to the engine once the player accepts (DONE) or
    // reaches a path that can't be extended.
    private void BeginStepwiseMove(DeclareMoveRequest m)
    {
        var partial = new List<ShipLocation>();
        void CommitEmpty()
        {
            m.ChosenPath = new List<ShipLocation>();
            ClearPrompt();
            _human.CompleteChoice();
        }
        void Refresh()
        {
            var matchingPaths = m.LegalPaths
                .Where(p => StartsWith(p, partial))
                .ToList();
            // Next-step options are the unique locations at index `partial.Count`
            // among the longer paths.
            var nextSteps = matchingPaths
                .Where(p => p.Count > partial.Count)
                .Select(p => p[partial.Count])
                .GroupBy(loc => Key(loc))
                .Select(grp => grp.First())
                .ToList();
            HighlightLocations(nextSteps);
            // Highlight the origin too so the user sees that clicking it is
            // a legal "stay" choice when partial is empty.
            if (partial.Count == 0)
                AddHighlight(m.Origin);

            string stepLabel = partial.Count == 0
                ? $"Pick step 1 of up to {m.MaxMoves} (click origin or STAY to not move)."
                : $"Step {partial.Count + 1}/{m.MaxMoves} — pick next, or accept current path.";
            PromptText.Text = stepLabel;

            ImpulseActionPanel.Children.Clear();
            // DONE accepts the partial path if it's a complete legal path.
            bool partialIsLegal = partial.Count > 0 &&
                                  matchingPaths.Any(p => p.Count == partial.Count);
            if (partialIsLegal)
            {
                ImpulseActionPanel.Children.Add(BuildButton("DONE (stop here)", () =>
                {
                    m.ChosenPath = matchingPaths.First(p => p.Count == partial.Count);
                    ClearPrompt();
                    _human.CompleteChoice();
                }));
            }
            if (partial.Count == 0)
            {
                ImpulseActionPanel.Children.Add(BuildButton("STAY (don't move)", CommitEmpty));
            }
            if (partial.Count > 0)
            {
                ImpulseActionPanel.Children.Add(BuildButton("UNDO step", () =>
                {
                    partial.RemoveAt(partial.Count - 1);
                    Refresh();
                }));
            }
        }

        _onMapLocClick = loc =>
        {
            // Clicking the origin while the path is empty = "stay".
            if (partial.Count == 0 && Key(loc) == Key(m.Origin))
            {
                CommitEmpty();
                return true;
            }
            // Otherwise click must match one of the next-step options.
            var validNext = m.LegalPaths
                .Where(p => StartsWith(p, partial) &&
                            p.Count > partial.Count &&
                            Key(p[partial.Count]) == Key(loc))
                .ToList();
            if (validNext.Count == 0) return false;
            partial.Add(loc);
            // Now restrict to paths that actually go through the new partial.
            var afterClick = m.LegalPaths.Where(p => StartsWith(p, partial)).ToList();
            bool canExtend = afterClick.Any(p => p.Count > partial.Count);
            bool partialIsExact = afterClick.Any(p => p.Count == partial.Count);
            if (!canExtend && partialIsExact)
            {
                m.ChosenPath = afterClick.First(p => p.Count == partial.Count);
                ClearPrompt();
                _human.CompleteChoice();
                return true;
            }
            Refresh();
            return true;
        };
        Refresh();
    }

    // Add a single ShipLocation to the existing highlight set without clearing.
    private void AddHighlight(ShipLocation loc)
    {
        switch (loc)
        {
            case ShipLocation.OnNode n: _highlightNodes.Add(n.Node); break;
            case ShipLocation.OnGate gateLoc: _highlightGates.Add(gateLoc.Gate); break;
        }
        RenderMap();
    }

    private static bool StartsWith(IReadOnlyList<ShipLocation> path, IReadOnlyList<ShipLocation> prefix)
    {
        if (path.Count < prefix.Count) return false;
        for (int i = 0; i < prefix.Count; i++)
            if (!Mechanics.LocationsEqual(path[i], prefix[i])) return false;
        return true;
    }

    private void ClearPrompt()
    {
        PromptText.Text = "";
        _onHandCardClick = null;
        _onMineralCardClick = null;
        _onMapLocClick = null;
        _highlightNodes.Clear();
        _highlightGates.Clear();
        _legalHandCardIds = null;
        _handPromptOverride = null;
        _showMineralsInsteadOfHand = false;
        _mineralsToShow = null;
        ImpulseActionPanel.Children.Clear();
        Render();
    }

    private void HighlightLocations(IEnumerable<ShipLocation> locs)
    {
        _highlightNodes.Clear();
        _highlightGates.Clear();
        foreach (var l in locs)
        {
            switch (l)
            {
                case ShipLocation.OnNode n: _highlightNodes.Add(n.Node); break;
                case ShipLocation.OnGate g: _highlightGates.Add(g.Gate); break;
            }
        }
        RenderMap();
    }

    private static (int kind, int id) Key(ShipLocation l) => l switch
    {
        ShipLocation.OnNode n => (0, n.Node.Value),
        ShipLocation.OnGate g => (1, g.Gate.Value),
        _ => (-1, 0),
    };

    private static bool ContainsLoc(IEnumerable<ShipLocation> set, ShipLocation l)
    {
        var k = Key(l);
        return set.Any(x => Key(x) == k);
    }

    private sealed class LocComparer : IEqualityComparer<ShipLocation>
    {
        public bool Equals(ShipLocation? x, ShipLocation? y) =>
            x is not null && y is not null && Key(x) == Key(y);
        public int GetHashCode(ShipLocation obj) => Key(obj).GetHashCode();
    }

    private bool _gameOverShown;
    private void ShowGameOverIfDone()
    {
        if (_gameOverShown) return;
        _gameOverShown = true;
        Render();
        var ranked = _g.Players
            .OrderByDescending(p => p.Prestige)
            .ThenBy(p => p.Id.Value)
            .ToList();
        var winner = ranked[0];
        var lines = string.Join("\n", ranked.Select(p =>
            $"{(p.Id == winner.Id ? "★ " : "  ")}{p.Id} {p.Race.Name}: {p.Prestige} prestige"));
        var title = _g.IsGameOver ? "Game Over" : "Game Ended (Turn Limit / Deck Exhausted)";
        var headline = _g.IsGameOver
            ? $"{winner.Id} {winner.Race.Name} wins with {winner.Prestige} prestige!"
            : $"Game ended without a 20-prestige winner. Leader: {winner.Id} ({winner.Prestige}).";
        PromptText.Text = headline;
        MessageBox.Show(this, headline + "\n\nFinal standings:\n" + lines,
            title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---------- Render ----------
    private void Render()
    {
        TurnText.Text = _g.CurrentTurn.ToString();
        PhaseText.Text = _g.Phase.ToString();
        var active = _g.Player(_g.ActivePlayer);
        ActivePlayerText.Text = $"{active.Id} {active.Race.Name}";
        ActivePlayerSwatch.Background = PlayerBrush(active.Color);
        UpdateContextText();

        RenderMap();
        RenderImpulse();
        RenderPlayers();
        RenderHand(_g.Player(_human.Seat));
        RenderMinerals(_g.Player(_human.Seat));
    }

    // Show what card / source is currently driving the active prompt:
    // - During Plan resolution → "Resolving Plan: <card>"
    // - During Impulse resolution → "Resolving Impulse: <card>"
    // - During tech use / activation → from PendingEffect.Source
    private void UpdateContextText()
    {
        string ctx = "";
        if (_g.IsResolvingPlan && _g.CurrentlyResolvingPlanCardId is int planCardId
            && _g.CardsById.TryGetValue(planCardId, out var planCard))
        {
            ctx = $"▶ Resolving Plan: {planCard.ActionType}/{planCard.Color}/{planCard.Size} #{planCard.Id}";
        }
        else if (_g.Phase == GamePhase.ResolveImpulse
                 && _g.ImpulseCursor < _g.Impulse.Count)
        {
            var ic = _g.CardsById[_g.Impulse[_g.ImpulseCursor]];
            ctx = $"▶ Resolving Impulse: {ic.ActionType}/{ic.Color}/{ic.Size} #{ic.Id}";
        }
        else if (_g.PendingEffect?.Source is { } src)
        {
            int? cardId = src switch
            {
                EffectSource.ImpulseCard ic => ic.CardId,
                EffectSource.PlanCard pc => pc.CardId,
                EffectSource.TechEffect te => te.CardId,
                EffectSource.MapActivation ma => ma.CardId,
                _ => null,
            };
            if (cardId is int id && _g.CardsById.TryGetValue(id, out var c))
                ctx = $"▶ Effect: {c.ActionType}/{c.Color}/{c.Size} #{c.Id}";
        }
        ContextText.Text = ctx;
    }

    private void RenderMinerals(PlayerState p)
    {
        MineralsPanel.Children.Clear();
        MineralsLabel.Text = $"MINERALS ({p.Minerals.Count})";
        if (p.Minerals.Count == 0)
        {
            MineralsPanel.Children.Add(new TextBlock { Style = (Style)FindResource("Label"), Text = "(empty)" });
            return;
        }
        foreach (var id in p.Minerals)
        {
            var c = _g.CardsById[id];
            MineralsPanel.Children.Add(BuildCard(c, id, dimmed: false, mineral: false, highlighted: false));
        }
    }

    private void AppendLog(string line)
    {
        if (LogText.Text.Length > 0) LogText.Text += "\n";
        LogText.Text += line;
        LogScroll.ScrollToEnd();
    }

    private void RenderMap()
    {
        if (_g is null) return;
        MapCanvas.Children.Clear();

        double cx = MapCanvas.ActualWidth / 2;
        double cy = MapCanvas.ActualHeight / 2;
        if (cx <= 0 || cy <= 0) return;

        Point HexCenter(int q, int r)
        {
            double x = HexRadius * Math.Sqrt(3) * (q + r / 2.0);
            double y = HexRadius * 1.5 * r;
            return new Point(cx + x, cy + y);
        }

        // gates
        foreach (var gate in _g.Map.Gates)
        {
            var a = _g.Map.Node(gate.EndpointA);
            var b = _g.Map.Node(gate.EndpointB);
            var pa = HexCenter(a.AxialQ, a.AxialR);
            var pb = HexCenter(b.AxialQ, b.AxialR);
            bool hi = _highlightGates.Contains(gate.Id);
            var line = new Line
            {
                X1 = pa.X, Y1 = pa.Y, X2 = pb.X, Y2 = pb.Y,
                Stroke = hi ? (Brush)FindResource("Accent") : (Brush)FindResource("GateStroke"),
                StrokeThickness = hi ? 6 : 4,
                Cursor = hi ? Cursors.Hand : Cursors.Arrow,
            };
            if (hi)
            {
                var capturedId = gate.Id;
                line.MouseLeftButtonDown += (_, _) => _onMapLocClick?.Invoke(new ShipLocation.OnGate(capturedId));
            }
            MapCanvas.Children.Add(line);
        }

        // gate-mounted cruisers, grouped by (gate, owner) so multi-cruiser
        // fleets show a count.
        var cruiserGroups = _g.ShipPlacements
            .Where(s => s.Location is ShipLocation.OnGate)
            .GroupBy(s => (Gate: ((ShipLocation.OnGate)s.Location).Gate, s.Owner))
            .ToList();
        // Per-gate ownership tooltip text.
        var perGateTooltip = cruiserGroups
            .GroupBy(g => g.Key.Gate)
            .ToDictionary(
                grp => grp.Key,
                grp => string.Join(", ", grp.Select(og => $"{og.Key.Owner}: {og.Count()} cruiser{(og.Count() == 1 ? "" : "s")}")));
        // Stagger same-gate groups along the gate so they don't overlap.
        var perGateGroups = cruiserGroups
            .GroupBy(g => g.Key.Gate)
            .ToDictionary(grp => grp.Key, grp => grp.ToList());
        foreach (var grp in cruiserGroups)
        {
            var gate = _g.Map.Gate(grp.Key.Gate);
            var a = _g.Map.Node(gate.EndpointA);
            var b = _g.Map.Node(gate.EndpointB);
            var pa = HexCenter(a.AxialQ, a.AxialR);
            var pb = HexCenter(b.AxialQ, b.AxialR);
            var mid = new Point((pa.X + pb.X) / 2, (pa.Y + pb.Y) / 2);
            var siblings = perGateGroups[grp.Key.Gate];
            int idx = siblings.IndexOf(grp);
            int total = siblings.Count;
            // Offset perpendicular to the gate axis for siblings.
            double dx = pb.X - pa.X, dy = pb.Y - pa.Y;
            double len = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
            double nx = -dy / len, ny = dx / len;
            double offset = (idx - (total - 1) / 2.0) * 18;
            var pos = new Point(mid.X + nx * offset, mid.Y + ny * offset);

            int count = grp.Count();
            bool gateHighlighted = _highlightGates.Contains(grp.Key.Gate);
            // When this gate is a legal click target (origin / destination),
            // draw an accent-colored ring around the cruiser square so the
            // highlight is visible on top of the player-coloured fill.
            // Without this the gate's accent stroke is hidden behind the
            // square and the player can't tell the gate is selectable.
            if (gateHighlighted)
            {
                var ring = new Rectangle
                {
                    Width = 24, Height = 24,
                    Fill = Brushes.Transparent,
                    Stroke = (Brush)FindResource("Accent"),
                    StrokeThickness = 3,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(ring, pos.X - 12);
                Canvas.SetTop(ring, pos.Y - 12);
                Canvas.SetZIndex(ring, 13);
                MapCanvas.Children.Add(ring);
            }
            var sq = new Rectangle
            {
                Width = 16, Height = 16,
                Fill = PlayerBrush(_g.Player(grp.Key.Owner).Color),
                Stroke = gateHighlighted ? (Brush)FindResource("Accent") : Brushes.Black,
                StrokeThickness = gateHighlighted ? 2 : 1,
                IsHitTestVisible = true,
                ToolTip = perGateTooltip[grp.Key.Gate],
                Cursor = gateHighlighted ? Cursors.Hand : Cursors.Arrow,
            };
            Canvas.SetLeft(sq, pos.X - 8);
            Canvas.SetTop(sq, pos.Y - 8);
            // ZIndex 10 keeps cruisers (and their count badges) above the
            // node hexes drawn later in this Render() pass.
            Canvas.SetZIndex(sq, 10);
            // Forward clicks on the square to the same location-click
            // handler the gate line uses, so clicking the cruiser visual
            // selects its gate during fleet/sabotage prompts.
            var capturedGateId = grp.Key.Gate;
            sq.MouseLeftButtonDown += (_, e) =>
            {
                if (_onMapLocClick?.Invoke(new ShipLocation.OnGate(capturedGateId)) == true)
                    e.Handled = true;
            };
            MapCanvas.Children.Add(sq);

            if (count > 1)
            {
                // Count badge: white circle with black border (high-contrast),
                // offset to the upper-right of the cruiser square.
                double bx = pos.X + 8;
                double by = pos.Y - 8;
                var badgeBg = new Ellipse
                {
                    Width = 16, Height = 16,
                    Fill = Brushes.White,
                    Stroke = Brushes.Black, StrokeThickness = 1.5,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(badgeBg, bx - 8);
                Canvas.SetTop(badgeBg, by - 8);
                Canvas.SetZIndex(badgeBg, 11);
                MapCanvas.Children.Add(badgeBg);
                var countLabel = new TextBlock
                {
                    Text = count.ToString(),
                    Foreground = Brushes.Black,
                    FontSize = 11, FontWeight = FontWeights.Bold,
                    FontFamily = new FontFamily("Segoe UI"),
                    IsHitTestVisible = false,
                };
                countLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(countLabel, bx - countLabel.DesiredSize.Width / 2);
                Canvas.SetTop(countLabel, by - countLabel.DesiredSize.Height / 2);
                Canvas.SetZIndex(countLabel, 12);
                MapCanvas.Children.Add(countLabel);
            }
        }

        // nodes
        foreach (var node in _g.Map.Nodes)
        {
            var p = HexCenter(node.AxialQ, node.AxialR);
            bool hi = _highlightNodes.Contains(node.Id);
            _g.NodeCards.TryGetValue(node.Id, out var cardState);
            bool isFaceDown = cardState is NodeCardState.FaceDown;
            bool isFaceUp = cardState is NodeCardState.FaceUp;
            var hex = BuildHexPolygon(p, HexRadius * 0.92);
            hex.Fill = node.IsSectorCore
                ? (Brush)FindResource("SectorCoreFill")
                : isFaceDown
                    ? (Brush)FindResource("BgPanel") // darker fill for face-down
                    : (Brush)FindResource("NodeFill");
            // For face-up nodes, use the card's color as the stroke.
            Brush faceUpStroke = (Brush)FindResource("NodeStroke");
            if (isFaceUp && cardState is NodeCardState.FaceUp fu)
                faceUpStroke = CardBrush(_g.CardsById[fu.CardId].Color);
            hex.Stroke = hi
                ? (Brush)FindResource("Accent")
                : node.IsSectorCore
                    ? (Brush)FindResource("SectorCoreStroke")
                    : (node.IsHome
                        ? PlayerBrush(_g.Player(node.Owner!.Value).Color)
                        : isFaceUp
                            ? faceUpStroke
                            : (Brush)FindResource("NodeStroke"));
            hex.StrokeThickness = hi ? 4 : (node.IsHome || node.IsSectorCore || isFaceUp ? 3 : 1.5);
            if (hi)
            {
                var capturedId = node.Id;
                hex.Cursor = Cursors.Hand;
                hex.MouseLeftButtonDown += (_, _) => _onMapLocClick?.Invoke(new ShipLocation.OnNode(capturedId));
            }
            // Hover any face-up node to show its card in the detail panel.
            if (isFaceUp && cardState is NodeCardState.FaceUp fuHover)
            {
                var hoverCard = _g.CardsById[fuHover.CardId];
                hex.MouseEnter += (_, _) => ShowCardDetail(hoverCard);
            }
            MapCanvas.Children.Add(hex);

            var label = new TextBlock
            {
                Text = node.IsSectorCore ? "CORE" : node.Id.ToString(),
                Foreground = (Brush)FindResource("FgMuted"),
                FontSize = 10, FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                IsHitTestVisible = false,
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, p.X - label.DesiredSize.Width / 2);
            Canvas.SetTop(label, p.Y - HexRadius * 0.92 + 4);
            MapCanvas.Children.Add(label);

            // Face-down indicator (large "?" centered on the hex).
            if (isFaceDown)
            {
                var q = new TextBlock
                {
                    Text = "?",
                    Foreground = (Brush)FindResource("FgMuted"),
                    FontSize = 28, FontWeight = FontWeights.Bold,
                    FontFamily = new FontFamily("Segoe UI"),
                    IsHitTestVisible = false,
                };
                q.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(q, p.X - q.DesiredSize.Width / 2);
                Canvas.SetTop(q, p.Y - q.DesiredSize.Height / 2);
                MapCanvas.Children.Add(q);
            }
            // Face-up: small action-type label so the player sees what's there.
            else if (isFaceUp && cardState is NodeCardState.FaceUp fup)
            {
                var card = _g.CardsById[fup.CardId];
                var t = new TextBlock
                {
                    Text = card.ActionType.ToString().ToUpperInvariant(),
                    Foreground = CardBrush(card.Color),
                    FontSize = 10, FontWeight = FontWeights.SemiBold,
                    FontFamily = new FontFamily("Segoe UI"),
                    IsHitTestVisible = false,
                };
                t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(t, p.X - t.DesiredSize.Width / 2);
                Canvas.SetTop(t, p.Y - 6);
                MapCanvas.Children.Add(t);
            }
        }

        // Enlarged transparent hit-target overlays for highlighted gates.
        // The visible line between hex polygons is narrow (~6px); without
        // this overlay, clicking a highlighted gate is fiddly.
        foreach (var gateId in _highlightGates)
        {
            var gate = _g.Map.Gate(gateId);
            var a = _g.Map.Node(gate.EndpointA);
            var b = _g.Map.Node(gate.EndpointB);
            var pa = HexCenter(a.AxialQ, a.AxialR);
            var pb = HexCenter(b.AxialQ, b.AxialR);
            var mid = new Point((pa.X + pb.X) / 2, (pa.Y + pb.Y) / 2);
            var hit = new Ellipse
            {
                Width = 36, Height = 36,
                Fill = Brushes.Transparent,
                Cursor = Cursors.Hand,
            };
            Canvas.SetLeft(hit, mid.X - 18);
            Canvas.SetTop(hit, mid.Y - 18);
            var capturedId = gateId;
            hit.MouseLeftButtonDown += (_, _) =>
                _onMapLocClick?.Invoke(new ShipLocation.OnGate(capturedId));
            MapCanvas.Children.Add(hit);
        }

        // node-mounted transports
        var byNode = _g.ShipPlacements
            .Where(s => s.Location is ShipLocation.OnNode)
            .GroupBy(s => ((ShipLocation.OnNode)s.Location).Node);
        foreach (var grp in byNode)
        {
            var node = _g.Map.Node(grp.Key);
            var center = HexCenter(node.AxialQ, node.AxialR);
            bool nodeHighlighted = _highlightNodes.Contains(grp.Key);
            int n = grp.Count();
            int i = 0;
            foreach (var ship in grp)
            {
                double angle = (Math.PI * 2) * i / Math.Max(n, 3);
                double radius = n == 1 ? 0 : HexRadius * 0.35;
                double sx = center.X + radius * Math.Cos(angle);
                double sy = center.Y + radius * Math.Sin(angle) + 8;
                // Highlight ring under the dot when this node is a legal
                // click target — same idea as the cruiser highlight but
                // for transports. Drawn first so the dot sits on top.
                if (nodeHighlighted)
                {
                    var ring = new Ellipse
                    {
                        Width = 20, Height = 20,
                        Fill = Brushes.Transparent,
                        Stroke = (Brush)FindResource("Accent"),
                        StrokeThickness = 3,
                        IsHitTestVisible = false,
                    };
                    Canvas.SetLeft(ring, sx - 10);
                    Canvas.SetTop(ring, sy - 10);
                    Canvas.SetZIndex(ring, 12);
                    MapCanvas.Children.Add(ring);
                }
                var dot = new Ellipse
                {
                    Width = 12, Height = 12,
                    Fill = PlayerBrush(_g.Player(ship.Owner).Color),
                    Stroke = nodeHighlighted ? (Brush)FindResource("Accent") : Brushes.Black,
                    StrokeThickness = nodeHighlighted ? 2 : 1,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(dot, sx - 6);
                Canvas.SetTop(dot, sy - 6);
                Canvas.SetZIndex(dot, 11);
                MapCanvas.Children.Add(dot);
                i++;
            }
        }
    }

    private static Polygon BuildHexPolygon(Point center, double radius)
    {
        var poly = new Polygon();
        for (int i = 0; i < 6; i++)
        {
            double angle = Math.PI / 180 * (60 * i - 30);
            poly.Points.Add(new Point(
                center.X + radius * Math.Cos(angle),
                center.Y + radius * Math.Sin(angle)));
        }
        return poly;
    }

    private void RenderImpulse()
    {
        ImpulsePanel.Children.Clear();
        // During Phase 4 (UsePlan), the active player's Plan replaces the
        // Impulse track in this column so the player can see what's being
        // resolved.
        bool resolvingPlan = _g.Phase == GamePhase.UsePlan || _g.IsResolvingPlan;
        if (resolvingPlan)
        {
            var p = _g.Player(_g.ActivePlayer);
            ImpulseTrackLabel.Text = $"PLAN ({_g.ActivePlayer})";
            ImpulseTrackSubLabel.Text = _g.CurrentlyResolvingPlanCardId is null
                ? "resolving — top first"
                : "▶ resolving the highlighted card";

            if (_g.CurrentlyResolvingPlanCardId is int currId)
            {
                ImpulsePanel.Children.Add(new TextBlock
                {
                    Style = (Style)FindResource("Label"),
                    Text = "▶ NOW RESOLVING",
                    Foreground = (Brush)FindResource("Accent"),
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 0, 0, 4),
                });
                ImpulsePanel.Children.Add(BuildPlanCard(currId, resolving: true));
                if (p.Plan.Count > 0)
                {
                    ImpulsePanel.Children.Add(new Border
                    {
                        Height = 1,
                        Background = (Brush)FindResource("GateStroke"),
                        Margin = new Thickness(0, 8, 0, 8),
                    });
                    ImpulsePanel.Children.Add(new TextBlock
                    {
                        Style = (Style)FindResource("Label"),
                        Text = $"QUEUED ({p.Plan.Count}) — top resolves next",
                        Margin = new Thickness(0, 0, 0, 4),
                    });
                }
            }
            for (int i = 0; i < p.Plan.Count; i++)
            {
                ImpulsePanel.Children.Add(BuildPlanCard(p.Plan[i], resolving: false));
            }
            if (p.Plan.Count == 0 && _g.CurrentlyResolvingPlanCardId is null)
                ImpulsePanel.Children.Add(new TextBlock { Style = (Style)FindResource("Label"), Text = "(empty)" });
            return;
        }

        ImpulseTrackLabel.Text = "IMPULSE TRACK";
        ImpulseTrackSubLabel.Text = "oldest → newest (FIFO)";
        for (int i = 0; i < _g.Impulse.Count; i++)
        {
            var card = _g.CardsById[_g.Impulse[i]];
            ImpulsePanel.Children.Add(BuildMiniCard(card, isCursor: i == _g.ImpulseCursor && _g.Phase == GamePhase.ResolveImpulse));
        }
    }

    private void RenderPlayers()
    {
        PlayersPanel.Children.Clear();
        foreach (var p in _g.Players)
            PlayersPanel.Children.Add(BuildPlayerPanel(p));
    }

    private UIElement BuildPlayerPanel(PlayerState p)
    {
        var border = new Border
        {
            Background = (Brush)FindResource("BgPanel2"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 6),
            BorderThickness = new Thickness(0, 0, 0, 2),
            BorderBrush = PlayerBrush(p.Color),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        border.MouseEnter += (_, _) => ShowPlayerDetail(p);
        var stack = new StackPanel();
        var headerRow = new StackPanel { Orientation = Orientation.Horizontal };
        headerRow.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = PlayerBrush(p.Color), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        headerRow.Children.Add(new TextBlock { Style = (Style)FindResource("Heading"), Text = $"{p.Id} {p.Race.Name}" });
        headerRow.Children.Add(new TextBlock { Style = (Style)FindResource("Label"), Text = $"  prestige", VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 0, 4, 1) });
        headerRow.Children.Add(new TextBlock { Style = (Style)FindResource("Heading"), Text = p.Prestige.ToString(), Foreground = (Brush)FindResource("Accent") });
        stack.Children.Add(headerRow);

        var bar = new Grid { Height = 6, Margin = new Thickness(0, 4, 0, 4) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, p.Prestige), GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1, 20 - p.Prestige), GridUnitType.Star) });
        var fill = new Border { Background = (Brush)FindResource("Accent"), CornerRadius = new CornerRadius(2) };
        var rest = new Border { Background = (Brush)FindResource("BgDark"), CornerRadius = new CornerRadius(2) };
        Grid.SetColumn(fill, 0); Grid.SetColumn(rest, 1);
        bar.Children.Add(fill); bar.Children.Add(rest);
        stack.Children.Add(bar);

        stack.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Label"),
            Text = $"hand {p.Hand.Count}   plan {p.Plan.Count}   ships available {p.ShipsAvailable}",
        });

        // Per-color gem totals (sum of mineral card sizes). The boost
        // formula is gems / 2, so this is what determines power.
        int RedGems = p.Minerals.Where(id => _g.CardsById[id].Color == CardColor.Red).Sum(id => _g.CardsById[id].Size);
        int BlueGems = p.Minerals.Where(id => _g.CardsById[id].Color == CardColor.Blue).Sum(id => _g.CardsById[id].Size);
        int GreenGems = p.Minerals.Where(id => _g.CardsById[id].Color == CardColor.Green).Sum(id => _g.CardsById[id].Size);
        int YellowGems = p.Minerals.Where(id => _g.CardsById[id].Color == CardColor.Yellow).Sum(id => _g.CardsById[id].Size);
        var gemRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        gemRow.Children.Add(new TextBlock { Style = (Style)FindResource("Label"), Text = $"GEMS ({p.Minerals.Count} cards)  ", VerticalAlignment = VerticalAlignment.Center });
        void AddGem(CardColor color, int n)
        {
            var swatch = new Border
            {
                Width = 12, Height = 12, CornerRadius = new CornerRadius(6),
                Background = CardBrush(color),
                Margin = new Thickness(0, 0, 3, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            gemRow.Children.Add(swatch);
            gemRow.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("Body"),
                Text = $"{n}  ",
                FontWeight = n > 0 ? FontWeights.Bold : FontWeights.Normal,
                Foreground = n > 0 ? (Brush)FindResource("FgPrimary") : (Brush)FindResource("FgMuted"),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        AddGem(CardColor.Red, RedGems);
        AddGem(CardColor.Blue, BlueGems);
        AddGem(CardColor.Green, GreenGems);
        AddGem(CardColor.Yellow, YellowGems);
        stack.Children.Add(gemRow);

        // Tech slots
        var techRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        techRow.Children.Add(new TextBlock { Style = (Style)FindResource("Label"), Text = "L  ", VerticalAlignment = VerticalAlignment.Center });
        techRow.Children.Add(BuildTechChip(p.Techs.Left, clickable: false, onClick: null));
        techRow.Children.Add(new TextBlock { Style = (Style)FindResource("Label"), Text = "R  ", VerticalAlignment = VerticalAlignment.Center });
        techRow.Children.Add(BuildTechChip(p.Techs.Right, clickable: false, onClick: null));
        stack.Children.Add(techRow);

        border.Child = stack;
        return border;
    }

    private void RenderHand(PlayerState p)
    {
        HandPanel.Children.Clear();
        if (_showMineralsInsteadOfHand && _mineralsToShow is not null)
        {
            HandLabel.Text = $"→ MINERALS ({p.Id}) — pick one to refine";
            HandLabel.Foreground = (Brush)FindResource("Accent");
            HandLabel.FontSize = 13;
            foreach (var id in _mineralsToShow)
            {
                var c = _g.CardsById[id];
                HandPanel.Children.Add(BuildCard(c, id, dimmed: false, mineral: true, highlighted: true));
            }
            if (_mineralsToShow.Count == 0)
                HandPanel.Children.Add(new TextBlock { Style = (Style)FindResource("Label"), Text = "(no eligible minerals)" });
            return;
        }

        if (_handPromptOverride is not null)
        {
            HandLabel.Text = _handPromptOverride;
            HandLabel.Foreground = (Brush)FindResource("Accent");
            HandLabel.FontSize = 13;
        }
        else
        {
            HandLabel.Text = $"HAND ({p.Id} {p.Race.Name})";
            HandLabel.Foreground = (Brush)FindResource("FgMuted");
            HandLabel.FontSize = 11;
        }

        foreach (var id in p.Hand)
        {
            var c = _g.CardsById[id];
            bool legal = _legalHandCardIds is null || _legalHandCardIds.Contains(id);
            bool dim = _legalHandCardIds is not null && !legal;
            // Highlighted = a hand-pick prompt is active and this card is legal.
            bool highlighted = _legalHandCardIds is not null && legal;
            HandPanel.Children.Add(BuildCard(c, id, dimmed: dim, mineral: false, highlighted: highlighted));
        }
        if (p.Hand.Count == 0)
            HandPanel.Children.Add(new TextBlock { Style = (Style)FindResource("Label"), Text = "(empty)" });
    }

    private UIElement BuildCard(Card c, int id, bool dimmed = false, bool mineral = false, bool highlighted = false)
    {
        var border = new Border
        {
            Width = 168, Height = 110,
            Margin = new Thickness(0, 0, 8, 0),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)FindResource("BgPanel"),
            BorderBrush = highlighted ? (Brush)FindResource("Accent") : CardBrush(c.Color),
            BorderThickness = new Thickness(highlighted ? 4 : 2),
            Padding = new Thickness(highlighted ? 6 : 8),
            Cursor = dimmed ? Cursors.Arrow : Cursors.Hand,
            Opacity = dimmed ? 0.3 : 1.0,
        };
        border.MouseLeftButtonDown += (_, _) =>
        {
            if (mineral) _onMineralCardClick?.Invoke(id);
            else _onHandCardClick?.Invoke(id);
        };
        HookCardHover(border, c);
        var stack = new StackPanel();
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Style = (Style)FindResource("Heading"), Foreground = CardBrush(c.Color), Text = c.ActionType.ToString().ToUpperInvariant() });
        header.Children.Add(new TextBlock { Style = (Style)FindResource("Label"), Text = $"  size {c.Size}   #{c.Id}", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        stack.Children.Add(header);
        stack.Children.Add(new TextBlock { Style = (Style)FindResource("Body"), Text = c.EffectText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), FontSize = 11 });
        border.Child = stack;
        return border;
    }

    // Larger plan-card for the Phase-4 plan column: shows effect text directly,
    // hover still pops detail. Compact width so the column fits the panel.
    private FrameworkElement BuildPlanCard(int cardId, bool resolving)
    {
        var c = _g.CardsById[cardId];
        var accent = (Brush)FindResource("Accent");
        var border = new Border
        {
            Width = resolving ? 260 : 220,
            CornerRadius = new CornerRadius(6),
            Background = resolving
                ? new SolidColorBrush(SystemColor.FromArgb(0x33, 0x5B, 0xC0, 0xFF)) // tinted accent
                : (Brush)FindResource("BgPanel"),
            BorderBrush = resolving ? accent : CardBrush(c.Color),
            BorderThickness = new Thickness(resolving ? 5 : 2),
            Padding = new Thickness(resolving ? 10 : 8, resolving ? 8 : 6, resolving ? 10 : 8, resolving ? 8 : 6),
            Margin = new Thickness(0, 0, 0, 6),
            Effect = resolving
                ? new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = SystemColor.FromRgb(0x5B, 0xC0, 0xFF),
                    BlurRadius = 14, ShadowDepth = 0, Opacity = 0.9,
                }
                : null,
        };
        var stack = new StackPanel();
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        if (resolving)
        {
            header.Children.Add(new TextBlock
            {
                Text = "▶ ",
                Foreground = accent,
                FontSize = 16, FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        header.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = CardBrush(c.Color), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        header.Children.Add(new TextBlock { Style = (Style)FindResource("Heading"), Foreground = CardBrush(c.Color), Text = c.ActionType.ToString().ToUpperInvariant(), FontSize = resolving ? 16 : 13, FontWeight = FontWeights.Bold });
        header.Children.Add(new TextBlock { Style = (Style)FindResource("Label"), Text = $"  size {c.Size}   #{c.Id}", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
        stack.Children.Add(header);
        stack.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Body"),
            Text = c.EffectText,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = resolving ? 13 : 11,
            FontWeight = resolving ? FontWeights.SemiBold : FontWeights.Normal,
        });
        border.Child = stack;
        HookCardHover(border, c);
        return border;
    }

    private FrameworkElement BuildMiniCard(Card c, bool isCursor)
    {
        var accent = (Brush)FindResource("Accent");
        var border = new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = isCursor
                ? new SolidColorBrush(SystemColor.FromArgb(0x33, 0x5B, 0xC0, 0xFF))
                : (Brush)FindResource("BgPanel2"),
            BorderBrush = isCursor ? accent : CardBrush(c.Color),
            BorderThickness = new Thickness(isCursor ? 4 : 1),
            Padding = new Thickness(isCursor ? 8 : 6, isCursor ? 5 : 3, isCursor ? 8 : 6, isCursor ? 5 : 3),
            Margin = new Thickness(0, 0, 0, isCursor ? 6 : 4),
            Effect = isCursor
                ? new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = SystemColor.FromRgb(0x5B, 0xC0, 0xFF),
                    BlurRadius = 12, ShadowDepth = 0, Opacity = 0.9,
                }
                : null,
        };
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        if (isCursor)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "▶ ",
                Foreground = accent,
                FontSize = 14, FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        stack.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = CardBrush(c.Color), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        stack.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Body"),
            Text = $"{c.ActionType} {c.Size}",
            FontWeight = isCursor ? FontWeights.Bold : FontWeights.Normal,
            FontSize = isCursor ? 13 : 12,
        });
        stack.Children.Add(new TextBlock { Style = (Style)FindResource("Label"), Text = $"  #{c.Id}", VerticalAlignment = VerticalAlignment.Center });
        border.Child = stack;
        HookCardHover(border, c);
        return border;
    }

    private Button BuildButton(string text, Action onClick)
    {
        var btn = new Button
        {
            Content = text,
            Margin = new Thickness(0, 0, 0, 4),
            Padding = new Thickness(8, 4, 8, 4),
            Background = (Brush)FindResource("BgPanel2"),
            Foreground = (Brush)FindResource("FgPrimary"),
            BorderBrush = (Brush)FindResource("Accent"),
            BorderThickness = new Thickness(1),
            FontFamily = new FontFamily("Segoe UI"),
            FontWeight = FontWeights.SemiBold,
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private Brush PlayerBrush(PlayerColor c) => c switch
    {
        PlayerColor.Blue => (Brush)FindResource("PlayerBlue"),
        PlayerColor.Green => (Brush)FindResource("PlayerGreen"),
        PlayerColor.Purple => (Brush)FindResource("PlayerPurple"),
        PlayerColor.Red => (Brush)FindResource("PlayerRed"),
        PlayerColor.White => (Brush)FindResource("PlayerWhite"),
        PlayerColor.Yellow => (Brush)FindResource("PlayerYellow"),
        _ => Brushes.Gray,
    };

    private Brush CardBrush(CardColor c) => c switch
    {
        CardColor.Blue => (Brush)FindResource("CardBlue"),
        CardColor.Yellow => (Brush)FindResource("CardYellow"),
        CardColor.Red => (Brush)FindResource("CardRed"),
        CardColor.Green => (Brush)FindResource("CardGreen"),
        _ => Brushes.Gray,
    };
}
