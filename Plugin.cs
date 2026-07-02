using System;
using System.IO;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ATKTip.Data;
using ATKTip.Windows;

namespace ATKTip;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/ATKTip";
    private const string CommandAlias = "/atktip";
    private const string CommandAliasHams = "/hams";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IClientState clientState;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly WindowSystem windowSystem = new("ATKTip");

    public Configuration Configuration { get; }
    public string ConfigDirectory { get; }
    public TimelineStore TimelineStore { get; }
    public UiSettingsStore UiSettingsStore { get; }
    public TimelineUserStateStore TimelineUserStateStore { get; }
    public CustomTimelineStore CustomTimelineStore { get; }
    public FFLogsClient FFLogsClient { get; }
    public TimelineAggregator Aggregator { get; }
    public Data.RecastDatabase RecastDatabase { get; }
    public ActionStateDatabase ActionStateDatabase { get; }
    public EnemyCastTracker EnemyCastTracker { get; }

    private readonly MainWindow mainWindow;
    private readonly OverlayWindow overlayWindow;
    private readonly AutoModalWindow autoModalWindow;
    private readonly QuickPickWindow quickPickWindow;
    public  MainWindow MainWindow => mainWindow;
    private readonly EncounterTracker encounterTracker;
    private readonly AntsController antsController;

    public OverlayWindow OverlayWindow => overlayWindow;
    public AutoModalWindow AutoModalWindow => autoModalWindow;
    public EncounterTracker EncounterTracker => encounterTracker;

    public ITextureProvider TextureProvider { get; }
    public IDataManager DataManager { get; }
    public IDtrBar DtrBar { get; }

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        ICondition condition,
        IDutyState dutyState,
        ITextureProvider textureProvider,
        IDataManager dataManager,
        IDtrBar dtrBar,
        IClientState clientState,
        IObjectTable objectTable,
        IFramework framework,
        IGameInteropProvider gameInterop,
        IGameGui gameGui,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.clientState = clientState;
        this.framework = framework;
        this.log = log;
        TextureProvider = textureProvider;
        DataManager = dataManager;
        DtrBar = dtrBar;

        // Load or create config
        Configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        var hadLegacySerializedRuntimeState = PurgeLegacySerializedRuntimeState(Configuration);
        ConfigDirectory = pluginInterface.GetPluginConfigDirectory();

        // Data layer
        var dataDir = Path.Combine(ConfigDirectory, "data");
        Directory.CreateDirectory(dataDir);

        TimelineStore  = new TimelineStore(dataDir, log);
        UiSettingsStore = new UiSettingsStore(Path.Combine(dataDir, "ui-settings.json"), log);
        TimelineUserStateStore = new TimelineUserStateStore(Path.Combine(dataDir, "timeline-ui-state.json"), log);
        CustomTimelineStore = new CustomTimelineStore(dataDir, log);
        TimelineStore.Load();
        UiSettingsStore.LoadInto(Configuration);
        TimelineUserStateStore.LoadInto(Configuration);
        CustomTimelineStore.LoadInto(Configuration);
        TimelineUserStateStore.SaveFrom(Configuration);
        if (hadLegacySerializedRuntimeState)
            SaveConfig();
        FFLogsClient   = new FFLogsClient(Configuration, log);
        RecastDatabase = new Data.RecastDatabase(dataManager, log);
        ActionStateDatabase = new ActionStateDatabase(RecastDatabase);
        Aggregator     = new TimelineAggregator(log, RecastDatabase);
        EnemyCastTracker = new EnemyCastTracker(this, objectTable, framework, gameInterop, log);

        // Windows
        mainWindow = new MainWindow(this, log);
        overlayWindow = new OverlayWindow(this, condition, dutyState, objectTable, framework, gameInterop, log);
        autoModalWindow = new AutoModalWindow();
        quickPickWindow = new QuickPickWindow(this);

        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(overlayWindow);
        windowSystem.AddWindow(autoModalWindow);
        windowSystem.AddWindow(quickPickWindow);
        overlayWindow.StopPreview();

        // Zone + combat tracker — auto-loads timelines when entering mapped instances
        encounterTracker = new EncounterTracker(this, clientState, objectTable, condition, dutyState, dataManager, framework, log);

        // Hotbar ants — hooks IsActionHighlighted to drive FFXIV's native combo/proc animation
        antsController = new AntsController(this, gameGui, gameInterop);

        // Refresh recast times from live ActionManager after login (applies trait-based reductions).
        // Also refresh immediately if already logged in (plugin reload while in-game).
        clientState.Login += OnLogin;
        if (clientState.IsLoggedIn)
            framework.RunOnFrameworkThread(() => RecastDatabase.RefreshFromLive(log));

        // Hooks
        pluginInterface.UiBuilder.Draw += windowSystem.Draw;
        pluginInterface.UiBuilder.Draw += antsController.Draw;
        pluginInterface.UiBuilder.OpenConfigUi += () => mainWindow.FocusConfigTab();
        pluginInterface.UiBuilder.OpenMainUi += () => mainWindow.Toggle();

        var commandInfo = new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the ATKTip timeline window.",
        };
        commandManager.AddHandler(CommandName, commandInfo);
        if (!string.Equals(CommandAlias, CommandName, StringComparison.Ordinal))
            commandManager.AddHandler(CommandAlias, commandInfo);
        if (!string.Equals(CommandAliasHams, CommandName, StringComparison.Ordinal) &&
            !string.Equals(CommandAliasHams, CommandAlias, StringComparison.Ordinal))
        {
            commandManager.AddHandler(CommandAliasHams, commandInfo);
        }
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "config":
                mainWindow.FocusConfigTab();
                break;
            case "overlay":
                overlayWindow.Toggle();
                break;
            case "preview":
                quickPickWindow.Toggle();
                break;
            default:
                mainWindow.Toggle();
                break;
        }
    }

    public void SaveConfig()
    {
        pluginInterface.SavePluginConfig(Configuration);
    }

    public void SaveUiSettings()
    {
        UiSettingsStore.SaveFrom(Configuration);
    }

    public void SaveTimelineUserState()
    {
        TimelineUserStateStore.SaveFrom(Configuration);
    }

    private void OnLogin()
    {
        framework.RunOnFrameworkThread(() => RecastDatabase.RefreshFromLive(log));
    }

    private static bool PurgeLegacySerializedRuntimeState(Configuration configuration)
    {
        var hadLegacyState =
            configuration.HiddenAbilities.Count > 0 ||
            configuration.AbilityFreqThresholds.Count > 0 ||
            configuration.AutoTimelineDisabledAbilities.Count > 0 ||
            configuration.CustomTimelines.Count > 0 ||
            configuration.TimelineGroups.Count > 0 ||
            configuration.TimelineGroupAssignments.Count > 0 ||
            configuration.TimelineNextLinks.Count > 0;

        if (!hadLegacyState)
            return false;

        configuration.HiddenAbilities = [];
        configuration.AbilityFreqThresholds = [];
        configuration.AutoTimelineDisabledAbilities = [];
        configuration.CustomTimelines = [];
        configuration.TimelineGroups = [];
        configuration.TimelineGroupAssignments = [];
        configuration.TimelineNextLinks = [];
        return true;
    }

    public void Dispose()
    {
        clientState.Login -= OnLogin;
        commandManager.RemoveHandler(CommandName);
        if (!string.Equals(CommandAlias, CommandName, StringComparison.Ordinal))
            commandManager.RemoveHandler(CommandAlias);
        if (!string.Equals(CommandAliasHams, CommandName, StringComparison.Ordinal) &&
            !string.Equals(CommandAliasHams, CommandAlias, StringComparison.Ordinal))
        {
            commandManager.RemoveHandler(CommandAliasHams);
        }
        pluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        pluginInterface.UiBuilder.Draw -= antsController.Draw;
        windowSystem.RemoveAllWindows();
        antsController.Dispose();
        encounterTracker.Dispose();
        overlayWindow.Dispose();
        EnemyCastTracker.Dispose();
        FFLogsClient.Dispose();
    }
}
