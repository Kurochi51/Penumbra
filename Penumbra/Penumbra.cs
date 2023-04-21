using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Plugin;
using ImGuiNET;
using Lumina.Excel.GeneratedSheets;
using Microsoft.Extensions.DependencyInjection;
using OtterGui;
using OtterGui.Log;
using Penumbra.Api;
using Penumbra.Api.Enums;
using Penumbra.UI;
using Penumbra.Util;
using Penumbra.Collections;
using Penumbra.Collections.Cache;
using Penumbra.GameData.Actors;
using Penumbra.Interop.ResourceLoading;
using Penumbra.Interop.PathResolving;
using CharacterUtility = Penumbra.Interop.Services.CharacterUtility;
using DalamudUtil = Dalamud.Utility.Util;
using ResidentResourceManager = Penumbra.Interop.Services.ResidentResourceManager;
using Penumbra.Services;
using Penumbra.Interop.Services;
using Penumbra.Mods.Manager;
using Penumbra.Collections.Manager;
using Penumbra.Mods;

namespace Penumbra;

public class Penumbra : IDalamudPlugin
{
    public string Name
        => "Penumbra";

    public static Logger        Log         { get; private set; } = null!;
    public static ChatService   ChatService { get; private set; } = null!;

    public static CharacterUtility  CharacterUtility  { get; private set; } = null!;
    public static CollectionManager CollectionManager { get; private set; } = null!;

    public readonly RedrawService RedrawService;
    public readonly ModFileSystem ModFileSystem;
    public          HttpApi       HttpApi = null!;
    internal        ConfigWindow? ConfigWindow { get; private set; }

    private readonly ValidityChecker         _validityChecker;
    private readonly ResidentResourceManager _residentResources;
    private readonly TempModManager          _tempMods;
    private readonly TempCollectionManager   _tempCollections;
    private readonly ModManager              _modManager;
    private readonly Configuration           _config;
    private          PenumbraWindowSystem?   _windowSystem;
    private          bool                    _disposed;

    private readonly PenumbraNew _tmp;

    public Penumbra(DalamudPluginInterface pluginInterface)
    {
        Log = PenumbraNew.Log;
        try
        {
            _tmp             = new PenumbraNew(this, pluginInterface);
            ChatService      = _tmp.Services.GetRequiredService<ChatService>();
            _validityChecker = _tmp.Services.GetRequiredService<ValidityChecker>();
            _tmp.Services.GetRequiredService<BackupService>();
            _config             = _tmp.Services.GetRequiredService<Configuration>();
            CharacterUtility   = _tmp.Services.GetRequiredService<CharacterUtility>();
            _tempMods          = _tmp.Services.GetRequiredService<TempModManager>();
            _residentResources = _tmp.Services.GetRequiredService<ResidentResourceManager>();
            _tmp.Services.GetRequiredService<ResourceManagerService>();
            _modManager       = _tmp.Services.GetRequiredService<ModManager>();
            CollectionManager = _tmp.Services.GetRequiredService<CollectionManager>();
            _tempCollections  = _tmp.Services.GetRequiredService<TempCollectionManager>();
            ModFileSystem     = _tmp.Services.GetRequiredService<ModFileSystem>();
            RedrawService     = _tmp.Services.GetRequiredService<RedrawService>();
            _tmp.Services.GetRequiredService<ResourceService>(); // Initialize because not required anywhere else.
            _tmp.Services.GetRequiredService<ModCacheManager>(); // Initialize because not required anywhere else.
            using (var t = _tmp.Services.GetRequiredService<StartTracker>().Measure(StartTimeType.PathResolver))
            {
                _tmp.Services.GetRequiredService<PathResolver>();
            }

            SetupInterface();
            SetupApi();

            _validityChecker.LogExceptions();
            Log.Information(
                $"Penumbra Version {_validityChecker.Version}, Commit #{_validityChecker.CommitHash} successfully Loaded from {pluginInterface.SourceRepository}.");
            OtterTex.NativeDll.Initialize(pluginInterface.AssemblyLocation.DirectoryName);
            Log.Information($"Loading native OtterTex assembly from {OtterTex.NativeDll.Directory}.");

            if (CharacterUtility.Ready)
                _residentResources.Reload();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void SetupApi()
    {
        using var timer = _tmp.Services.GetRequiredService<StartTracker>().Measure(StartTimeType.Api);
        var       api   = _tmp.Services.GetRequiredService<IPenumbraApi>();
        HttpApi = _tmp.Services.GetRequiredService<HttpApi>();
        _tmp.Services.GetRequiredService<PenumbraIpcProviders>();
        api.ChangedItemTooltip += it =>
        {
            if (it is Item)
                ImGui.TextUnformatted("Left Click to create an item link in chat.");
        };
        api.ChangedItemClicked += (button, it) =>
        {
            if (button == MouseButton.Left && it is Item item)
                ChatService.LinkItem(item);
        };
    }

    private void SetupInterface()
    {
        Task.Run(() =>
            {
                using var tInterface = _tmp.Services.GetRequiredService<StartTracker>().Measure(StartTimeType.Interface);
                var       system     = _tmp.Services.GetRequiredService<PenumbraWindowSystem>();
                _tmp.Services.GetRequiredService<CommandHandler>();
                if (!_disposed)
                {
                    _windowSystem = system;
                    ConfigWindow  = system.Window;
                }
                else
                {
                    system.Dispose();
                }
            }
        );
    }

    public event Action<bool>? EnabledChange;

    public bool SetEnabled(bool enabled)
    {
        if (enabled == _config.EnableMods)
            return false;

        _config.EnableMods = enabled;
        if (enabled)
        {
            if (CharacterUtility.Ready)
            {
                CollectionManager.Active.Default.SetFiles();
                _residentResources.Reload();
                RedrawService.RedrawAll(RedrawType.Redraw);
            }
        }
        else
        {
            if (CharacterUtility.Ready)
            {
                CharacterUtility.ResetAll();
                _residentResources.Reload();
                RedrawService.RedrawAll(RedrawType.Redraw);
            }
        }

        _config.Save();
        EnabledChange?.Invoke(enabled);

        return true;
    }

    public void ForceChangelogOpen()
        => _windowSystem?.ForceChangelogOpen();

    public void Dispose()
    {
        if (_disposed)
            return;

        _tmp?.Dispose();
        _disposed = true;
    }

    public string GatherSupportInformation()
    {
        var sb     = new StringBuilder(10240);
        var exists = _config.ModDirectory.Length > 0 && Directory.Exists(_config.ModDirectory);
        var drive  = exists ? new DriveInfo(new DirectoryInfo(_config.ModDirectory).Root.FullName) : null;
        sb.AppendLine("**Settings**");
        sb.Append($"> **`Plugin Version:              `** {_validityChecker.Version}\n");
        sb.Append($"> **`Commit Hash:                 `** {_validityChecker.CommitHash}\n");
        sb.Append($"> **`Enable Mods:                 `** {_config.EnableMods}\n");
        sb.Append($"> **`Enable HTTP API:             `** {_config.EnableHttpApi}\n");
        sb.Append($"> **`Operating System:            `** {(DalamudUtil.IsLinux() ? "Mac/Linux (Wine)" : "Windows")}\n");
        sb.Append($"> **`Root Directory:              `** `{_config.ModDirectory}`, {(exists ? "Exists" : "Not Existing")}\n");
        sb.Append(
            $"> **`Free Drive Space:            `** {(drive != null ? Functions.HumanReadableSize(drive.AvailableFreeSpace) : "Unknown")}\n");
        sb.Append($"> **`Auto-Deduplication:          `** {_config.AutoDeduplicateOnImport}\n");
        sb.Append($"> **`Debug Mode:                  `** {_config.DebugMode}\n");
        sb.Append(
            $"> **`Synchronous Load (Dalamud):  `** {(_tmp.Services.GetRequiredService<DalamudServices>().GetDalamudConfig(DalamudServices.WaitingForPluginsOption, out bool v) ? v.ToString() : "Unknown")}\n");
        sb.Append(
            $"> **`Logging:                     `** Log: {_config.EnableResourceLogging}, Watcher: {_config.EnableResourceWatcher} ({_config.MaxResourceWatcherRecords})\n");
        sb.Append($"> **`Use Ownership:               `** {_config.UseOwnerNameForCharacterCollection}\n");
        sb.AppendLine("**Mods**");
        sb.Append($"> **`Installed Mods:              `** {_modManager.Count}\n");
        sb.Append($"> **`Mods with Config:            `** {_modManager.Count(m => m.HasOptions)}\n");
        sb.Append(
            $"> **`Mods with File Redirections: `** {_modManager.Count(m => m.TotalFileCount > 0)}, Total: {_modManager.Sum(m => m.TotalFileCount)}\n");
        sb.Append(
            $"> **`Mods with FileSwaps:         `** {_modManager.Count(m => m.TotalSwapCount > 0)}, Total: {_modManager.Sum(m => m.TotalSwapCount)}\n");
        sb.Append(
            $"> **`Mods with Meta Manipulations:`** {_modManager.Count(m => m.TotalManipulations > 0)}, Total {_modManager.Sum(m => m.TotalManipulations)}\n");
        sb.Append($"> **`IMC Exceptions Thrown:       `** {_validityChecker.ImcExceptions.Count}\n");
        sb.Append(
            $"> **`#Temp Mods:                  `** {_tempMods.Mods.Sum(kvp => kvp.Value.Count) + _tempMods.ModsForAllCollections.Count}\n");

        void PrintCollection(ModCollection c, CollectionCache _)
            => sb.Append($"**Collection {c.AnonymizedName}**\n"
              + $"> **`Inheritances:                 `** {c.DirectlyInheritsFrom.Count}\n"
              + $"> **`Enabled Mods:                 `** {c.ActualSettings.Count(s => s is { Enabled: true })}\n"
              + $"> **`Conflicts (Solved/Total):     `** {c.AllConflicts.SelectMany(x => x).Sum(x => x.HasPriority && x.Solved ? x.Conflicts.Count : 0)}/{c.AllConflicts.SelectMany(x => x).Sum(x => x.HasPriority ? x.Conflicts.Count : 0)}\n");

        sb.AppendLine("**Collections**");
        sb.Append($"> **`#Collections:                 `** {CollectionManager.Storage.Count - 1}\n");
        sb.Append($"> **`#Temp Collections:            `** {_tempCollections.Count}\n");
        sb.Append($"> **`Active Collections:           `** {CollectionManager.Caches.Count - _tempCollections.Count}\n");
        sb.Append($"> **`Base Collection:              `** {CollectionManager.Active.Default.AnonymizedName}\n");
        sb.Append($"> **`Interface Collection:         `** {CollectionManager.Active.Interface.AnonymizedName}\n");
        sb.Append($"> **`Selected Collection:          `** {CollectionManager.Active.Current.AnonymizedName}\n");
        foreach (var (type, name, _) in CollectionTypeExtensions.Special)
        {
            var collection = CollectionManager.Active.ByType(type);
            if (collection != null)
                sb.Append($"> **`{name,-30}`** {collection.AnonymizedName}\n");
        }

        foreach (var (name, id, collection) in CollectionManager.Active.Individuals.Assignments)
            sb.Append($"> **`{id[0].Incognito(name) + ':',-30}`** {collection.AnonymizedName}\n");

        foreach (var (collection, cache) in CollectionManager.Caches.Active)
            PrintCollection(collection, cache);

        return sb.ToString();
    }
}
