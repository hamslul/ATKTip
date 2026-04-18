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
    private const string CommandName = "/atktip";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IClientState clientState;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly WindowSystem windowSystem = new("ATKTip");

    public Configuration Configuration { get; }
    public TimelineStore TimelineStore { get; }
    public FFLogsClient FFLogsClient { get; }
    public TimelineAggregator Aggregator { get; }
    public Data.RecastDatabase RecastDatabase { get; }

    private readonly MainWindow mainWindow;
    private readonly OverlayWindow overlayWindow;
    public  MainWindow MainWindow => mainWindow;
    private readonly EncounterTracker encounterTracker;
    private readonly AntsController antsController;

    public ConfigWindow ConfigWindow { get; }
    public OverlayWindow OverlayWindow => overlayWindow;
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

        // Data layer
        var dataDir = Path.Combine(pluginInterface.GetPluginConfigDirectory(), "data");
        Directory.CreateDirectory(dataDir);

        TimelineStore = new TimelineStore(dataDir, log);
        FFLogsClient = new FFLogsClient(Configuration, log);
        Aggregator = new TimelineAggregator(log);
        RecastDatabase = new Data.RecastDatabase(dataManager, log);

        // Windows
        ConfigWindow = new ConfigWindow(this);
        mainWindow = new MainWindow(this, log);
        overlayWindow = new OverlayWindow(this, condition, dutyState, framework, log);

        windowSystem.AddWindow(ConfigWindow);
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(overlayWindow);

        // Zone + combat tracker — auto-loads timelines when entering mapped instances
        encounterTracker = new EncounterTracker(this, clientState, objectTable, condition, dataManager, framework, log);

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

        commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the ATKTip timeline window.",
        });
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
            default:
                mainWindow.Toggle();
                break;
        }
    }

    public void SaveConfig()
    {
        pluginInterface.SavePluginConfig(Configuration);
    }

    private void OnLogin()
    {
        framework.RunOnFrameworkThread(() => RecastDatabase.RefreshFromLive(log));
    }

    public void Dispose()
    {
        clientState.Login -= OnLogin;
        commandManager.RemoveHandler(CommandName);
        pluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        pluginInterface.UiBuilder.Draw -= antsController.Draw;
        windowSystem.RemoveAllWindows();
        antsController.Dispose();
        encounterTracker.Dispose();
        overlayWindow.Dispose();
        FFLogsClient.Dispose();
    }
}
