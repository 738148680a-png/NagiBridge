using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using Microsoft.Xna.Framework.Input;
using StardewValley.Menus;
using System.Runtime.InteropServices;
using xTile.Dimensions;
using StardewValley.Monsters;
using StardewValley.Minigames;

namespace NagiBridge;

public class ModEntry : Mod
{
    [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly Queue<Action> _mainThreadQueue = new();
    private readonly object _queueLock = new();
    private int _port;

    // Pathfinding state
    private Queue<Point>? _pathQueue;
    private int _pathTickCooldown;

    // Command queue state
    private Queue<Dictionary<string, object?>>? _commandQueue;
    private readonly List<object> _commandResults = new();
    private TaskCompletionSource<object>? _commandQueueTcs;
    private int _commandDelay;
    private bool _waitingForMove;
    private bool _waitingForBite;
    private int _biteTimeout;

    // Time freeze state
    private bool _timeFrozen;
    private int _frozenTime;

    // Alert queue for game/system feedback consumed by external agents.
    private readonly Queue<Dictionary<string, object?>> _alertQueue = new();
    private readonly Dictionary<string, DateTime> _lastAlertTimes = new();
    private readonly object _alertLock = new();
    private string? _lastMenuType;
    private string? _lastMenuText;
    private string? _lastEventId;
    private string? _lastEventText;
    private bool _lastStaminaLow;
    private bool _lastWaterEmpty;
    private bool _lastInventoryFull;

    // Multiplayer inventory sync message type
    private const string MSG_ADD_ITEM = "NagiBridge.AddItem";

    private readonly PrairieKingBot _prairieKingBot = new();

    private readonly FlowerDanceBot _flowerDanceBot = new();
    private readonly LuauBot _luauBot = new();
    private readonly WinterStarBot _winterStarBot = new();
    private readonly MermaidBot _mermaidBot = new();
    private readonly SpiritsEveBot _spiritsEveBot = new();
    private readonly SpinningWheelBot _spinningWheelBot = new();
    private readonly EggHuntBot _eggHuntBot = new();

    private ChatHud? _chatHud;
    private ModConfig? _modConfig;
    private LlmClient? _llmClient;

    public override void Entry(IModHelper helper)
    {
        _modConfig = helper.ReadConfig<ModConfig>();
        _llmClient = new LlmClient(_modConfig, helper.DirectoryPath);

        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        helper.Events.Display.RenderedHud += OnRenderedHud;
        helper.Events.Display.Rendered += OnRendered;
        helper.Events.Input.ButtonPressed += OnButtonPressed;
        helper.Events.Multiplayer.ModMessageReceived += OnModMessageReceived;

        _chatHud = new ChatHud(Monitor, OnChatSend, OnApiConfigured, OnChannelSelected);
        _chatHud.SetInitialState(_modConfig.Mode, _modConfig.ApiKey, _modConfig.ApiUrl);
    }

    private void OnApiConfigured(string apiKey, string apiUrl)
    {
        _modConfig!.ApiKey = apiKey;
        _modConfig.ApiUrl = apiUrl;
        _modConfig.Mode = "api";
        if (apiUrl.Contains("deepseek")) _modConfig.ApiProvider = "deepseek";
        else if (apiUrl.Contains("anthropic")) _modConfig.ApiProvider = "claude";
        else if (apiUrl.Contains("openai.com")) _modConfig.ApiProvider = "openai";
        else _modConfig.ApiProvider = "custom";
        _llmClient = new LlmClient(_modConfig, Helper.DirectoryPath);
        Helper.WriteConfig(_modConfig);
        Monitor.Log($"API configured, provider={_modConfig.ApiProvider}, url={apiUrl}", LogLevel.Info);
    }

    private void OnChannelSelected()
    {
        _modConfig!.Mode = "cc";
        Helper.WriteConfig(_modConfig);
        Monitor.Log($"Channel mode selected", LogLevel.Info);
    }

    private void OnChatSend(string text)
    {
        Task.Run(async () =>
        {
            try
            {
                if (_modConfig!.Mode.Equals("cc", StringComparison.OrdinalIgnoreCase))
                {
                    using var client = new HttpClient();
                    var json = JsonSerializer.Serialize(new { message = text });
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    await client.PostAsync(_modConfig.ChannelServerUrl, content);
                }
                else
                {
                    var reply = await _llmClient!.SendAsync(text);
                    _chatHud?.AddMessage(_chatHud.AiDisplayName, reply);
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"Chat send error: {ex.Message}", LogLevel.Debug);
            }
        });
    }

    private void OnRenderedHud(object? sender, RenderedHudEventArgs e)
    {
        _chatHud?.DrawHud(e.SpriteBatch);
    }

    private void OnRendered(object? sender, RenderedEventArgs e)
    {
        _chatHud?.DrawPanel(e.SpriteBatch);
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (e.Button == StardewModdingAPI.SButton.OemTilde)
            Helper.Input.Suppress(e.Button);
        if (_chatHud?.IsOpen == true)
            Helper.Input.Suppress(e.Button);
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        StartServer();
    }

    /// <summary>
    /// Host-side handler: receives AddItem messages from farmhands and adds items
    /// to the requesting farmhand's inventory. This ensures inventory changes are
    /// authoritative (host-side) and properly synced in multiplayer.
    /// </summary>
    private void OnModMessageReceived(object? sender, ModMessageReceivedEventArgs e)
    {
        if (e.Type != MSG_ADD_ITEM || !Context.IsMainPlayer) return;

        try
        {
            var data = e.ReadAs<Dictionary<string, JsonElement>>();
            var farmerId = data["farmerId"].GetInt64();
            var itemId = data["itemId"].GetString()!;
            var count = data.ContainsKey("count") ? data["count"].GetInt32() : 1;
            var quality = data.ContainsKey("quality") ? data["quality"].GetInt32() : 0;

            EnqueueMainThread(() =>
            {
                var targetFarmer = Game1.getOnlineFarmers()
                    .FirstOrDefault(f => f.UniqueMultiplayerID == farmerId);
                if (targetFarmer == null)
                {
                    Monitor.Log($"AddItem: farmhand {farmerId} not found online", LogLevel.Warn);
                    return;
                }
                var item = ItemRegistry.Create(itemId, count);
                if (item is StardewValley.Object obj && quality > 0)
                    obj.Quality = quality;
                targetFarmer.addItemToInventory(item);
                Monitor.Log($"AddItem: gave {item.Name} x{count} (q{quality}) to {targetFarmer.Name}", LogLevel.Info);
            });
        }
        catch (Exception ex)
        {
            Monitor.Log($"AddItem message error: {ex.Message}", LogLevel.Error);
        }
    }

    /// <summary>
    /// Multiplayer-safe addItem: host adds directly, farmhand sends a message to host.
    /// </summary>
    private void AddItemSynced(string itemId, int count = 1, int quality = 0)
    {
        if (Context.IsMainPlayer)
        {
            // Host: add directly
            var item = ItemRegistry.Create(itemId, count);
            if (item is StardewValley.Object obj && quality > 0)
                obj.Quality = quality;
            Game1.player.addItemToInventory(item);
        }
        else
        {
            // Farmhand: ask host to add it for us
            Helper.Multiplayer.SendMessage(
                new Dictionary<string, object>
                {
                    ["farmerId"] = Game1.player.UniqueMultiplayerID,
                    ["itemId"] = itemId,
                    ["count"] = count,
                    ["quality"] = quality
                },
                MSG_ADD_ITEM,
                modIDs: new[] { ModManifest.UniqueID }
            );
        }
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        ClearMovementState();
    }

    private void ClearMovementState()
    {
        _pathQueue = null;
        _pathTickCooldown = 0;
        _waitingForMove = false;
    }

    private void CenterViewportOnFarmer(Farmer farmer)
    {
        var loc = farmer.currentLocation;
        int viewW = Game1.viewport.Width;
        int viewH = Game1.viewport.Height;
        int maxX = Math.Max(0, loc.Map.DisplayWidth - viewW);
        int maxY = Math.Max(0, loc.Map.DisplayHeight - viewH);
        int vx = (int)farmer.Position.X - viewW / 2;
        int vy = (int)farmer.Position.Y - viewH / 2;

        Game1.viewport.X = Math.Max(0, Math.Min(maxX, vx));
        Game1.viewport.Y = Math.Max(0, Math.Min(maxY, vy));
    }

    private void EnqueueAlert(string type, string message, string severity = "info", string source = "bridge")
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        var now = DateTime.UtcNow;
        var key = $"{type}:{message}";

        lock (_alertLock)
        {
            if (_lastAlertTimes.TryGetValue(key, out var last) && (now - last).TotalSeconds < 4)
                return;
            _lastAlertTimes[key] = now;

            _alertQueue.Enqueue(new Dictionary<string, object?>
            {
                ["timeUtc"] = now.ToString("O"),
                ["type"] = type,
                ["severity"] = severity,
                ["source"] = source,
                ["message"] = message
            });

            while (_alertQueue.Count > 100)
                _alertQueue.Dequeue();

            foreach (var stale in _lastAlertTimes.Where(p => (now - p.Value).TotalMinutes > 5).Select(p => p.Key).ToList())
                _lastAlertTimes.Remove(stale);
        }
    }

    private void CaptureAlerts()
    {
        var farmer = Game1.player;
        if (farmer == null)
            return;

        if (Game1.hudMessages != null)
        {
            foreach (var hud in Game1.hudMessages)
            {
                var text = hud.message;
                if (!string.IsNullOrWhiteSpace(text))
                    EnqueueAlert("hud", text, "info", "hud");
            }
        }

        bool staminaLow = farmer.MaxStamina > 0 && farmer.Stamina / farmer.MaxStamina < 0.15f;
        if (staminaLow && !_lastStaminaLow)
            EnqueueAlert("stamina_low", $"Stamina low: {farmer.Stamina:0}/{farmer.MaxStamina:0}", "warning", "state");
        else if (!staminaLow && _lastStaminaLow)
            EnqueueAlert("stamina_ok", $"Stamina recovered: {farmer.Stamina:0}/{farmer.MaxStamina:0}", "info", "state");
        _lastStaminaLow = staminaLow;

        var wateringCan = farmer.Items.OfType<WateringCan>().FirstOrDefault();
        bool waterEmpty = wateringCan != null && wateringCan.WaterLeft <= 0;
        if (waterEmpty && !_lastWaterEmpty)
            EnqueueAlert("water_empty", "Watering can is empty", "warning", "state");
        else if (!waterEmpty && _lastWaterEmpty)
            EnqueueAlert("water_refilled", "Watering can has water", "info", "state");
        _lastWaterEmpty = waterEmpty;

        int usedSlots = farmer.Items.Count(item => item != null);
        bool inventoryFull = usedSlots >= farmer.MaxItems;
        if (inventoryFull && !_lastInventoryFull)
            EnqueueAlert("inventory_full", $"Inventory full: {usedSlots}/{farmer.MaxItems}", "warning", "state");
        else if (!inventoryFull && _lastInventoryFull)
            EnqueueAlert("inventory_space", $"Inventory has space: {usedSlots}/{farmer.MaxItems}", "info", "state");
        _lastInventoryFull = inventoryFull;

        CaptureMenuAlerts();
        CaptureEventAlerts();
    }

    private void CaptureMenuAlerts()
    {
        var menu = Game1.activeClickableMenu;
        string? menuType = menu?.GetType().Name;
        string? menuText = null;

        if (menu is DialogueBox dialogue)
        {
            try { menuText = dialogue.getCurrentString(); } catch { }
        }
        else if (menu != null)
        {
            menuText = menuType;
        }

        if (menuType != _lastMenuType)
        {
            if (menuType == null)
                EnqueueAlert("menu_closed", "Menu closed", "info", "menu");
            else
                EnqueueAlert("menu_opened", $"Menu opened: {menuType}", "info", "menu");
            _lastMenuType = menuType;
            _lastMenuText = null;
        }

        if (!string.IsNullOrWhiteSpace(menuText) && menuText != _lastMenuText)
        {
            EnqueueAlert("menu_text", menuText, "info", "menu");
            _lastMenuText = menuText;
        }
    }

    private void CaptureEventAlerts()
    {
        var ev = Game1.currentLocation?.currentEvent;
        string? eventId = ev?.id;
        string? eventText = null;

        if (ev != null && Game1.activeClickableMenu is DialogueBox dialogue)
        {
            try { eventText = dialogue.getCurrentString(); } catch { }
        }

        if (eventId != _lastEventId)
        {
            if (eventId == null)
                EnqueueAlert("event_ended", "Event ended", "info", "event");
            else
                EnqueueAlert("event_started", $"Event started: {eventId}", "info", "event");
            _lastEventId = eventId;
            _lastEventText = null;
        }

        if (!string.IsNullOrWhiteSpace(eventText) && eventText != _lastEventText)
        {
            EnqueueAlert("event_text", eventText, "info", "event");
            _lastEventText = eventText;
        }
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        _chatHud?.Update();

        // Drain main-thread action queue
        lock (_queueLock)
        {
            while (_mainThreadQueue.Count > 0)
            {
                try { _mainThreadQueue.Dequeue().Invoke(); }
                catch (Exception ex) { Monitor.Log($"Queued action error: {ex}", LogLevel.Error); }
            }
        }

        // Freeze time if paused
        if (_timeFrozen && Context.IsWorldReady)
            Game1.timeOfDay = _frozenTime;

        _prairieKingBot.Update(Monitor);

        if (Context.IsWorldReady && Game1.player != null)
        {
            GameTime time = Game1.currentGameTime;
            _mermaidBot.Update(time);

            if (Game1.currentSeason.Equals("spring", StringComparison.OrdinalIgnoreCase))
            {
                if (Game1.dayOfMonth == 13)
                    _eggHuntBot.Update(time);
                else if (Game1.dayOfMonth == 24)
                    _flowerDanceBot.Update(time);
            }
            else if (Game1.currentSeason.Equals("summer", StringComparison.OrdinalIgnoreCase) && Game1.dayOfMonth == 11)
            {
                _luauBot.Update(time);
            }
            else if (Game1.currentSeason.Equals("fall", StringComparison.OrdinalIgnoreCase))
            {
                if (Game1.dayOfMonth == 16)
                    _spinningWheelBot.Update(time);
                else if (Game1.dayOfMonth == 27)
                    _spiritsEveBot.Update(time);
            }
            else if (Game1.currentSeason.Equals("winter", StringComparison.OrdinalIgnoreCase) && Game1.dayOfMonth == 25)
            {
                _winterStarBot.Update(time);
            }

            CaptureAlerts();
        }

        // Process pathfinding movement
        if (_pathQueue != null && _pathQueue.Count > 0 && Context.IsWorldReady)
        {
            if (_pathTickCooldown > 0)
            {
                _pathTickCooldown--;
                return;
            }

            var next = _pathQueue.Peek();
            var farmer = Game1.player;
            var target = new Vector2(next.X * 64 + 32, next.Y * 64 + 32);
            var diff = target - farmer.Position;

            if (diff.Length() < 6f)
            {
                _pathQueue.Dequeue();
                _pathTickCooldown = 0;
            }
            else
            {
                // Set facing direction
                if (Math.Abs(diff.X) > Math.Abs(diff.Y))
                    farmer.FacingDirection = diff.X > 0 ? 1 : 3;
                else
                    farmer.FacingDirection = diff.Y > 0 ? 2 : 0;

                var speed = farmer.getMovementSpeed();
                if (diff.Length() < speed)
                    farmer.Position = target;
                else
                {
                    diff.Normalize();
                    farmer.Position += diff * speed;
                }
            }
        }

        // Process command queue
        if (_commandQueue != null && _commandQueue.Count > 0 && Context.IsWorldReady)
        {
            // Wait for delay between commands
            if (_commandDelay > 0)
            {
                _commandDelay--;
                return;
            }

            // Wait for move to complete before next command
            if (_waitingForMove)
            {
                if (_pathQueue != null && _pathQueue.Count > 0)
                    return; // still walking
                _waitingForMove = false;
                _commandDelay = 5; // small gap after arriving
                return;
            }

            // Wait for fish bite
            if (_waitingForBite)
            {
                _biteTimeout--;
                if (_biteTimeout <= 0)
                {
                    _waitingForBite = false;
                    _commandResults.Add(new { ok = false, action = "wait_for_bite", error = "Timed out waiting for bite" });
                    // Don't abort queue - let next commands handle it
                }
                else if (Game1.player.CurrentTool is FishingRod fishRod && fishRod.isNibbling)
                {
                    _waitingForBite = false;
                    _commandResults.Add(new { ok = true, action = "wait_for_bite", message = "Fish is biting!" });
                    _commandDelay = 2; // tiny delay before reeling
                }
                else
                    return; // keep waiting
                return;
            }

            var cmd = _commandQueue.Dequeue();
            var action = cmd.ContainsKey("action") && cmd["action"] is JsonElement ae
                ? ae.GetString() ?? "" : "";

            try
            {
                switch (action)
                {
                    case "move":
                    {
                        var x = cmd.ContainsKey("x") && cmd["x"] is JsonElement xe ? xe.GetInt32() : 0;
                        var y = cmd.ContainsKey("y") && cmd["y"] is JsonElement ye ? ye.GetInt32() : 0;
                        var farmer = Game1.player;
                        var path = FindPath(farmer.currentLocation, farmer.TilePoint, new Point(x, y));
                        _pathQueue = path ?? new Queue<Point>(new[] { new Point(x, y) });
                        _pathTickCooldown = 0;
                        _waitingForMove = true;
                        _commandResults.Add(new { ok = true, action = "move", x, y });
                        break;
                    }
                    case "face":
                    {
                        var dir = cmd.ContainsKey("direction") && cmd["direction"] is JsonElement de ? de.GetInt32() : 2;
                        Game1.player.FacingDirection = dir;
                        _commandResults.Add(new { ok = true, action = "face", direction = dir });
                        _commandDelay = 3;
                        break;
                    }
                    case "select":
                    {
                        var name = cmd.ContainsKey("name") && cmd["name"] is JsonElement ne ? ne.GetString() ?? "" : "";
                        var farmer = Game1.player;
                        var idx = -1;
                        for (int i = 0; i < farmer.Items.Count; i++)
                        {
                            if (farmer.Items[i] != null && farmer.Items[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                            { idx = i; break; }
                        }
                        if (idx >= 0)
                        {
                            farmer.CurrentToolIndex = idx;
                            _commandResults.Add(new { ok = true, action = "select", name, slot = idx });
                        }
                        else
                            _commandResults.Add(new { ok = false, action = "select", error = $"Item '{name}' not found" });
                        _commandDelay = 3;
                        break;
                    }
                    case "use":
                    {
                        var farmer = Game1.player;
                        var item = farmer.CurrentItem;
                        if (item is Tool)
                        {
                            farmer.BeginUsingTool();
                            _commandResults.Add(new { ok = true, action = "use", item = item.Name });
                        }
                        else if (item is StardewValley.Object obj)
                        {
                            var facingTile = GetFacingTile(farmer);
                            int px = (int)facingTile.X * 64;
                            int py = (int)facingTile.Y * 64;
                            bool placed = obj.placementAction(farmer.currentLocation, px, py, farmer);
                            if (placed)
                            {
                                farmer.reduceActiveItemByOne();
                                _commandResults.Add(new { ok = true, action = "placed", item = item.Name });
                            }
                            else
                                _commandResults.Add(new { ok = false, action = "use", error = $"Cannot use '{item.Name}' here" });
                        }
                        else
                            _commandResults.Add(new { ok = false, action = "use", error = "No usable item" });
                        _commandDelay = 15; // tool animation time
                        break;
                    }
                    case "interact":
                    {
                        var farmer = Game1.player;
                        var facingTile = GetFacingTile(farmer);
                        var acted = farmer.currentLocation.checkAction(
                            new Location((int)facingTile.X, (int)facingTile.Y), Game1.viewport, farmer);
                        _commandResults.Add(new { ok = true, action = "interact", triggered = acted });
                        _commandDelay = 10;
                        break;
                    }
                    case "wait":
                    {
                        var ticks = cmd.ContainsKey("ticks") && cmd["ticks"] is JsonElement te ? te.GetInt32() : 60;
                        _commandResults.Add(new { ok = true, action = "wait", ticks });
                        _commandDelay = ticks;
                        break;
                    }
                    case "warp":
                    {
                        var loc = cmd.ContainsKey("location") && cmd["location"] is JsonElement le ? le.GetString() ?? "" : "";
                        var wx = cmd.ContainsKey("x") && cmd["x"] is JsonElement wxe ? wxe.GetInt32() : 10;
                        var wy = cmd.ContainsKey("y") && cmd["y"] is JsonElement wye ? wye.GetInt32() : 10;
                        Game1.warpFarmer(loc, wx, wy, false);
                        _commandResults.Add(new { ok = true, action = "warp", location = loc, x = wx, y = wy });
                        _commandDelay = 30; // wait for warp to complete
                        break;
                    }
                    case "wait_for_bite":
                    {
                        var timeout = cmd.ContainsKey("timeout") && cmd["timeout"] is JsonElement to ? to.GetInt32() : 1800;
                        _waitingForBite = true;
                        _biteTimeout = timeout;
                        break;
                    }
                    case "key":
                    {
                        var keyName = cmd.ContainsKey("key") && cmd["key"] is JsonElement ke ? ke.GetString() ?? "confirm" : "confirm";
                        switch (keyName.ToLower())
                        {
                            case "confirm": case "action":
                                Game1.pressActionButton(Game1.input.GetKeyboardState(), Game1.input.GetMouseState(), Game1.input.GetGamePadState());
                                break;
                            case "skip": case "escape":
                                if (Game1.activeClickableMenu != null)
                                    Game1.activeClickableMenu.receiveKeyPress(Keys.Escape);
                                else
                                    Game1.activeClickableMenu?.exitThisMenu();
                                break;
                        }
                        _commandResults.Add(new { ok = true, action = "key", key = keyName });
                        _commandDelay = 10;
                        break;
                    }
                    default:
                        _commandResults.Add(new { ok = false, action, error = "Unknown action" });
                        break;
                }
            }
            catch (Exception ex)
            {
                _commandResults.Add(new { ok = false, action, error = ex.Message });
            }

            // All commands done? Return results
            if (_commandQueue.Count == 0)
            {
                _commandQueueTcs?.TrySetResult(new
                {
                    ok = true,
                    executed = _commandResults.Count,
                    results = _commandResults.ToArray()
                });
                _commandQueue = null;
            }
        }
    }

    private static bool IsPortAvailable(int port)
    {
        try
        {
            var tcp = new TcpListener(IPAddress.Loopback, port);
            tcp.Start();
            tcp.Stop();
            return true;
        }
        catch { return false; }
    }

    private void StartServer()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        Task.Run(async () =>
        {
            // Auto-detect available port starting from 7842
            _listener = null;
            for (_port = 7842; _port < 7850; _port++)
            {
                try
                {
                    var listener = new HttpListener();
                    listener.Prefixes.Add($"http://localhost:{_port}/");
                    listener.Start();
                    _listener = listener;
                    Monitor.Log($"NagiBridge HTTP server started on port {_port}", LogLevel.Info);
                    break;
                }
                catch
                {
                    Monitor.Log($"Port {_port} unavailable, trying next...", LogLevel.Debug);
                }
            }

            if (_listener == null)
            {
                Monitor.Log("Failed to start HTTP server on any port (7842-7849)", LogLevel.Error);
                return;
            }

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HandleRequest(ctx), token);
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    Monitor.Log($"Listener error: {ex.Message}", LogLevel.Warn);
                }
            }
        }, token);
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        var method = ctx.Request.HttpMethod;

        try
        {
            object? result = path switch
            {
                "/status" => HandleStatus(),
                "/move" => HandleMove(ctx),
                "/tool" => HandleTool(ctx),
                "/interact" => HandleInteract(ctx),
                "/chat" => HandleChat(ctx),
                "/emote" => HandleEmote(ctx),
                "/state" => HandleState(),
                "/surroundings" => HandleSurroundings(ctx),
                "/alerts" => HandleAlerts(ctx),
                "/stop" => HandleStop(),
                "/map" => HandleMap(),
                "/buy" => HandleBuy(ctx),
                "/face" => HandleFace(ctx),
                "/select" => HandleSelect(ctx),
                "/use" => HandleUse(ctx),
                "/sleep" => HandleSleep(),
                "/wakeup" => HandleWakeup(),
                "/queue" => HandleQueue(ctx),
                "/key" => HandleKey(ctx),
                "/warp" => HandleWarp(ctx),
                "/position" => HandlePosition(ctx),
                "/pause" => HandlePause(),
                "/resume" => HandleResume(),
                "/give" => HandleGive(ctx),
                "/toss" => HandleToss(ctx),
                "/money" => HandleMoney(ctx),
                "/refill" => HandleRefill(),
                "/heal" => HandleHeal(),
                "/ripen" => HandleRipen(ctx),
                "/sell" => HandleSell(ctx),
                "/harvest" => HandleHarvest(ctx),
                "/store" => HandleStore(ctx),
                "/chest" => HandleChest(ctx),
                "/placechest" => HandlePlaceChest(ctx),
                "/fishbot" => HandleFishbot(ctx),
                "/minigame/state" => HandleMinigameState(),
                "/minigame/bot" => HandleMinigameBot(ctx),
                "/menu" => HandleMenu(),
                "/menu/click" => HandleMenuClick(ctx),
                "/craft" => HandleCraft(ctx),
                "/machines" => HandleMachines(),
                "/animals" => HandleAnimals(),
                "/scan" => HandleScan(),
                "/festival" => HandleFestival(),
                "/festival/interact" => HandleFestivalInteract(ctx),
                "/festival/answer" => HandleFestivalAnswer(ctx),
                "/chat/push" => HandleChatPush(ctx),
                "/chat/history" => HandleChatHistory(),
                "/cast" => HandleCast(ctx),
                "/ai-fish" => HandleAiFish(ctx),
                _ => throw new InvalidOperationException($"Unknown endpoint: {path}")
            };

            Respond(ctx, 200, result ?? new { ok = true });
        }
        catch (Exception ex)
        {
            Respond(ctx, 400, new { error = ex.Message });
        }
    }

    private static void Respond(HttpListenerContext ctx, int status, object body)
    {
        var json = JsonSerializer.Serialize(body, new JsonSerializerOptions { WriteIndented = false });
        var buf = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = buf.Length;
        ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
        ctx.Response.OutputStream.Write(buf, 0, buf.Length);
        ctx.Response.Close();
    }

    private Dictionary<string, object?> ReadJson(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var body = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(body))
            return new Dictionary<string, object?>();
        return JsonSerializer.Deserialize<Dictionary<string, object?>>(body) ?? new();
    }

    private T GetParam<T>(Dictionary<string, object?> dict, string key)
    {
        if (!dict.TryGetValue(key, out var val) || val == null)
            throw new InvalidOperationException($"Missing parameter: {key}");

        if (val is JsonElement je)
        {
            if (typeof(T) == typeof(int)) return (T)(object)je.GetInt32();
            if (typeof(T) == typeof(float)) return (T)(object)je.GetSingle();
            if (typeof(T) == typeof(string)) return (T)(object)(je.GetString() ?? "");
            if (typeof(T) == typeof(bool)) return (T)(object)je.GetBoolean();
        }

        return (T)Convert.ChangeType(val, typeof(T));
    }

    private T GetParamOr<T>(Dictionary<string, object?> dict, string key, T defaultValue)
    {
        if (!dict.TryGetValue(key, out var val) || val == null)
            return defaultValue;

        if (val is JsonElement je)
        {
            if (typeof(T) == typeof(int)) return (T)(object)je.GetInt32();
            if (typeof(T) == typeof(float)) return (T)(object)je.GetSingle();
            if (typeof(T) == typeof(string)) return (T)(object)(je.GetString() ?? "");
            if (typeof(T) == typeof(bool)) return (T)(object)je.GetBoolean();
        }

        return (T)Convert.ChangeType(val, typeof(T));
    }

    // --- Handlers ---

    private object HandleStatus()
    {
        return new
        {
            ok = true,
            server = "NagiBridge",
            version = "1.0.0",
            port = _port,
            worldReady = Context.IsWorldReady,
            isMultiplayer = Context.IsMultiplayer
        };
    }

    /// <summary>
    /// POST /move  { "x": 10, "y": 15 }
    /// Walks to tile (x, y) using simple straight-line pathfinding.
    /// </summary>
    private object HandleMove(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var tx = GetParam<int>(p, "x");
        var ty = GetParam<int>(p, "y");

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        // Build simple path: current tile -> target tile (straight line, then adjust)
        var tcs = new TaskCompletionSource<object>();

        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;
            var startTile = farmer.TilePoint;
            var path = FindPath(farmer.currentLocation, startTile, new Point(tx, ty));

            if (path == null || path.Count == 0)
            {
                // Fallback: just teleport-walk directly
                _pathQueue = new Queue<Point>();
                _pathQueue.Enqueue(new Point(tx, ty));
            }
            else
            {
                _pathQueue = path;
            }
            _pathTickCooldown = 0;

            tcs.SetResult(new { ok = true, message = $"Moving to ({tx},{ty}), steps={_pathQueue.Count}" });
        });

        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// POST /tool  { "name": "Axe", "x": 12, "y": 34 }
    /// Swings the specified tool (or current tool) once.
    /// Optional x/y: target tile — must be adjacent (or own tile); farmer auto-faces it before swinging.
    /// </summary>
    private object HandleTool(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var name = GetParamOr(p, "name", "current");
        var tx = GetParamOr(p, "x", -1);
        var ty = GetParamOr(p, "y", -1);

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();

        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;

            if (tx >= 0 && ty >= 0)
            {
                var dx = tx - farmer.TilePoint.X;
                var dy = ty - farmer.TilePoint.Y;
                if (Math.Abs(dx) + Math.Abs(dy) > 1)
                {
                    tcs.SetResult(new { ok = false,
                        error = $"Target ({tx},{ty}) is not adjacent — standing at ({farmer.TilePoint.X},{farmer.TilePoint.Y}). Move to a tile next to it first." });
                    return;
                }
                if (dx != 0 || dy != 0)
                    farmer.faceDirection(dx > 0 ? 1 : dx < 0 ? 3 : dy > 0 ? 2 : 0);
            }

            if (name != "current")
            {
                var tool = farmer.Items
                    .Where(i => i is Tool)
                    .Cast<Tool>()
                    .FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

                if (tool == null)
                {
                    tcs.SetResult(new { ok = false, error = $"Tool '{name}' not found in inventory" });
                    return;
                }

                farmer.CurrentToolIndex = farmer.Items.IndexOf(tool);
            }

            farmer.BeginUsingTool();
            var toolTile = GetFacingTile(farmer);
            tcs.SetResult(new { ok = true, tool = farmer.CurrentTool?.Name ?? "none",
                facing = farmer.FacingDirection, hitTile = new { x = (int)toolTile.X, y = (int)toolTile.Y } });
        });

        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// POST /interact  { }
    /// Triggers an action check at the tile the farmer is facing.
    /// Returns what's on the tile for context.
    /// </summary>
    private object HandleInteract(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();

        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;
            var loc = farmer.currentLocation;
            var facingTile = GetFacingTile(farmer);
            int ftx = (int)facingTile.X, fty = (int)facingTile.Y;
            var tileVec = new Vector2(ftx, fty);

            bool acted = loc.checkAction(
                new Location(ftx, fty),
                Game1.viewport,
                farmer
            );

            var result = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["actionTriggered"] = acted,
                ["facingTile"] = new { x = ftx, y = fty }
            };

            if (loc.objects.TryGetValue(tileVec, out var obj))
                result["object"] = obj.Name;
            if (loc.terrainFeatures.TryGetValue(tileVec, out var tf))
            {
                result["terrain"] = tf.GetType().Name;
                if (tf is HoeDirt dirt && dirt.crop != null && dirt.readyForHarvest())
                    result["harvestable"] = true;
            }
            var npc = loc.characters.FirstOrDefault(n => n.TilePoint.X == ftx && n.TilePoint.Y == fty);
            if (npc != null)
                result["npc"] = npc.Name;

            tcs.SetResult(result);
        });

        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// POST /chat  { "message": "Hello!" }
    /// Sends a chat message visible to all players.
    /// </summary>
    private object HandleChat(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var message = GetParam<string>(p, "message");

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        EnqueueMainThread(() =>
        {
            Game1.chatBox?.addMessage(message, Color.White);
            if (Context.IsMultiplayer)
            {
                Game1.chatBox?.setText(message);
                Game1.chatBox?.chatBox.RecieveCommandInput('\r');
            }
        });

        return new { ok = true, message };
    }

    private object HandleChatPush(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var sender = p.TryGetValue("sender", out var s) ? s?.ToString() ?? "Nagi" : "Nagi";
        var message = GetParam<string>(p, "message");
        _chatHud?.AddMessage(sender, message);
        return new { ok = true, sender, message };
    }

    private object HandleChatHistory()
    {
        // Returns empty if chatHud not initialized - safe fallback
        return new { ok = true, messages = Array.Empty<object>() };
    }

    /// <summary>
    /// POST /cast  { "count": 3, "radius": 5 }
    /// Remote "staff" attack: scans for monsters within radius, deals weapon damage
    /// without needing to be adjacent. Simulates ranged combat for API-controlled players.
    /// Weapon stats are derived from currently equipped weapon (or best weapon in inventory).
    /// </summary>
    private object HandleCast(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var count = GetParamOr(p, "count", 1);
        var radius = GetParamOr(p, "radius", -1);

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        if (count < 1) count = 1;
        if (count > 10) count = 10;

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                var farmer = Game1.player;
                var loc = farmer.currentLocation;
                var cx = farmer.TilePoint.X;
                var cy = farmer.TilePoint.Y;

                // Find equipped weapon or best weapon in inventory
                StardewValley.Tools.MeleeWeapon? weapon = farmer.CurrentTool as StardewValley.Tools.MeleeWeapon;
                if (weapon == null)
                {
                    foreach (var item in farmer.Items)
                    {
                        if (item is StardewValley.Tools.MeleeWeapon w)
                        {
                            if (weapon == null || w.minDamage.Value > weapon.minDamage.Value)
                                weapon = w;
                        }
                    }
                }

                if (weapon == null)
                {
                    tcs.SetResult(new { ok = false, error = "No weapon found in inventory" });
                    return;
                }

                int baseDmgMin = weapon.minDamage.Value;
                int baseDmgMax = weapon.maxDamage.Value;
                string weaponName = weapon.DisplayName ?? weapon.Name;

                // Determine effective radius based on weapon level
                if (radius < 0)
                {
                    int weaponLevel = weapon.getItemLevel();
                    radius = Math.Min(3 + weaponLevel, 12);
                }

                var kills = new List<object>();
                var hits = new List<object>();
                int totalDamageDealt = 0;
                int damageTaken = 0;
                int staminaCost = 0;

                for (int i = 0; i < count; i++)
                {
                    // Find nearest monster within radius
                    Monster? target = null;
                    double minDist = double.MaxValue;
                    foreach (var npc in loc.characters)
                    {
                        if (npc is Monster m && m.Health > 0)
                        {
                            double dist = Math.Sqrt(Math.Pow(m.TilePoint.X - cx, 2) + Math.Pow(m.TilePoint.Y - cy, 2));
                            if (dist <= radius && dist < minDist)
                            {
                                minDist = dist;
                                target = m;
                            }
                        }
                    }

                    if (target == null) break;

                    // Calculate damage with some randomness
                    var rng = Game1.random;
                    int dmg = rng.Next(baseDmgMin, baseDmgMax + 1);
                    bool crit = rng.NextDouble() < 0.05 + weapon.critChance.Value;
                    if (crit) dmg = (int)(dmg * (2.0 + weapon.critMultiplier.Value));

                    // Apply damage
                    int prevHp = target.Health;
                    target.Health -= dmg;
                    totalDamageDealt += dmg;
                    staminaCost += 2;

                    // Monster counter-attack chance (distance-based)
                    double counterChance = Math.Max(0, 0.4 - minDist * 0.06);
                    if (rng.NextDouble() < counterChance)
                    {
                        int monsterDmg = Math.Max(1, target.DamageToFarmer - farmer.CombatLevel);
                        farmer.health = Math.Max(1, farmer.health - monsterDmg);
                        damageTaken += monsterDmg;
                    }

                    bool killed = target.Health <= 0;
                    var hitInfo = new
                    {
                        monster = target.Name,
                        distance = Math.Round(minDist, 1),
                        damage = dmg,
                        critical = crit,
                        monsterHpLeft = Math.Max(0, target.Health),
                        killed
                    };

                    if (killed)
                    {
                        kills.Add(hitInfo);
                        // Remove dead monster and grant XP
                        loc.characters.Remove(target);
                        farmer.gainExperience(4, target.ExperienceGained); // combat skill
                    }
                    else
                    {
                        hits.Add(hitInfo);
                    }
                }

                // Deduct stamina
                farmer.Stamina = Math.Max(0, farmer.Stamina - staminaCost);

                // Scan remaining monsters
                var remaining = loc.characters
                    .OfType<Monster>()
                    .Where(m => m.Health > 0 && Math.Sqrt(Math.Pow(m.TilePoint.X - cx, 2) + Math.Pow(m.TilePoint.Y - cy, 2)) <= radius)
                    .Select(m => new { name = m.Name, x = m.TilePoint.X, y = m.TilePoint.Y, health = m.Health, distance = Math.Round(Math.Sqrt(Math.Pow(m.TilePoint.X - cx, 2) + Math.Pow(m.TilePoint.Y - cy, 2)), 1) })
                    .ToList();

                tcs.SetResult(new
                {
                    ok = true,
                    weapon = weaponName,
                    effectiveRadius = radius,
                    casts = hits.Count + kills.Count,
                    hits,
                    kills,
                    totalDamageDealt,
                    damageTaken,
                    staminaCost,
                    playerHealth = farmer.health,
                    playerStamina = (int)farmer.Stamina,
                    monstersInRange = remaining
                });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    // ============================================================
    // AI Fishing Minigame System
    // ============================================================

    private static readonly string[][] AiFishPatterns = new[]
    {
        new[] { "rush","calm","rush","calm","rush","calm" },       // 0: alternating rush/calm
        new[] { "struggle","dive","struggle","dive","struggle" },  // 1: alternating struggle/dive
        new[] { "calm","calm","dive","rush","rush","rush" },       // 2: buildup
        new[] { "rush","rush","struggle","dive","calm","calm" },   // 3: fadeout
        new[] { "calm","calm","rush","calm","calm","rush" },       // 4: pulse
        new[] { "calm","struggle","rush","struggle","calm" },      // 5: mirror
        new[] { "rush","rush","rush","rush","rush","rush" },       // 6: stubborn rush
        new[] { "struggle","struggle","struggle","struggle" },     // 7: stubborn struggle
        new[] { "calm","dive","struggle","struggle","rush" },      // 8: accelerating
        new[] { "calm","struggle","rush","dive","calm" },          // 9: spiral
        new[] { "calm","calm","calm","rush","rush","rush" },       // 10: bait trap
        new[] { "calm","rush","dive","rush","calm","rush" },       // 11: zigzag
    };

    private static readonly string[] AiFishPatternNames = new[]
    {
        "交替型(冲↔静)", "交替型(挣↔潜)", "蓄力型", "衰减型",
        "脉冲型", "镜像型", "固执型(冲)", "固执型(挣)",
        "加速型", "回旋型", "诱饵型", "锯齿型"
    };

    private static readonly Dictionary<string, string> AiFishClues = new()
    {
        ["calm"] = "鱼在水面缓缓游动",
        ["struggle"] = "鱼线在微微颤抖",
        ["rush"] = "水面突然炸开一片水花",
        ["dive"] = "鱼竿被猛地向下拽",
    };

    private static readonly string[] AiFishTaunts = new[]
    {
        "聪明反被聪明误！鱼溜了~",
        "鱼王回头看了你一眼，带着不屑游走了",
        "你感觉到鱼线一松——它在笑你",
        "读心失败，反被鱼读了心",
        "这条鱼的智商可能比你高",
        "鱼：就这？",
        "线断了，鱼带着你的自信游走了",
        "张力爆表！鱼竿发出不满的嘎吱声",
        "你以为你读懂了它，其实它在遛你",
        "鱼使出了假动作，你上当了",
    };

    private class AiFishState
    {
        public int Difficulty;
        public int TotalPhases;
        public int CurrentPhase;
        public double Progress;
        public double Tension;
        public double TensionMax;
        public double ProgressMult;
        public double LossReduce;
        public double TensionResist;
        public double ProgressBonus;
        public double ClueBonus;
        public int PatternIdx;
        public string[] BehaviorSeq = Array.Empty<string>();
        public List<object> History = new();
        public Random Rng = new();
        public string RodName = "";
        public string TackleName = "";
        public string BaitName = "";
        public string? FishId;      // real fish rolled from location pool; null = practice mode
        public string? FishName;
        public int PerfectCount;
    }

    private AiFishState? _aiFishState;

    private object HandleAiFish(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var action = GetParamOr(p, "action", "cast");

        if (action == "cast")
            return AiFishCast(p);
        else if (action == "decide")
            return AiFishDecide(p);
        else if (action == "status")
            return _aiFishState != null
                ? new { ok = true, active = true, phase = _aiFishState.CurrentPhase, totalPhases = _aiFishState.TotalPhases }
                : new { ok = true, active = false, phase = 0, totalPhases = 0 };
        else
            throw new InvalidOperationException($"Unknown ai-fish action: {action}");
    }

    private object AiFishCast(Dictionary<string, object>? p)
    {
        if (_aiFishState != null)
            return new { ok = false, error = "Already fishing! Use action='decide' to continue or wait for result." };

        var tcs = new TaskCompletionSource<object>();
        int diffParam = p != null && p.ContainsKey("difficulty") ? GetParam<int>(p, "difficulty") : -1;

        EnqueueMainThread(() =>
        {
            try
            {
                var farmer = Game1.player;
                if (!Context.IsWorldReady)
                    throw new InvalidOperationException("World not ready");

                // Detect rod
                int rodLevel = 0; // 0=bamboo, 1=training, 2=fiberglass, 3=iridium
                string rodName = "Bamboo Pole";
                if (farmer.CurrentTool is FishingRod rod)
                {
                    rodName = rod.Name ?? "Bamboo Pole";
                    if (rodName.Contains("Training")) rodLevel = 1;
                    else if (rodName.Contains("Iridium")) rodLevel = 3;
                    else if (rodName.Contains("Fiberglass")) rodLevel = 2;
                    else if (rod.UpgradeLevel >= 3) rodLevel = 3;
                    else if (rod.UpgradeLevel >= 2) rodLevel = 2;
                    else if (rod.UpgradeLevel >= 1) rodLevel = 2;
                }

                // Detect tackle and bait
                string tackleName = "none";
                string baitName = "none";
                if (farmer.CurrentTool is FishingRod fishRod)
                {
                    var baitObj = fishRod.GetBait();
                    if (baitObj != null)
                    {
                        var bn = baitObj.Name?.ToLower() ?? "";
                        if (bn.Contains("challenge")) baitName = "challenge";
                        else if (bn.Contains("deluxe")) baitName = "deluxe";
                        else if (bn.Contains("wild")) baitName = "wild";
                        else if (bn.Contains("magnet")) baitName = "magnet";
                        else baitName = "basic";
                    }
                    var tackleList = fishRod.GetTackle();
                    if (tackleList != null)
                    {
                        foreach (var tack in tackleList)
                        {
                            if (tack == null) continue;
                            var tn = tack.Name?.ToLower() ?? "";
                            if (tn.Contains("cork")) { tackleName = "cork_bobber"; break; }
                            else if (tn.Contains("lead")) { tackleName = "lead_bobber"; break; }
                            else if (tn.Contains("trap")) { tackleName = "trap_bobber"; break; }
                            else if (tn.Contains("barbed")) { tackleName = "barbed_hook"; break; }
                            else if (tn.Contains("dressed")) { tackleName = "dressed_spinner"; break; }
                            else if (tn.Contains("curiosity")) { tackleName = "curiosity_lure"; break; }
                            else { tackleName = "other"; break; }
                        }
                    }
                }

                // Real-fish mode (default): roll a fish from this location's pool,
                // use its actual difficulty. Passing "difficulty" switches to practice
                // mode: old behavior, no real fish awarded.
                string? fishId = null;
                string? fishName = null;
                int realFishDiff = -1;
                if (diffParam < 0)
                {
                    if (farmer.CurrentTool is not FishingRod)
                    {
                        tcs.SetResult(new { ok = false, error = "Need a fishing rod equipped — use /select first" });
                        return;
                    }

                    var loc = farmer.currentLocation;
                    var ft = farmer.TilePoint;
                    Vector2? waterTile = null;
                    for (int r = 1; r <= 8 && waterTile == null; r++)
                    {
                        for (int dx = -r; dx <= r && waterTile == null; dx++)
                        for (int dy = -r; dy <= r && waterTile == null; dy++)
                        {
                            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                            if (loc.isWaterTile(ft.X + dx, ft.Y + dy))
                                waterTile = new Vector2(ft.X + dx, ft.Y + dy);
                        }
                    }
                    if (waterTile == null)
                    {
                        tcs.SetResult(new { ok = false, error = "No water within 8 tiles — walk to a shore, river or pond first" });
                        return;
                    }

                    var rolled = loc.getFish(0f, null, 4, farmer, 0.0, waterTile.Value);
                    if (rolled == null)
                    {
                        tcs.SetResult(new { ok = false, error = "Nothing is biting here, try another spot" });
                        return;
                    }

                    var fishData = Game1.content.Load<Dictionary<string, string>>("Data\\Fish");
                    var fields = fishData.TryGetValue(rolled.ItemId, out var rawFish) ? rawFish.Split('/') : null;
                    if (fields == null || fields.Length < 3 || !int.TryParse(fields[1], out realFishDiff))
                    {
                        // junk / algae / non-fish: no minigame, straight to inventory
                        AddItemSynced(rolled.QualifiedItemId, rolled.Stack);
                        tcs.SetResult(new { ok = true, status = "junk", caught = true, fish = rolled.DisplayName,
                            message = $"钓上来一个 {rolled.DisplayName}……不是鱼，直接进包了" });
                        return;
                    }
                    fishId = rolled.QualifiedItemId;
                    fishName = rolled.DisplayName;
                }

                // Determine difficulty
                int diff;
                if (diffParam >= 0)
                    diff = Math.Clamp(diffParam, 5, 120);
                else
                    diff = Math.Clamp(realFishDiff, 5, 120);

                // Rod stats: (tensionMax, progressMult)
                double tensionMax, progressMult;
                switch (rodLevel)
                {
                    case 0: tensionMax = 78; progressMult = 0.98; break;
                    case 1: tensionMax = 82; progressMult = 1.04; break;
                    case 2: tensionMax = 90; progressMult = 1.10; break;
                    default: tensionMax = 100; progressMult = 1.18; break;
                }

                // Tackle bonuses
                double progressBonus = 0, tensionResist = 0, lossReduce = 0;
                switch (tackleName)
                {
                    case "cork_bobber": progressBonus = 0.12; break;
                    case "lead_bobber": tensionResist = 0.2; break;
                    case "trap_bobber": lossReduce = 0.35; break;
                    case "barbed_hook": progressBonus = 0.18; tensionResist = -0.05; break;
                }

                // Bait bonuses
                double startBonus = 0, clueBonus = 0;
                switch (baitName)
                {
                    case "basic": startBonus = 5; break;
                    case "wild": startBonus = 3; clueBonus = 0.05; break;
                    case "deluxe": startBonus = 8; break;
                    case "challenge": startBonus = -10; clueBonus = -0.1; break;
                }

                // Phases based on difficulty
                int phases;
                if (diff <= 25) phases = 2;
                else if (diff <= 50) phases = 3;
                else if (diff <= 75) phases = 4;
                else phases = 5;

                // Generate fish pattern (weighted by difficulty)
                var rng2 = new Random();
                // Groups: A=stubborn(6,7) B=bait(10) C=pulse/spiral/zigzag(4,9,11) D=rest(0,1,2,3,5,8)
                double roll = rng2.NextDouble() * 100;
                int[] candidates;
                if (diff <= 25)  // Easy: A=5% B=5% C=25% D=65%
                {
                    if (roll < 5) candidates = new[] {6, 7};
                    else if (roll < 10) candidates = new[] {10};
                    else if (roll < 35) candidates = new[] {4, 9, 11};
                    else candidates = new[] {0, 1, 2, 3, 5, 8};
                }
                else if (diff <= 50)  // Medium: A=20% B=10% C=40% D=30%
                {
                    if (roll < 20) candidates = new[] {6, 7};
                    else if (roll < 30) candidates = new[] {10};
                    else if (roll < 70) candidates = new[] {4, 9, 11};
                    else candidates = new[] {0, 1, 2, 3, 5, 8};
                }
                else if (diff <= 75)  // Hard: A=35% B=20% C=20% D=25%
                {
                    if (roll < 35) candidates = new[] {6, 7};
                    else if (roll < 55) candidates = new[] {10};
                    else if (roll < 75) candidates = new[] {4, 9, 11};
                    else candidates = new[] {0, 1, 2, 3, 5, 8};
                }
                else  // Legend: A=60% B=25% C=15% D=0%
                {
                    if (roll < 60) candidates = new[] {6, 7};
                    else if (roll < 85) candidates = new[] {10};
                    else candidates = new[] {4, 9, 11};
                }
                int patIdx = candidates[rng2.Next(candidates.Length)];
                double noise = Math.Min(0.4, diff / 250.0);
                var seq = new string[phases];
                var pat = AiFishPatterns[patIdx];
                for (int i = 0; i < phases; i++)
                {
                    seq[i] = pat[i % pat.Length];
                    if (rng2.NextDouble() < noise)
                    {
                        var behaviors = new[] { "calm", "struggle", "rush", "dive" };
                        seq[i] = behaviors[rng2.Next(behaviors.Length)];
                    }
                }

                // 逃跑概率（整次一次roll）
                double escapeChance = diff <= 25 ? 0 : diff <= 50 ? 0.08 : diff <= 75 ? 0.15 : 0.20;
                if (rng2.NextDouble() < escapeChance)
                {
                    tcs.SetResult(new
                    {
                        ok = true,
                        status = "finished",
                        result = "escaped",
                        caught = false,
                        difficulty = diff,
                        fishEscaped = fishName,
                        message = fishName != null ? $"鱼咬钩后猛力挣脱，跑了！好像是一条{fishName}……" : "鱼咬钩后猛力挣脱，跑了！",
                        taunt = AiFishTaunts[rng2.Next(AiFishTaunts.Length)]
                    });
                    return;
                }

                double startProgress = Math.Max(20, 55 - diff * 0.28) + startBonus;

                var state = new AiFishState
                {
                    Difficulty = diff,
                    TotalPhases = phases,
                    CurrentPhase = 0,
                    Progress = startProgress,
                    Tension = 10,
                    TensionMax = tensionMax,
                    ProgressMult = progressMult,
                    LossReduce = lossReduce,
                    TensionResist = tensionResist,
                    ProgressBonus = progressBonus,
                    ClueBonus = clueBonus,
                    PatternIdx = patIdx,
                    BehaviorSeq = seq,
                    History = new List<object>(),
                    Rng = rng2,
                    RodName = rodName,
                    TackleName = tackleName,
                    BaitName = baitName,
                    FishId = fishId,
                    FishName = fishName,
                };
                _aiFishState = state;

                // Generate first phase clue
                var firstBehavior = seq[0];
                double acc = AiFishClueAccuracy(diff, 0, phases) + clueBonus;
                string clue;
                if (rng2.NextDouble() < acc)
                    clue = AiFishClues[firstBehavior];
                else
                {
                    var others = AiFishClues.Keys.Where(k => k != firstBehavior).ToArray();
                    clue = AiFishClues[others[rng2.Next(others.Length)]];
                }

                string diffLabel = diff <= 25 ? "easy" : diff <= 50 ? "medium" : diff <= 75 ? "hard" : diff <= 95 ? "very_hard" : "legendary";

                tcs.SetResult(new
                {
                    ok = true,
                    status = "fishing",
                    phase = 1,
                    totalPhases = phases,
                    difficulty = diff,
                    difficultyLabel = diffLabel,
                    patternHint = diff <= 40 ? AiFishPatternNames[patIdx] : "???",
                    rod = rodName,
                    tackle = tackleName,
                    bait = baitName,
                    clue,
                    progress = Math.Round(state.Progress, 1),
                    tension = Math.Round(state.Tension, 1),
                    tensionMax = state.TensionMax,
                    message = $"鱼咬钩了！难度:{diffLabel} | 阶段:1/{phases}"
                });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object AiFishDecide(Dictionary<string, object>? p)
    {
        var state = _aiFishState;
        if (state == null)
            return new { ok = false, error = "No active fishing session. Use action='cast' first." };

        string choice = p != null ? GetParamOr(p, "choice", "") : "";
        if (choice != "reel" && choice != "release" && choice != "steady")
            return new { ok = false, error = "Invalid choice. Use 'reel', 'release', or 'steady'." };

        int phase = state.CurrentPhase;
        if (phase >= state.TotalPhases)
        {
            _aiFishState = null;
            return new { ok = false, error = "Fishing session already ended." };
        }

        var rng = state.Rng;
        string behavior = state.BehaviorSeq[phase];
        double strug = AiFishStruggle(state.Difficulty, phase, state.TotalPhases, rng);
        double s = strug / 5.0;

        // Calculate effects
        double dp = 0, dt = 0;
        if (choice == "reel")
        {
            switch (behavior)
            {
                case "calm": dp = 22 + rng.Next(-3, 6); dt = (4 + rng.Next(-1, 3)) * s; break;
                case "struggle": dp = 5 + rng.Next(-3, 4); dt = (18 + rng.Next(-2, 5)) * s; break;
                case "rush": dp = 2 + rng.Next(-4, 3); dt = (24 + rng.Next(-2, 7)) * s; break;
                case "dive": dp = 8 + rng.Next(-3, 4); dt = (11 + rng.Next(-1, 4)) * s; break;
            }
        }
        else if (choice == "release")
        {
            switch (behavior)
            {
                case "calm": dp = -11 + rng.Next(-2, 2); dt = -10 + rng.Next(-2, 2); break;
                case "struggle": dp = -6 + rng.Next(-2, 2); dt = -9 + rng.Next(-1, 2); break;
                case "rush": dp = -3 + rng.Next(-1, 2); dt = -17 + rng.Next(-2, 2); break;
                case "dive": dp = -13 + rng.Next(-3, 1); dt = -5 + rng.Next(-1, 2); break;
            }
        }
        else // steady
        {
            switch (behavior)
            {
                case "calm": dp = 6 + rng.Next(-1, 3); dt = -3 + rng.Next(-1, 2); break;
                case "struggle": dp = 10 + rng.Next(-2, 4); dt = (9 + rng.Next(-1, 3)) * s; break;
                case "rush": dp = 4 + rng.Next(-2, 3); dt = (9 + rng.Next(-1, 4)) * s; break;
                case "dive": dp = 14 + rng.Next(-2, 5); dt = (3 + rng.Next(-1, 3)) * s; break;
            }
        }

        // Apply equipment bonuses
        if (dp > 0)
            dp = dp * state.ProgressMult * (1 + state.ProgressBonus);
        else
            dp = dp * (1 - state.LossReduce);

        if (dt > 0)
            dt = dt * (1 - state.TensionResist);

        state.Progress = Math.Clamp(state.Progress + dp, 0, 100);
        state.Tension = Math.Clamp(state.Tension + dt, 0, 100);

        // Determine if choice was optimal
        string optimal;
        switch (behavior)
        {
            case "calm": optimal = "reel"; break;
            case "struggle": optimal = "steady"; break;
            case "rush": optimal = "release"; break;
            default: optimal = "steady"; break;
        }
        string quality = choice == optimal ? "perfect" : "ok";
        if ((behavior == "rush" && choice == "reel") || (behavior == "calm" && choice == "release") || (behavior == "dive" && choice == "release"))
            quality = "bad";
        if (quality == "perfect") state.PerfectCount++;

        state.History.Add(new
        {
            phase = phase + 1,
            choice,
            actualBehavior = behavior,
            quality,
            progressDelta = Math.Round(dp, 1),
            tensionDelta = Math.Round(dt, 1),
            progress = Math.Round(state.Progress, 1),
            tension = Math.Round(state.Tension, 1),
        });

        state.CurrentPhase++;

        // Check end conditions
        string? result = null;
        string? endMessage = null;
        string? taunt = null;

        if (state.Tension >= state.TensionMax)
        {
            result = "snap";
            taunt = AiFishTaunts[rng.Next(AiFishTaunts.Length)];
            endMessage = $"💥 线断了！{taunt}";
        }
        else if (state.Progress >= 100)
        {
            result = "caught";
            endMessage = "🐟 钓到了！";
        }
        else if (state.Progress <= 0)
        {
            result = "escaped";
            taunt = AiFishTaunts[rng.Next(AiFishTaunts.Length)];
            endMessage = $"鱼跑了...{taunt}";
        }
        else if (state.CurrentPhase >= state.TotalPhases)
        {
            if (state.Difficulty > 75)
            {
                // 传说鱼第6阶段：鱼王决死
                if (state.Progress < 30)
                {
                    result = "escaped";
                    taunt = AiFishTaunts[rng.Next(AiFishTaunts.Length)];
                    endMessage = $"进度太低，鱼王直接逃走了...{taunt}";
                }
                else
                {
                    // 3次猜2次对。一代(diff>100)准确率25%，二代(diff<=100)准确率37%
                    double aiAcc = state.Difficulty > 100 ? 0.25 : 0.37;
                    int correct = 0;
                    for (int i = 0; i < 3; i++)
                        if (rng.NextDouble() < aiAcc) correct++;

                    if (correct >= 2)
                    {
                        result = "caught";
                        endMessage = $"🐟🏆 鱼王决死（{correct}/3）！传说之鱼到手！";
                    }
                    else
                    {
                        result = "escaped";
                        taunt = AiFishTaunts[rng.Next(AiFishTaunts.Length)];
                        endMessage = $"鱼王决死失败（{correct}/3）...{taunt}";
                    }
                }
            }
            else
            {
                double threshold = state.Difficulty <= 50 ? 50 : 55;
                if (state.Progress >= threshold)
                {
                    result = "caught";
                    endMessage = "🐟 鱼精疲力竭，钓到了！";
                }
                else
                {
                    result = "escaped";
                    taunt = AiFishTaunts[rng.Next(AiFishTaunts.Length)];
                    endMessage = $"进度不足，鱼挣脱了...{taunt}";
                }
            }
        }

        if (result != null)
        {
            // Fishing ended
            bool caught = result == "caught";

            // Quality from play: all perfect = gold, half+ = silver
            int fishQuality = 0;
            if (caught && state.CurrentPhase > 0)
            {
                if (state.PerfectCount >= state.CurrentPhase) fishQuality = 2;
                else if (state.PerfectCount * 2 >= state.CurrentPhase) fishQuality = 1;
            }

            if (caught && state.FishName != null)
                endMessage = $"🐟 钓到了一条{state.FishName}！" + (fishQuality == 2 ? "全程完美，金星品质！" : fishQuality == 1 ? "银星品质" : "");
            else if (!caught && state.FishName != null)
                endMessage += $"（跑掉的好像是一条{state.FishName}）";

            var finalResult = new
            {
                ok = true,
                status = "finished",
                result,
                message = endMessage,
                taunt,
                caught,
                fish = state.FishName,
                quality = caught ? fishQuality : (int?)null,
                difficulty = state.Difficulty,
                pattern = AiFishPatternNames[state.PatternIdx],
                finalProgress = Math.Round(state.Progress, 1),
                finalTension = Math.Round(state.Tension, 1),
                phases = state.CurrentPhase,
                history = state.History,
            };

            // Award XP and the real fish if caught
            if (caught)
            {
                var fishId = state.FishId;
                var capturedQuality = fishQuality; // capture for closure
                EnqueueMainThread(() =>
                {
                    var xp = Math.Max(3, state.Difficulty / 8);
                    Game1.player.gainExperience(1, xp); // 1 = fishing skill
                    if (fishId != null)
                    {
                        // Use multiplayer-safe add for farmhand compatibility
                        AddItemSynced(fishId, 1, capturedQuality);
                    }
                });
            }

            _aiFishState = null;
            return finalResult;
        }

        // Generate next phase clue
        int nextPhase = state.CurrentPhase;
        string nextBehavior = state.BehaviorSeq[nextPhase];
        double nextAcc = AiFishClueAccuracy(state.Difficulty, nextPhase, state.TotalPhases) + state.ClueBonus;
        string nextClue;
        if (rng.NextDouble() < nextAcc)
            nextClue = AiFishClues[nextBehavior];
        else
        {
            var others = AiFishClues.Keys.Where(k => k != nextBehavior).ToArray();
            nextClue = AiFishClues[others[rng.Next(others.Length)]];
        }

        return new
        {
            ok = true,
            status = "ongoing",
            phase = nextPhase + 1,
            totalPhases = state.TotalPhases,
            lastChoice = choice,
            lastBehavior = behavior,
            lastQuality = quality,
            clue = nextClue,
            progress = Math.Round(state.Progress, 1),
            tension = Math.Round(state.Tension, 1),
            tensionMax = state.TensionMax,
            history = state.History,
            message = $"阶段{nextPhase + 1}/{state.TotalPhases} | 进度:{Math.Round(state.Progress, 1)}% 张力:{Math.Round(state.Tension, 1)}/{state.TensionMax}"
        };
    }

    private static double AiFishClueAccuracy(int diff, int phase, int total)
    {
        double baseAcc = Math.Max(0.32, 1.0 - diff / 145.0);
        if (phase < 2)
            return Math.Min(0.94, baseAcc + 0.30);
        double decay = (phase - 1) * 0.09;
        return Math.Max(0.22, baseAcc - decay);
    }

    private static double AiFishStruggle(int diff, int phase, int total, Random rng)
    {
        double baseVal = diff / 10.0;
        double phaseFactor = total > 1 ? (double)phase / (total - 1) : 0;
        return Math.Clamp(baseVal + (rng.NextDouble() * 3.0 - 1.5) + phaseFactor * 2.5, 1, 10);
    }

    /// <summary>
    /// POST /emote  { "id": 16 }
    /// Plays an emote animation on the farmer.
    /// Common emote IDs: 16=happy, 20=sad, 24=heart, 28=exclamation, 32=note, 36=sleep, 40=game, 52=angry, 56=laugh, 60=blush
    /// </summary>
    private object HandleEmote(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var id = GetParam<int>(p, "id");

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        EnqueueMainThread(() =>
        {
            Game1.player.doEmote(id);
        });

        return new { ok = true, emoteId = id };
    }

    /// <summary>
    /// GET /state
    /// Returns comprehensive game state.
    /// </summary>
    private object HandleState()
    {
        if (!Context.IsWorldReady)
            return new { ok = true, worldReady = false };

        var farmer = Game1.player;
        var loc = farmer.currentLocation;

        var npcs = loc.characters
            .Select(n => new
            {
                name = n.Name,
                x = n.TilePoint.X,
                y = n.TilePoint.Y
            }).ToList();

        var inventory = farmer.Items
            .Where(i => i != null)
            .Select(i =>
            {
                var entry = new Dictionary<string, object?>
                {
                    ["name"] = i.Name,
                    ["stack"] = i.Stack,
                    ["category"] = i.getCategoryName()
                };
                if (i is WateringCan wc)
                {
                    entry["waterLeft"] = wc.WaterLeft;
                    entry["waterMax"] = wc.waterCanMax;
                }
                return entry;
            }).ToList();

        var menuInfo = (object?)null;
        if (Game1.activeClickableMenu != null)
        {
            var menuType = Game1.activeClickableMenu.GetType().Name;
            var dialogueText = "";
            if (Game1.activeClickableMenu is StardewValley.Menus.DialogueBox db)
            {
                try { dialogueText = db.getCurrentString() ?? ""; } catch { }
            }
            menuInfo = new
            {
                type = menuType,
                dialogue = string.IsNullOrEmpty(dialogueText) ? null : dialogueText
            };
        }

        var eventInfo = (object?)null;
        if (loc.currentEvent != null)
        {
            var ev = loc.currentEvent;
            string? evDialogue = null;
            if (Game1.activeClickableMenu is DialogueBox evDb)
            {
                try { evDialogue = evDb.getCurrentString(); } catch { }
            }
            eventInfo = new
            {
                id = ev.id,
                skippable = ev.skippable,
                message = evDialogue
            };
        }

        return new
        {
            ok = true,
            worldReady = true,
            player = new
            {
                name = farmer.Name,
                x = farmer.TilePoint.X,
                y = farmer.TilePoint.Y,
                health = farmer.health,
                maxHealth = farmer.maxHealth,
                stamina = farmer.Stamina,
                maxStamina = farmer.MaxStamina,
                money = farmer.Money,
                currentTool = farmer.CurrentTool?.Name,
                facingDirection = farmer.FacingDirection,
                isMoving = _pathQueue != null && _pathQueue.Count > 0,
                fishing = farmer.CurrentTool is FishingRod rod ? new
                {
                    isCasting = rod.isTimingCast,
                    isFishing = rod.isFishing,
                    isNibbling = rod.isNibbling,
                    isReeling = rod.isReeling,
                    hit = rod.hit
                } : null
            },
            location = new
            {
                name = loc.Name,
                mapWidth = loc.Map.DisplayWidth / 64,
                mapHeight = loc.Map.DisplayHeight / 64
            },
            time = new
            {
                timeOfDay = Game1.timeOfDay,
                dayOfMonth = Game1.dayOfMonth,
                season = Game1.currentSeason,
                year = Game1.year
            },
            activeMenu = menuInfo,
            activeEvent = eventInfo,
            npcs,
            inventory
        };
    }

    /// <summary>
    /// GET /surroundings  ?radius=10
    /// Returns tile info around the player: passability, objects, terrain features, buildings, NPCs.
    /// </summary>
    private object HandleSurroundings(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var qs = ctx.Request.QueryString;
        int radius = 10;
        if (int.TryParse(qs["radius"], out var r) && r > 0 && r <= 30)
            radius = r;

        var farmer = Game1.player;
        var loc = farmer.currentLocation;
        var cx = farmer.TilePoint.X;
        var cy = farmer.TilePoint.Y;

        var tiles = new List<object>();

        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                int tx = cx + dx, ty = cy + dy;
                if (tx < 0 || ty < 0) continue;
                var mapW = loc.Map.DisplayWidth / 64;
                var mapH = loc.Map.DisplayHeight / 64;
                if (tx >= mapW || ty >= mapH) continue;

                var tileVec = new Vector2(tx, ty);
                var passable = loc.isTilePassable(tileVec);

                string? objName = null;
                if (loc.objects.TryGetValue(tileVec, out var obj))
                    objName = obj.Name;

                string? terrainName = null;
                bool diggable = loc.doesTileHaveProperty(tx, ty, "Diggable", "Back") != null;
                bool watered = false;
                string? cropName = null;
                int cropPhase = -1;
                bool harvestable = false;

                if (loc.terrainFeatures.TryGetValue(tileVec, out var tf))
                {
                    terrainName = tf.GetType().Name;
                    if (tf is HoeDirt dirt)
                    {
                        terrainName = "HoeDirt";
                        watered = dirt.state.Value == 1;
                        if (dirt.crop != null)
                        {
                            cropName = dirt.crop.indexOfHarvest.Value;
                            cropPhase = dirt.crop.currentPhase.Value;
                            harvestable = dirt.readyForHarvest();
                        }
                    }
                    else if (tf is Tree tree)
                    {
                        terrainName = $"Tree:{tree.treeType.Value}";
                    }
                    else if (tf is GiantCrop gc)
                    {
                        terrainName = "GiantCrop";
                    }
                }

                string? resourceName = null;
                var clump = loc.resourceClumps.FirstOrDefault(c =>
                    c.Tile == tileVec || (tx >= c.Tile.X && tx < c.Tile.X + c.width.Value
                    && ty >= c.Tile.Y && ty < c.Tile.Y + c.height.Value));
                if (clump != null)
                    resourceName = clump.parentSheetIndex.Value switch
                    {
                        600 => "LargeStump",
                        602 => "LargeLog",
                        622 => "MeteoriteOre",
                        672 => "LargeBoulder",
                        752 => "LargeBoulder",
                        754 => "LargeBoulder",
                        _ => $"Clump:{clump.parentSheetIndex.Value}"
                    };

                bool hasInfo = !passable || objName != null || terrainName != null
                    || resourceName != null || diggable || cropName != null;
                if (hasInfo)
                {
                    var tile = new Dictionary<string, object?> { ["x"] = tx, ["y"] = ty, ["passable"] = passable };
                    if (diggable) tile["diggable"] = true;
                    if (objName != null) tile["object"] = objName;
                    if (terrainName != null) tile["terrain"] = terrainName;
                    if (resourceName != null) tile["resource"] = resourceName;
                    if (cropName != null)
                    {
                        tile["crop"] = cropName;
                        tile["cropPhase"] = cropPhase;
                        tile["harvestable"] = harvestable;
                    }
                    if (watered) tile["watered"] = true;
                    tiles.Add(tile);
                }
            }
        }

        var nearbyNpcs = loc.characters
            .Where(n => !(n is Monster) && Math.Abs(n.TilePoint.X - cx) <= radius && Math.Abs(n.TilePoint.Y - cy) <= radius)
            .Select(n => new { name = n.Name, x = n.TilePoint.X, y = n.TilePoint.Y })
            .ToList();

        var nearbyMonsters = loc.characters
            .OfType<Monster>()
            .Where(m => Math.Abs(m.TilePoint.X - cx) <= radius && Math.Abs(m.TilePoint.Y - cy) <= radius)
            .Select(m => new { name = m.Name, x = m.TilePoint.X, y = m.TilePoint.Y, health = m.Health, maxHealth = m.MaxHealth })
            .ToList();

        var nearbyFarmers = Game1.getOnlineFarmers()
            .Where(f => f != farmer && f.currentLocation == loc
                && Math.Abs(f.TilePoint.X - cx) <= radius && Math.Abs(f.TilePoint.Y - cy) <= radius)
            .Select(f => new { name = f.Name, x = f.TilePoint.X, y = f.TilePoint.Y })
            .ToList();

        return new
        {
            ok = true,
            center = new { x = cx, y = cy },
            radius,
            location = loc.Name,
            tiles,
            npcs = nearbyNpcs,
            monsters = nearbyMonsters,
            farmers = nearbyFarmers
        };
    }

    /// <summary>
    /// GET /alerts ?peek=true
    /// Returns queued game/system alerts. By default this drains the queue.
    /// </summary>
    private object HandleAlerts(HttpListenerContext ctx)
    {
        var qs = ctx.Request.QueryString;
        bool peek = bool.TryParse(qs["peek"], out var p) && p;

        lock (_alertLock)
        {
            var alerts = _alertQueue.ToList();
            if (!peek)
                _alertQueue.Clear();

            return new
            {
                ok = true,
                count = alerts.Count,
                alerts
            };
        }
    }

    /// <summary>
    /// POST /face  { "direction": 2 }
    /// Sets the farmer's facing direction. 0=up, 1=right, 2=down, 3=left
    /// </summary>
    private object HandleFace(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var dir = GetParam<int>(p, "direction");
        if (dir < 0 || dir > 3)
            throw new InvalidOperationException("direction must be 0-3 (up/right/down/left)");

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            Game1.player.FacingDirection = dir;
            tcs.SetResult(new { ok = true, direction = dir });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// POST /select  { "name": "Parsnip Seeds" }
    /// Selects an inventory item by name (sets it as the active toolbar slot).
    /// </summary>
    private object HandleSelect(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var name = GetParam<string>(p, "name");

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;
            var idx = -1;
            for (int i = 0; i < farmer.Items.Count; i++)
            {
                if (farmer.Items[i] != null &&
                    farmer.Items[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    idx = i;
                    break;
                }
            }

            if (idx < 0)
            {
                tcs.SetResult(new { ok = false, error = $"Item '{name}' not found in inventory" });
                return;
            }

            farmer.CurrentToolIndex = idx;
            tcs.SetResult(new { ok = true, selected = name, slot = idx });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// POST /use  { "force": false }
    /// Uses the currently held item with pre-validation.
    /// Tools: checks if facing tile is appropriate (hoe→diggable empty, wateringcan→HoeDirt, axe→tree/stump, pickaxe→stone).
    /// Placeables: checks tile is clear. Pass force=true to skip validation.
    /// </summary>
    private object HandleUse(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var p = ReadJson(ctx);
        var force = GetParamOr(p, "force", false);

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;
            var item = farmer.CurrentItem;
            if (item == null)
            {
                tcs.SetResult(new { ok = false, error = "No item selected" });
                return;
            }

            var facingTile = GetFacingTile(farmer);
            var loc = farmer.currentLocation;
            int ftx = (int)facingTile.X, fty = (int)facingTile.Y;
            var tileVec = new Vector2(ftx, fty);

            if (item is Tool tool && !force)
            {
                var validation = ValidateToolUse(tool, loc, tileVec, ftx, fty);
                if (validation != null)
                {
                    tcs.SetResult(new { ok = false, error = validation,
                        tile = new { x = ftx, y = fty }, tool = tool.Name });
                    return;
                }
            }

            if (item is Tool)
            {
                farmer.BeginUsingTool();
                tcs.SetResult(new { ok = true, action = "tool", item = item.Name,
                    tile = new { x = ftx, y = fty } });
            }
            else if (item is StardewValley.Object obj)
            {
                int px = ftx * 64, py = fty * 64;
                bool placed = obj.placementAction(loc, px, py, farmer);
                if (placed)
                {
                    farmer.reduceActiveItemByOne();
                    tcs.SetResult(new { ok = true, action = "placed", item = item.Name,
                        tile = new { x = ftx, y = fty } });
                }
                else
                {
                    tcs.SetResult(new { ok = false, error = $"Cannot place '{item.Name}' here",
                        tile = new { x = ftx, y = fty } });
                }
            }
            else
            {
                tcs.SetResult(new { ok = false, error = $"Cannot use '{item.Name}' (unsupported item type)" });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private string? ValidateToolUse(Tool tool, GameLocation loc, Vector2 tileVec, int tx, int ty)
    {
        bool hasObj = loc.objects.ContainsKey(tileVec);
        loc.terrainFeatures.TryGetValue(tileVec, out var tf);
        bool diggable = loc.doesTileHaveProperty(tx, ty, "Diggable", "Back") != null;

        switch (tool)
        {
            case Hoe:
                if (tf is HoeDirt)
                    return "Tile already tilled";
                if (hasObj)
                    return $"Tile blocked by object: {loc.objects[tileVec].Name}";
                if (!diggable)
                    return "Tile is not diggable";
                return null;

            case WateringCan:
                if (tf is not HoeDirt dirt)
                    return "No tilled soil here — till first";
                if (dirt.state.Value == 1)
                    return "Already watered";
                return null;

            case Axe:
                bool hasTree = tf is Tree;
                bool hasStump = loc.resourceClumps.Any(c =>
                    (c.parentSheetIndex.Value == 600 || c.parentSheetIndex.Value == 602)
                    && tx >= c.Tile.X && tx < c.Tile.X + c.width.Value
                    && ty >= c.Tile.Y && ty < c.Tile.Y + c.height.Value);
                bool hasTwig = hasObj && loc.objects[tileVec].Name == "Twig";
                if (!hasTree && !hasStump && !hasTwig)
                    return "Nothing to chop here";
                return null;

            case Pickaxe:
                bool hasStone = hasObj && loc.objects[tileVec].Name == "Stone";
                bool hasBoulder = loc.resourceClumps.Any(c =>
                    (c.parentSheetIndex.Value == 672 || c.parentSheetIndex.Value == 752 || c.parentSheetIndex.Value == 754 || c.parentSheetIndex.Value == 622)
                    && tx >= c.Tile.X && tx < c.Tile.X + c.width.Value
                    && ty >= c.Tile.Y && ty < c.Tile.Y + c.height.Value);
                if (!hasStone && !hasBoulder && tf is not HoeDirt)
                    return "Nothing to break here";
                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// GET /map
    /// Returns buildings, warps, NPCs, and other farmers for the current location.
    /// Provides everything needed for long-range pathfinding and navigation.
    /// </summary>
    private object HandleMap()
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;
            var loc = farmer.currentLocation;
            var mapWidth = loc.Map.DisplayWidth / 64;
            var mapHeight = loc.Map.DisplayHeight / 64;

            // Buildings (Farm, etc.)
            var buildings = new List<object>();
            if (loc is Farm farm)
            {
                foreach (var b in farm.buildings)
                {
                    var entry = new Dictionary<string, object?>
                    {
                        ["type"] = b.buildingType.Value,
                        ["x"] = b.tileX.Value,
                        ["y"] = b.tileY.Value,
                        ["width"] = b.tilesWide.Value,
                        ["height"] = b.tilesHigh.Value
                    };
                    if (b.humanDoor.Value != Point.Zero || b.humanDoor.Value != default)
                    {
                        entry["doorX"] = b.tileX.Value + b.humanDoor.X;
                        entry["doorY"] = b.tileY.Value + b.humanDoor.Y;
                    }
                    buildings.Add(entry);
                }
            }

            // Warps (exits/entrances to other maps)
            var warps = loc.warps
                .Select(w => new
                {
                    x = w.X,
                    y = w.Y,
                    targetLocation = w.TargetName,
                    targetX = w.TargetX,
                    targetY = w.TargetY
                }).ToList();

            // All NPCs in current location
            var npcs = loc.characters
                .Select(n => new
                {
                    name = n.Name,
                    x = n.TilePoint.X,
                    y = n.TilePoint.Y
                }).ToList();

            // All other farmers in current location
            var farmers = Game1.getOnlineFarmers()
                .Where(f => f != farmer && f.currentLocation == loc)
                .Select(f => new
                {
                    name = f.Name,
                    x = f.TilePoint.X,
                    y = f.TilePoint.Y
                }).ToList();

            // Animals (if on farm or animal building interior)
            var animals = new List<object>();
            if (loc is Farm farmLoc)
            {
                foreach (var a in farmLoc.animals.Values)
                    animals.Add(new { name = a.Name, type = a.type.Value, x = a.TilePoint.X, y = a.TilePoint.Y });
            }
            else if (loc is AnimalHouse ah)
            {
                foreach (var a in ah.animals.Values)
                    animals.Add(new { name = a.Name, type = a.type.Value, x = a.TilePoint.X, y = a.TilePoint.Y });
            }

            tcs.SetResult(new
            {
                ok = true,
                player = new { x = farmer.TilePoint.X, y = farmer.TilePoint.Y },
                location = new
                {
                    name = loc.Name,
                    width = mapWidth,
                    height = mapHeight
                },
                buildings,
                warps,
                npcs,
                farmers,
                animals
            });
        });

        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// POST /buy  { "id": "472", "quantity": 5 }  or  { "id": "(O)472", "quantity": 5 }
    /// Buys an item: deducts gold, adds item to inventory.
    /// Optional "price" param to override per-unit cost; otherwise uses the item's default sale price * 2 (shop markup).
    /// </summary>
    private object HandleBuy(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var rawId = GetParam<string>(p, "id");
        var quantity = GetParamOr(p, "quantity", 1);
        var priceOverride = GetParamOr(p, "price", -1);

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                // Qualify the item ID if needed (e.g. "472" -> "(O)472")
                var qualifiedId = rawId.StartsWith("(") ? rawId : ItemRegistry.QualifyItemId(rawId);
                if (qualifiedId == null)
                {
                    tcs.SetResult(new { ok = false, error = $"Unknown item ID: {rawId}" });
                    return;
                }

                // Create a test item to get its info
                var testItem = ItemRegistry.Create(qualifiedId, 1);
                if (testItem == null)
                {
                    tcs.SetResult(new { ok = false, error = $"Cannot create item: {qualifiedId}" });
                    return;
                }

                // Calculate price: override > default (salePrice * 2 as shop markup)
                int unitPrice = priceOverride >= 0
                    ? priceOverride
                    : (testItem is StardewValley.Object obj ? obj.salePrice() * 2 : 100);
                int totalCost = unitPrice * quantity;

                var farmer = Game1.player;
                if (farmer.Money < totalCost)
                {
                    tcs.SetResult(new { ok = false, error = $"Not enough gold. Need {totalCost}g, have {farmer.Money}g",
                        need = totalCost, have = farmer.Money });
                    return;
                }

                // Check inventory space
                int freeSlots = 0;
                for (int i = 0; i < farmer.MaxItems; i++)
                {
                    if (i >= farmer.Items.Count || farmer.Items[i] == null)
                        freeSlots++;
                }
                if (freeSlots < 1)
                {
                    tcs.SetResult(new { ok = false, error = "Inventory full! Please clear backpack before buying.",
                        freeSlots = 0 });
                    EnqueueAlert("inventory_full", "Cannot buy: inventory is full. Clear backpack first.", "warning", "buy");
                    return;
                }

                // Create the actual item and add to inventory (multiplayer-safe)
                var item = ItemRegistry.Create(qualifiedId, quantity);
                farmer.Money -= totalCost;
                AddItemSynced(qualifiedId, quantity);

                tcs.SetResult(new
                {
                    ok = true,
                    bought = item.Name,
                    quantity,
                    unitPrice,
                    totalCost,
                    remainingGold = farmer.Money
                });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });

        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// POST /sleep
    /// Warps the farmer to their bed and triggers sleep (end of day).
    /// </summary>
    private object HandleSleep()
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                var farmer = Game1.player;

                // Find home: try homeLocation, then scan all locations for a cabin belonging to this farmer
                var homeName = farmer.homeLocation.Value;
                GameLocation homeLoc = null;
                if (!string.IsNullOrEmpty(homeName))
                    homeLoc = Game1.getLocationFromName(homeName);

                if (homeLoc == null)
                {
                    // Scan for cabin with this farmer's unique ID
                    foreach (var loc in Game1.locations)
                    {
                        if (loc is StardewValley.Locations.Cabin cabin && cabin.owner == farmer)
                        {
                            homeLoc = cabin;
                            homeName = cabin.Name;
                            break;
                        }
                    }
                }

                // Fallback to FarmHouse for host
                if (homeLoc == null)
                {
                    homeLoc = Game1.getLocationFromName("FarmHouse");
                    homeName = "FarmHouse";
                }

                if (homeLoc == null)
                {
                    tcs.SetResult(new { ok = false, error = "Cannot find home location" });
                    return;
                }

                // Dynamically find bed position instead of hardcoding
                var bedX = 10;
                var bedY = 6;
                if (homeLoc is StardewValley.Locations.FarmHouse fh)
                {
                    var bedSpot = fh.GetPlayerBedSpot();
                    bedX = bedSpot.X;
                    bedY = bedSpot.Y;
                }
                else if (homeLoc is StardewValley.Locations.Cabin cab)
                {
                    var bedSpot = cab.GetPlayerBedSpot();
                    bedX = bedSpot.X;
                    bedY = bedSpot.Y;
                }

                var needsWarp = farmer.currentLocation.NameOrUniqueName != homeLoc.NameOrUniqueName;
                if (needsWarp)
                {
                    // Cabins are building interiors (structures); a plain name warp
                    // fails to resolve them and dumps the farmer at a garbage position
                    Game1.warpFarmer(
                        Game1.getLocationRequest(homeLoc.NameOrUniqueName, homeLoc.isStructure.Value),
                        bedX, bedY, 2);
                }

                // Longer delay for farmhand warp sync
                var delay = needsWarp ? 3000 : 500;
                DelayedAction.functionAfterDelay(() =>
                {
                    // Verify the warp actually landed us home; retry once if it missed
                    var f = Game1.player;
                    if (f.currentLocation?.NameOrUniqueName != homeLoc.NameOrUniqueName)
                    {
                        Game1.warpFarmer(
                            Game1.getLocationRequest(homeLoc.NameOrUniqueName, homeLoc.isStructure.Value),
                            bedX, bedY, 2);
                    }

                    DelayedAction.functionAfterDelay(() =>
                    {
                        var f2 = Game1.player;
                        // isInBed only sticks if the farmer is physically on the bed tile
                        if (Math.Abs(f2.TilePoint.X - bedX) > 1 || Math.Abs(f2.TilePoint.Y - bedY) > 1)
                            f2.Position = new Vector2(bedX * 64f, bedY * 64f);

                        f2.isInBed.Value = true;
                        f2.sleptInTemporaryBed.Value = false;
                        f2.currentLocation.answerDialogueAction("Sleep_Yes", Array.Empty<string>());

                        DelayedAction.functionAfterDelay(() =>
                        {
                            if (Game1.activeClickableMenu != null)
                            {
                                Game1.player.currentLocation.answerDialogueAction("Sleep_Yes", Array.Empty<string>());
                                Game1.pressActionButton(Game1.input.GetKeyboardState(), Game1.input.GetMouseState(),
                                    Game1.input.GetGamePadState());
                            }
                        }, 1000);
                    }, 1000);
                }, delay);

                tcs.SetResult(new { ok = true, action = "sleeping", home = homeName, bed = $"{bedX},{bedY}" });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// POST /wakeup
    /// After sleeping / new day, walks the farmer out of their cabin to the farm.
    /// Returns current location and position.
    /// </summary>
    private object HandleWakeup()
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;
            var loc = farmer.currentLocation;

            // Find any warp out of current indoor location
            var warp = loc.warps.FirstOrDefault();
            if (warp != null)
            {
                // Directly warp the farmer - more reliable than walking
                Game1.warpFarmer(warp.TargetName, warp.TargetX, warp.TargetY, false);
                tcs.SetResult(new
                {
                    ok = true,
                    action = "warped",
                    from = loc.Name,
                    target = warp.TargetName,
                    x = warp.TargetX,
                    y = warp.TargetY
                });
            }
            else
            {
                tcs.SetResult(new { ok = true, action = "already_outside", location = loc.Name,
                    x = farmer.TilePoint.X, y = farmer.TilePoint.Y });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// POST /stop
    /// Cancels current movement.
    /// </summary>
    /// <summary>
    /// POST /queue  [{"action":"move","x":60,"y":17},{"action":"select","name":"Hoe"},{"action":"face","direction":2},{"action":"use"},...]
    /// Executes a sequence of commands automatically. Supported actions: move, face, select, use, interact, wait.
    /// Returns all results when the queue finishes.
    /// <summary>
    /// POST /key  { "key": "confirm" }
    /// Simulates a key press. Used to advance dialogue, confirm menus, skip cutscenes.
    /// Supported keys: confirm (action button), cancel (back/menu), skip (escape)
    /// </summary>
    /// <summary>
    /// POST /warp  { "location": "Beach", "x": 20, "y": 4 }
    /// Teleports the farmer to any game location. If x/y omitted, warps to default entry point.
    /// Common locations: Farm, Town, Beach, Mountain, Forest, Mine, BusStop, Desert, FishShop
    /// </summary>
    private object HandleWarp(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var location = GetParam<string>(p, "location");
        var x = GetParamOr(p, "x", -1);
        var y = GetParamOr(p, "y", -1);

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var shopLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "SeedShop", "FishShop", "Blacksmith", "ScienceHouse", "AnimalShop", "Saloon", "AdventureGuild", "Hospital", "HatShop", "DesertTrade", "QiGemShop" };
        if (shopLocations.Contains(location) && Game1.player.freeSpotsInInventory() == 0)
            return new { ok = false, error = "Inventory full! Clear backpack before going to a shop.", freeSlots = 0 };

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                var targetLoc = Game1.getLocationFromName(location);
                if (targetLoc == null)
                {
                    tcs.SetResult(new { ok = false, error = $"Location '{location}' not found" });
                    return;
                }

                ClearMovementState();

                // If no coordinates given, try to find a reasonable entry point
                if (x < 0 || y < 0)
                {
                    // Use the first warp that targets this location from current map, or default center
                    var farmer = Game1.player;
                    var curWarps = farmer.currentLocation.warps;
                    var matchWarp = curWarps.FirstOrDefault(w => w.TargetName == location);
                    if (matchWarp != null)
                    {
                        Game1.warpFarmer(location, matchWarp.TargetX, matchWarp.TargetY, false);
                    }
                    else
                    {
                        // Default: warp to center-ish of map
                        var mw = targetLoc.Map.DisplayWidth / 64;
                        var mh = targetLoc.Map.DisplayHeight / 64;
                        Game1.warpFarmer(location, mw / 2, mh / 2, false);
                    }
                }
                else
                {
                    var farmer = Game1.player;
                    if (farmer.currentLocation.Name == location)
                    {
                        farmer.Position = new Vector2(x, y) * Game1.tileSize;
                        CenterViewportOnFarmer(farmer);
                    }
                    else
                    {
                        Game1.warpFarmer(location, x, y, false);
                    }
                }

                var f = Game1.player;
                tcs.SetResult(new
                {
                    ok = true,
                    action = "warped",
                    requested = new { location, x, y },
                    actual = new { location = f.currentLocation.Name, x = f.TilePoint.X, y = f.TilePoint.Y }
                });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// POST /position { "x": 10, "y": 15 }
    /// Sets the farmer position on the current map and centers the camera.
    /// </summary>
    private object HandlePosition(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var x = GetParam<int>(p, "x");
        var y = GetParam<int>(p, "y");

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            ClearMovementState();
            var farmer = Game1.player;
            farmer.Position = new Vector2(x, y) * Game1.tileSize;
            CenterViewportOnFarmer(farmer);
            tcs.SetResult(new
            {
                ok = true,
                action = "positioned",
                location = farmer.currentLocation.Name,
                x = farmer.TilePoint.X,
                y = farmer.TilePoint.Y
            });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleKey(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var p = ReadJson(ctx);
        var key = GetParamOr(p, "key", "confirm");
        var count = GetParamOr(p, "count", 1);

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                for (int i = 0; i < count; i++)
                {
                    switch (key.ToLower())
                    {
                        case "confirm":
                        case "action":
                            if (Game1.currentMinigame != null)
                            {
                                Game1.currentMinigame.receiveKeyPress(Keys.Enter);
                                keybd_event(0x0D, 0, 0, UIntPtr.Zero);
                                System.Threading.Thread.Sleep(50);
                                keybd_event(0x0D, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                            }
                            else if (Game1.activeClickableMenu is DialogueBox dialogueBox)
                            {
                                dialogueBox.receiveLeftClick(0, 0);
                            }
                            else if (Game1.activeClickableMenu != null)
                            {
                                Game1.activeClickableMenu.receiveLeftClick(
                                    Game1.activeClickableMenu.xPositionOnScreen + Game1.activeClickableMenu.width / 2,
                                    Game1.activeClickableMenu.yPositionOnScreen + Game1.activeClickableMenu.height / 2);
                            }
                            else if (Game1.currentLocation?.currentEvent != null)
                            {
                                Game1.currentLocation.currentEvent.receiveActionPress(0, 0);
                            }
                            else if (Game1.input != null)
                            {
                                Game1.pressActionButton(Game1.input.GetKeyboardState(), Game1.input.GetMouseState(),
                                    Game1.input.GetGamePadState());
                            }
                            break;
                        case "ok":
                            if (Game1.activeClickableMenu != null)
                            {
                                var okBtn = Game1.activeClickableMenu.GetType()
                                    .GetField("okButton", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)?
                                    .GetValue(Game1.activeClickableMenu) as ClickableTextureComponent;
                                if (okBtn != null)
                                {
                                    Game1.activeClickableMenu.receiveLeftClick(
                                        okBtn.bounds.Center.X, okBtn.bounds.Center.Y);
                                }
                                else
                                {
                                    Game1.activeClickableMenu.exitThisMenu();
                                }
                            }
                            break;
                        case "menu":
                            if (Game1.activeClickableMenu != null)
                                Game1.activeClickableMenu.receiveKeyPress(Keys.Escape);
                            else
                                Game1.activeClickableMenu = new GameMenu();
                            break;
                        case "cancel":
                        case "back":
                            if (Game1.activeClickableMenu != null)
                                Game1.activeClickableMenu.receiveKeyPress(Keys.Escape);
                            else if (Game1.input != null)
                                Game1.pressUseToolButton();
                            break;
                        case "skip":
                        case "escape":
                            if (Game1.currentLocation?.currentEvent != null)
                            {
                                Game1.currentLocation.currentEvent.skipped = true;
                                Game1.currentLocation.currentEvent.skipEvent();
                            }
                            else
                            {
                                Game1.currentMinigame?.receiveKeyPress(Keys.Escape);
                                if (Game1.activeClickableMenu != null)
                                    Game1.activeClickableMenu.receiveKeyPress(Keys.Escape);
                            }
                            break;
                        default:
                            byte? virtualKey = null;
                            if (key.ToLower().StartsWith("f") && int.TryParse(key.Substring(1), out int fNum) && fNum >= 1 && fNum <= 12)
                                virtualKey = (byte)(0x70 + fNum - 1);
                            else if (key.Equals("space", StringComparison.OrdinalIgnoreCase)) virtualKey = 0x20;
                            else if (key.Equals("enter", StringComparison.OrdinalIgnoreCase)) virtualKey = 0x0D;
                            else if (key.Equals("up", StringComparison.OrdinalIgnoreCase)) virtualKey = 0x26;
                            else if (key.Equals("down", StringComparison.OrdinalIgnoreCase)) virtualKey = 0x28;
                            else if (key.Equals("left", StringComparison.OrdinalIgnoreCase)) virtualKey = 0x25;
                            else if (key.Equals("right", StringComparison.OrdinalIgnoreCase)) virtualKey = 0x27;
                            else if (key.Equals("w", StringComparison.OrdinalIgnoreCase)) virtualKey = 0x57;
                            else if (key.Equals("a", StringComparison.OrdinalIgnoreCase)) virtualKey = 0x41;
                            else if (key.Equals("s", StringComparison.OrdinalIgnoreCase)) virtualKey = 0x53;
                            else if (key.Equals("d", StringComparison.OrdinalIgnoreCase)) virtualKey = 0x44;
                            if (virtualKey.HasValue)
                            {
                                if (Game1.currentMinigame != null && Enum.TryParse<Keys>(key, true, out var xnaKey))
                                    Game1.currentMinigame.receiveKeyPress(xnaKey);
                                keybd_event(virtualKey.Value, 0, 0, UIntPtr.Zero);
                                System.Threading.Thread.Sleep(50);
                                keybd_event(virtualKey.Value, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                            }
                            break;
                    }
                }
                tcs.SetResult(new { ok = true, key, count });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    /// </summary>
    private object HandleQueue(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var body = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(body))
            throw new InvalidOperationException("Empty command queue");

        var commands = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(body);
        if (commands == null || commands.Count == 0)
            throw new InvalidOperationException("No commands in queue");

        _commandQueueTcs = new TaskCompletionSource<object>();
        _commandResults.Clear();

        EnqueueMainThread(() =>
        {
            _commandQueue = new Queue<Dictionary<string, object?>>(commands);
            _commandDelay = 0;
            _waitingForMove = false;
        });

        // Wait for all commands to execute (timeout 5 minutes)
        if (_commandQueueTcs.Task.Wait(TimeSpan.FromMinutes(5)))
            return _commandQueueTcs.Task.Result;
        else
            return new { ok = false, error = "Queue execution timed out", executed = _commandResults.Count };
    }

    private object HandleStop()
    {
        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            ClearMovementState();
            var farmer = Game1.player;
            tcs.SetResult(new
            {
                ok = true,
                message = "Movement stopped",
                location = farmer.currentLocation.Name,
                x = farmer.TilePoint.X,
                y = farmer.TilePoint.Y
            });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandlePlaceChest(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var cx = GetParam<int>(p, "x");
        var cy = GetParam<int>(p, "y");

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var loc = Game1.player.currentLocation;
            var tileVec = new Vector2(cx, cy);

            if (loc.objects.ContainsKey(tileVec))
            {
                tcs.SetResult(new { ok = false, error = $"Tile ({cx},{cy}) already has an object" });
                return;
            }

            var chest = new StardewValley.Objects.Chest(true, tileVec);
            loc.objects.Add(tileVec, chest);
            tcs.SetResult(new { ok = true, placed = "Chest", x = cx, y = cy });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleStore(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var cx = GetParam<int>(p, "x");
        var cy = GetParam<int>(p, "y");
        var name = GetParamOr(p, "name", "");
        var keepTools = GetParamOr(p, "keepTools", true);

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;
            var loc = farmer.currentLocation;
            var tileVec = new Vector2(cx, cy);

            if (!loc.objects.TryGetValue(tileVec, out var obj) || obj is not StardewValley.Objects.Chest chest)
            {
                tcs.SetResult(new { ok = false, error = $"No chest at ({cx},{cy})" });
                return;
            }

            var stored = new List<object>();
            for (int i = farmer.Items.Count - 1; i >= 0; i--)
            {
                var item = farmer.Items[i];
                if (item == null) continue;
                if (keepTools && item is Tool) continue;
                if (!string.IsNullOrEmpty(name)
                    && !item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    continue;

                var leftover = chest.addItem(item);
                if (leftover == null)
                {
                    stored.Add(new { item = item.Name, count = item.Stack });
                    farmer.Items[i] = null;
                }
                else if (leftover.Stack < item.Stack)
                {
                    stored.Add(new { item = item.Name, count = item.Stack - leftover.Stack });
                    farmer.Items[i] = leftover;
                }
            }

            tcs.SetResult(new { ok = true, stored, chestAt = new { x = cx, y = cy } });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleChest(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var cx = GetParam<int>(p, "x");
        var cy = GetParam<int>(p, "y");

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;
            var loc = farmer.currentLocation;
            var tileVec = new Vector2(cx, cy);

            if (!loc.objects.TryGetValue(tileVec, out var obj) || obj is not StardewValley.Objects.Chest chest)
            {
                tcs.SetResult(new { ok = false, error = $"No chest at ({cx},{cy})" });
                return;
            }

            var items = chest.Items
                .Where(i => i != null)
                .Select(i => new { name = i.Name, count = i.Stack })
                .ToList();

            tcs.SetResult(new { ok = true, items, capacity = chest.GetActualCapacity(), used = items.Count });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleHarvest(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var qs = ctx.Request.QueryString;
        int radius = 15;
        if (int.TryParse(qs["radius"], out var r) && r > 0 && r <= 50)
            radius = r;

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;
            var loc = farmer.currentLocation;
            int count = 0;

            foreach (var pair in loc.terrainFeatures.Pairs)
            {
                if (pair.Value is HoeDirt dirt && dirt.crop != null && dirt.readyForHarvest())
                {
                    var pos = pair.Key;
                    if (Math.Abs(pos.X - farmer.TilePoint.X) > radius
                        || Math.Abs(pos.Y - farmer.TilePoint.Y) > radius)
                        continue;

                    if (dirt.crop.harvest((int)pos.X, (int)pos.Y, dirt))
                    {
                        dirt.destroyCrop(false);
                        count++;
                    }
                }
            }

            tcs.SetResult(new { ok = true, harvested = count });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleSell(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var p = ReadJson(ctx);
        var name = GetParamOr(p, "name", "");
        var sellAll = GetParamOr(p, "all", false);

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;
            var loc = farmer.currentLocation;

            var bin = loc is Farm farm
                ? farm.getShippingBin(farmer)
                : null;

            if (bin == null)
            {
                tcs.SetResult(new { ok = false, error = "No shipping bin found (must be on Farm)" });
                return;
            }

            var sold = new List<object>();
            var keepCategories = new HashSet<int> { -99, -98, -97, -96 }; // tools, rings, boots, weapons

            for (int i = farmer.Items.Count - 1; i >= 0; i--)
            {
                var item = farmer.Items[i];
                if (item == null) continue;
                if (item is Tool) continue;
                if (keepCategories.Contains(item.Category)) continue;

                if (!sellAll && !string.IsNullOrEmpty(name)
                    && !item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (sellAll && item.Name.Contains("Seeds", StringComparison.OrdinalIgnoreCase))
                    continue;

                var salePrice = item is StardewValley.Object obj ? obj.sellToStorePrice() * item.Stack : 0;
                sold.Add(new { item = item.Name, count = item.Stack, price = salePrice });

                bin.Add(item);
                farmer.Items[i] = null;
            }

            tcs.SetResult(new { ok = true, sold, totalItems = sold.Count });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleRefill()
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var wc = Game1.player.Items.OfType<WateringCan>().FirstOrDefault();
            if (wc == null)
            {
                tcs.SetResult(new { ok = false, error = "No watering can in inventory" });
                return;
            }
            wc.WaterLeft = wc.waterCanMax;
            tcs.SetResult(new { ok = true, water = wc.WaterLeft, max = wc.waterCanMax });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleHeal()
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var f = Game1.player;
            f.health = f.maxHealth;
            f.Stamina = f.MaxStamina;
            tcs.SetResult(new { ok = true, health = f.health, stamina = f.Stamina });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleRipen(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var qs = ctx.Request.QueryString;
        int radius = 30;
        if (int.TryParse(qs["radius"], out var r) && r > 0 && r <= 50)
            radius = r;

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;
            var loc = farmer.currentLocation;
            int count = 0;

            foreach (var pair in loc.terrainFeatures.Pairs)
            {
                if (pair.Value is HoeDirt dirt && dirt.crop != null && !dirt.readyForHarvest())
                {
                    var pos = pair.Key;
                    if (Math.Abs(pos.X - farmer.TilePoint.X) <= radius
                        && Math.Abs(pos.Y - farmer.TilePoint.Y) <= radius)
                    {
                        dirt.crop.growCompletely();
                        count++;
                    }
                }
            }

            tcs.SetResult(new { ok = true, ripened = count });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleGive(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var itemId = GetParam<string>(p, "id");
        var count = GetParamOr(p, "count", 1);

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            // Use multiplayer-safe add: farmhand sends to host, host adds directly
            AddItemSynced(itemId, count);
            var item = ItemRegistry.Create(itemId, 1); // just for the name in response
            tcs.SetResult(new { ok = true, given = item.Name, count, id = itemId,
                synced = !Context.IsMainPlayer ? "via_host" : "direct" });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// POST /toss  { "name": "Wood", "count": 5 }
    /// Drops items from inventory onto the ground in front of the farmer,
    /// so another player can pick them up (multiplayer item hand-off).
    /// </summary>
    private object HandleToss(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var name = GetParam<string>(p, "name");
        var count = GetParamOr(p, "count", 1);

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;
            var item = farmer.Items.FirstOrDefault(i =>
                i != null && i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (item == null)
            {
                tcs.SetResult(new { ok = false, error = $"Item '{name}' not found in inventory" });
                return;
            }

            var tossCount = Math.Min(count, item.Stack);
            var dropped = item.getOne();
            dropped.Stack = tossCount;

            if (item.Stack <= tossCount)
                farmer.removeItemFromInventory(item);
            else
                item.Stack -= tossCount;

            Game1.createItemDebris(dropped, farmer.getStandingPosition(), farmer.FacingDirection, farmer.currentLocation);
            tcs.SetResult(new { ok = true, tossed = dropped.Name, count = tossCount });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleMoney(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var amount = GetParam<int>(p, "amount");

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            Game1.player.Money += amount;
            tcs.SetResult(new { ok = true, added = amount, total = Game1.player.Money });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandlePause()
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");
        _frozenTime = Game1.timeOfDay;
        _timeFrozen = true;
        return new { ok = true, action = "paused", frozenAt = _frozenTime };
    }

    private object HandleResume()
    {
        _timeFrozen = false;
        return new { ok = true, action = "resumed" };
    }

    private object HandleFishbot(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var action = GetParamOr(p, "action", "toggle"); // on, off, toggle, status

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                // Find Fishbot mod via SMAPI mod registry
                object? fishbotMod = null;
                System.Reflection.FieldInfo? autoField = null;

                var modInfo = this.Helper.ModRegistry.Get("AdroSlice.Fishbot");
                if (modInfo != null)
                {
                    var modInfoType = modInfo.GetType();
                    var modProp = modInfoType.GetProperty("Mod",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);
                    fishbotMod = modProp?.GetValue(modInfo);
                    if (fishbotMod == null)
                    {
                        var modField = modInfoType.GetField("Mod",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                            System.Reflection.BindingFlags.Instance);
                        fishbotMod = modField?.GetValue(modInfo);
                    }
                }

                if (fishbotMod == null)
                {
                    tcs.SetResult(new { ok = false, error = "Fishbot mod not found" });
                    return;
                }

                // Find AutomationEnabled field/property
                var fbType = fishbotMod.GetType();
                autoField = fbType.GetField("AutomationEnabled",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static);

                var autoProp = fbType.GetProperty("AutomationEnabled",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static);

                var bindingAll = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;

                if (autoField != null || autoProp != null)
                {
                    bool current = autoField != null
                        ? (bool)autoField.GetValue(fishbotMod)!
                        : (bool)autoProp!.GetValue(fishbotMod)!;
                    bool target = action == "toggle" ? !current : action == "on";

                    if (action != "status")
                    {
                        if (autoField != null) autoField.SetValue(fishbotMod, target);
                        else autoProp!.SetValue(fishbotMod, target);

                        if (target)
                        {
                            var startMethod = fbType.GetMethod("StartCasting", bindingAll);
                            startMethod?.Invoke(fishbotMod, null);
                        }
                        else
                        {
                            var resetMethod = fbType.GetMethod("reset", bindingAll)
                                ?? fbType.GetMethod("Reset", bindingAll);
                            resetMethod?.Invoke(fishbotMod, null);
                        }
                    }
                    tcs.SetResult(new { ok = true, enabled = action == "status" ? current : target });
                }
                else
                {
                    // List all fields for debugging
                    var fields = fbType.GetFields(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static);
                    var names = string.Join(", ", fields.Select(f => f.Name));
                    tcs.SetResult(new { ok = false, error = $"AutomationEnabled not found. Fields: {names}" });
                }
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleMinigameState()
    {
        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                tcs.SetResult(_prairieKingBot.BuildState());
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleMinigameBot(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var action = GetParamOr(p, "action", "status").ToLowerInvariant();

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                switch (action)
                {
                    case "start":
                        _prairieKingBot.Start();
                        break;
                    case "stop":
                        _prairieKingBot.Stop();
                        break;
                    case "status":
                        break;
                    default:
                        tcs.SetResult(new { ok = false, error = "action must be start, stop, or status" });
                        return;
                }

                tcs.SetResult(new
                {
                    ok = true,
                    active = _prairieKingBot.IsActive,
                    inPrairieKing = PrairieKingBot.IsPrairieKing(Game1.currentMinigame),
                    currentMinigame = Game1.currentMinigame?.GetType().FullName,
                    lastMove = new { x = _prairieKingBot.LastMove.X, y = _prairieKingBot.LastMove.Y },
                    lastError = _prairieKingBot.LastError
                });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleMenu()
    {
        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                var menu = Game1.activeClickableMenu;
                if (menu == null)
                {
                    object? eventInfo = null;
                    if (Game1.currentLocation?.currentEvent != null)
                    {
                        var ev = Game1.currentLocation.currentEvent;
                        eventInfo = new { id = ev.id, skippable = ev.skippable };
                    }
                    tcs.SetResult(new { ok = true, open = false, activeEvent = eventInfo });
                    return;
                }

                var menuType = menu.GetType().Name;
                string? dialogue = null;
                List<object>? responses = null;
                List<object>? shopItems = null;
                List<object>? buttons = null;

                if (menu is DialogueBox db)
                {
                    try { dialogue = db.getCurrentString(); } catch { }

                    var responseField = typeof(DialogueBox).GetField("responseCC",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);
                    var responseCCs = responseField?.GetValue(db) as List<ClickableComponent>;

                    var responsesField = typeof(DialogueBox).GetField("responses",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);
                    var responseList = responsesField?.GetValue(db) as List<Response>;

                    if (responseList != null && responseList.Count > 0)
                    {
                        responses = new List<object>();
                        for (int i = 0; i < responseList.Count; i++)
                        {
                            var r = responseList[i];
                            responses.Add(new
                            {
                                index = i,
                                key = r.responseKey,
                                text = r.responseText,
                                bounds = responseCCs != null && i < responseCCs.Count
                                    ? new { x = responseCCs[i].bounds.X, y = responseCCs[i].bounds.Y,
                                            w = responseCCs[i].bounds.Width, h = responseCCs[i].bounds.Height }
                                    : null
                            });
                        }
                    }
                }
                else if (menu is ShopMenu shop)
                {
                    shopItems = new List<object>();
                    var forSale = shop.forSale;
                    var itemPriceAndStock = shop.itemPriceAndStock;
                    foreach (var item in forSale)
                    {
                        int price = 0;
                        int stock = -1;
                        if (itemPriceAndStock.TryGetValue(item, out var info))
                        {
                            price = info.Price;
                            stock = info.Stock;
                        }
                        shopItems.Add(new
                        {
                            name = item.DisplayName,
                            id = item.QualifiedItemId,
                            price,
                            stock
                        });
                    }
                }

                // Collect named buttons via reflection
                buttons = new List<object>();
                foreach (var fieldName in new[] { "okButton", "cancelButton", "backButton",
                    "forwardButton", "upperRightCloseButton", "trashCan" })
                {
                    var field = menu.GetType().GetField(fieldName,
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);
                    var comp = field?.GetValue(menu) as ClickableComponent;
                    if (comp != null && comp.visible)
                    {
                        buttons.Add(new
                        {
                            name = fieldName,
                            x = comp.bounds.Center.X,
                            y = comp.bounds.Center.Y
                        });
                    }
                }

                tcs.SetResult(new
                {
                    ok = true,
                    open = true,
                    type = menuType,
                    dialogue,
                    responses,
                    shopItems,
                    buttons = buttons.Count > 0 ? buttons : null
                });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleMenuClick(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var option = GetParamOr(p, "option", -1);
        var button = GetParamOr(p, "button", "");
        var clickX = GetParamOr(p, "x", -1);
        var clickY = GetParamOr(p, "y", -1);

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                var menu = Game1.activeClickableMenu;
                if (menu == null)
                {
                    tcs.SetResult(new { ok = false, error = "No menu open" });
                    return;
                }

                if (option >= 0 && menu is DialogueBox db)
                {
                    var responseField = typeof(DialogueBox).GetField("responseCC",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);
                    var responseCCs = responseField?.GetValue(db) as List<ClickableComponent>;

                    if (responseCCs != null && option < responseCCs.Count)
                    {
                        var rc = responseCCs[option];
                        db.receiveLeftClick(rc.bounds.Center.X, rc.bounds.Center.Y);
                        tcs.SetResult(new { ok = true, clicked = "response", option });
                    }
                    else
                    {
                        tcs.SetResult(new { ok = false, error = $"Response index {option} out of range" });
                    }
                    return;
                }

                if (button != "")
                {
                    var field = menu.GetType().GetField(button == "ok" ? "okButton" :
                                                        button == "cancel" ? "cancelButton" :
                                                        button == "back" ? "backButton" :
                                                        button == "forward" ? "forwardButton" :
                                                        button == "close" ? "upperRightCloseButton" :
                                                        button,
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);
                    var comp = field?.GetValue(menu) as ClickableComponent;
                    if (comp != null)
                    {
                        menu.receiveLeftClick(comp.bounds.Center.X, comp.bounds.Center.Y);
                        tcs.SetResult(new { ok = true, clicked = "button", button });
                    }
                    else
                    {
                        tcs.SetResult(new { ok = false, error = $"Button '{button}' not found" });
                    }
                    return;
                }

                if (clickX >= 0 && clickY >= 0)
                {
                    menu.receiveLeftClick(clickX, clickY);
                    tcs.SetResult(new { ok = true, clicked = "position", x = clickX, y = clickY });
                    return;
                }

                menu.receiveLeftClick(
                    menu.xPositionOnScreen + menu.width / 2,
                    menu.yPositionOnScreen + menu.height / 2);
                tcs.SetResult(new { ok = true, clicked = "center" });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleCraft(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var p = ReadJson(ctx);
        var name = GetParam<string>(p, "name");
        var count = GetParamOr(p, "count", 1);

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                var farmer = Game1.player;
                var recipes = CraftingRecipe.craftingRecipes;
                if (!recipes.ContainsKey(name))
                {
                    var known = farmer.craftingRecipes.Keys.ToList();
                    tcs.SetResult(new { ok = false, error = $"Recipe '{name}' not found",
                        knownRecipes = known });
                    return;
                }

                if (!farmer.craftingRecipes.ContainsKey(name))
                {
                    tcs.SetResult(new { ok = false, error = $"Player hasn't learned recipe '{name}'" });
                    return;
                }

                var recipe = new CraftingRecipe(name, false);
                int crafted = 0;
                var missing = new Dictionary<string, int>();

                for (int i = 0; i < count; i++)
                {
                    if (!recipe.doesFarmerHaveIngredientsInInventory())
                    {
                        foreach (var kvp in recipe.recipeList)
                        {
                            var ingredientId = kvp.Key;
                            var needed = kvp.Value;
                            var have = 0;
                            foreach (var item in farmer.Items)
                            {
                                if (item != null && (item.ParentSheetIndex.ToString() == ingredientId
                                    || item.Category.ToString() == ingredientId))
                                    have += item.Stack;
                            }
                            if (have < needed)
                            {
                                var ingredientName = ingredientId;
                                try { ingredientName = new StardewValley.Object(ingredientId, 1).DisplayName; } catch { }
                                missing[ingredientName] = needed - have;
                            }
                        }
                        break;
                    }
                    recipe.consumeIngredients(null);
                    var product = recipe.createItem();
                    if (!farmer.addItemToInventoryBool(product))
                    {
                        Game1.createItemDebris(product, farmer.getStandingPosition(), farmer.FacingDirection);
                        tcs.SetResult(new { ok = true, crafted = crafted + 1,
                            warning = "Inventory full, item dropped" });
                        return;
                    }
                    crafted++;
                }

                if (crafted == 0)
                    tcs.SetResult(new { ok = false, error = "Missing materials", missing });
                else if (crafted < count)
                    tcs.SetResult(new { ok = true, crafted, requested = count,
                        warning = "Ran out of materials", missing });
                else
                    tcs.SetResult(new { ok = true, crafted });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleMachines()
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                var loc = Game1.player.currentLocation;
                var machines = new List<object>();

                foreach (var pair in loc.objects.Pairs)
                {
                    var obj = pair.Value;
                    if (!obj.bigCraftable.Value) continue;

                    string status;
                    if (obj.readyForHarvest.Value)
                        status = "ready";
                    else if (obj.heldObject.Value != null || obj.MinutesUntilReady > 0)
                        status = "processing";
                    else
                        status = "empty";

                    var entry = new Dictionary<string, object?>
                    {
                        ["name"] = obj.Name,
                        ["x"] = (int)pair.Key.X,
                        ["y"] = (int)pair.Key.Y,
                        ["status"] = status,
                        ["minutesLeft"] = obj.MinutesUntilReady
                    };

                    if (obj.heldObject.Value != null)
                    {
                        entry["heldItem"] = obj.heldObject.Value.Name;
                        entry["heldItemId"] = obj.heldObject.Value.QualifiedItemId;
                    }

                    machines.Add(entry);
                }

                tcs.SetResult(new
                {
                    ok = true,
                    location = loc.Name,
                    count = machines.Count,
                    machines
                });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleAnimals()
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                var loc = Game1.player.currentLocation;
                var animals = new List<object>();

                IEnumerable<FarmAnimal>? animalList = null;
                if (loc is Farm farm)
                    animalList = farm.animals.Values;
                else if (loc is AnimalHouse ah)
                    animalList = ah.animals.Values;

                if (animalList != null)
                {
                    foreach (var a in animalList)
                    {
                        animals.Add(new
                        {
                            name = a.Name,
                            type = a.type.Value,
                            x = a.TilePoint.X,
                            y = a.TilePoint.Y,
                            wasPetToday = a.wasPet.Value,
                            friendship = a.friendshipTowardFarmer.Value,
                            happiness = a.happiness.Value,
                            fullness = a.fullness.Value,
                            age = a.age.Value,
                            home = a.home?.indoors.Value?.Name,
                            product = a.currentProduce.Value,
                            productReady = a.currentProduce.Value != null && a.currentProduce.Value != "-1"
                        });
                    }
                }

                tcs.SetResult(new
                {
                    ok = true,
                    location = loc.Name,
                    count = animals.Count,
                    animals
                });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleScan()
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                var loc = Game1.currentLocation;
                var actions = new List<object>();
                for (int x = 0; x < loc.Map.Layers[0].LayerWidth; x++)
                {
                    for (int y = 0; y < loc.Map.Layers[0].LayerHeight; y++)
                    {
                        string? action = loc.doesTileHaveProperty(x, y, "Action", "Buildings");
                        if (action != null)
                            actions.Add(new { x, y, action });
                    }
                }
                tcs.SetResult(new { ok = true, location = loc.Name, count = actions.Count, actions });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleFestival()
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                var evt = Game1.CurrentEvent;
                if (evt == null)
                {
                    tcs.SetResult(new { ok = false, error = "No active event" });
                    return;
                }

                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                var actors = new List<object>();

                // Get event actors
                var actorsField = evt.GetType().GetField("actors", flags);
                if (actorsField?.GetValue(evt) is IEnumerable<NPC> npcList)
                {
                    foreach (var npc in npcList)
                    {
                        actors.Add(new
                        {
                            name = npc.Name,
                            displayName = npc.displayName,
                            x = npc.TilePoint.X,
                            y = npc.TilePoint.Y
                        });
                    }
                }

                // Check festival name
                string festivalName = "";
                var nameField = evt.GetType().GetField("FestivalName", flags) ?? evt.GetType().GetField("festivalName", flags);
                if (nameField != null)
                    festivalName = nameField.GetValue(evt) as string ?? "";
                var nameProp = evt.GetType().GetProperty("FestivalName", flags);
                if (string.IsNullOrEmpty(festivalName) && nameProp != null)
                    festivalName = nameProp.GetValue(evt) as string ?? "";

                // Check isFestival
                bool isFestival = false;
                var isFestMethod = typeof(Game1).GetMethod("isFestival", flags, null, Type.EmptyTypes, null);
                if (isFestMethod != null)
                    isFestival = (bool?)isFestMethod.Invoke(null, null) ?? false;

                tcs.SetResult(new
                {
                    ok = true,
                    isFestival,
                    festivalName,
                    location = Game1.currentLocation?.Name,
                    actorCount = actors.Count,
                    actors
                });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleFestivalInteract(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var body = ReadJson(ctx);
        string targetName = body.ContainsKey("name") ? body["name"].ToString() : "";

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                var evt = Game1.CurrentEvent;
                if (evt == null)
                {
                    tcs.SetResult(new { ok = false, error = "No active event" });
                    return;
                }

                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                var actorsField = evt.GetType().GetField("actors", flags);
                if (actorsField?.GetValue(evt) is not IEnumerable<NPC> npcList)
                {
                    tcs.SetResult(new { ok = false, error = "No actors found" });
                    return;
                }

                NPC? target = null;
                foreach (var npc in npcList)
                {
                    if (string.IsNullOrEmpty(targetName) || npc.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                    {
                        target = npc;
                        break;
                    }
                }

                if (target == null)
                {
                    tcs.SetResult(new { ok = false, error = $"Actor '{targetName}' not found" });
                    return;
                }

                // Move player next to NPC and face them
                var farmer = Game1.player;
                farmer.Position = new Vector2(target.TilePoint.X, target.TilePoint.Y + 1) * Game1.tileSize;
                farmer.faceDirection(0); // face up toward NPC

                // Try to trigger NPC action via checkAction
                bool triggered = Game1.currentLocation.checkAction(
                    new xTile.Dimensions.Location(target.TilePoint.X, target.TilePoint.Y),
                    Game1.viewport, farmer);

                if (!triggered)
                {
                    // Fallback: try direct NPC click
                    target.checkAction(farmer, Game1.currentLocation);
                    triggered = true;
                }

                tcs.SetResult(new
                {
                    ok = true,
                    target = target.Name,
                    targetTile = new { x = target.TilePoint.X, y = target.TilePoint.Y },
                    playerTile = new { x = farmer.TilePoint.X, y = farmer.TilePoint.Y },
                    triggered
                });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleFestivalAnswer(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var body = ReadJson(ctx);
        int answer = body.ContainsKey("answer") ? Convert.ToInt32(body["answer"]) : 0;
        string key = body.ContainsKey("key") ? body["key"].ToString() : "";

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                var evt = Game1.CurrentEvent;
                if (evt == null)
                {
                    tcs.SetResult(new { ok = false, error = "No active event" });
                    return;
                }

                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

                // Try answerDialogueQuestion
                var answerMethod = evt.GetType().GetMethod("answerDialogueQuestion", flags);
                if (answerMethod != null)
                {
                    var npc = Game1.currentLocation.isCharacterAtTile(Game1.player.GetGrabTile());
                    answerMethod.Invoke(evt, new object?[] { npc, answer.ToString() });
                    tcs.SetResult(new { ok = true, method = "answerDialogueQuestion", answer });
                    return;
                }

                // Fallback: try answerDialogue on the event
                var methods = evt.GetType().GetMethods(flags);
                foreach (var m in methods)
                {
                    if (m.Name.Contains("answer", StringComparison.OrdinalIgnoreCase) ||
                        m.Name.Contains("Answer", StringComparison.OrdinalIgnoreCase))
                    {
                        var parms = m.GetParameters();
                        if (parms.Length >= 1)
                        {
                            try
                            {
                                if (parms[0].ParameterType == typeof(int))
                                    m.Invoke(evt, new object[] { answer });
                                else if (parms[0].ParameterType == typeof(string))
                                    m.Invoke(evt, new object[] { answer.ToString() });
                                tcs.SetResult(new { ok = true, method = m.Name, answer });
                                return;
                            }
                            catch { continue; }
                        }
                    }
                }

                // Fallback: use Game1.currentLocation.answerDialogueAction
                var locMethod = Game1.currentLocation.GetType().GetMethod("answerDialogueAction", flags);
                if (locMethod != null)
                {
                    string actionKey = string.IsNullOrEmpty(key) ? $"festival_{answer}" : key;
                    locMethod.Invoke(Game1.currentLocation, new object[] { actionKey, Array.Empty<string>() });
                    tcs.SetResult(new { ok = true, method = "location.answerDialogueAction", key = actionKey });
                    return;
                }

                tcs.SetResult(new { ok = false, error = "No answer method found" });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private Vector2 GetFacingTile(Farmer farmer)
    {
        int x = farmer.TilePoint.X;
        int y = farmer.TilePoint.Y;
        return farmer.FacingDirection switch
        {
            0 => new Vector2(x, y - 1),
            1 => new Vector2(x + 1, y),
            2 => new Vector2(x, y + 1),
            3 => new Vector2(x - 1, y),
            _ => new Vector2(x, y)
        };
    }

    // --- Helpers ---

    private void EnqueueMainThread(Action action)
    {
        lock (_queueLock)
        {
            _mainThreadQueue.Enqueue(action);
        }
    }

    /// <summary>
    /// Simple BFS pathfinding on the game map.
    /// </summary>
    private Queue<Point>? FindPath(GameLocation location, Point start, Point end)
    {
        if (start == end) return new Queue<Point>();

        var maxSteps = 500;
        var visited = new HashSet<Point> { start };
        var queue = new Queue<(Point pos, List<Point> path)>();
        queue.Enqueue((start, new List<Point>()));

        int[] dx = { 0, 0, -1, 1 };
        int[] dy = { -1, 1, 0, 0 };

        while (queue.Count > 0 && maxSteps-- > 0)
        {
            var (pos, path) = queue.Dequeue();

            for (int i = 0; i < 4; i++)
            {
                var next = new Point(pos.X + dx[i], pos.Y + dy[i]);

                if (visited.Contains(next)) continue;
                if (!IsTilePassable(location, next)) continue;

                visited.Add(next);
                var newPath = new List<Point>(path) { next };

                if (next == end)
                    return new Queue<Point>(newPath);

                queue.Enqueue((next, newPath));
            }
        }

        // If no path found, return null (caller will fallback to direct walk)
        return null;
    }

    private bool IsTilePassable(GameLocation location, Point tile)
    {
        // Check map bounds
        if (tile.X < 0 || tile.Y < 0) return false;
        var mapWidth = location.Map.DisplayWidth / 64;
        var mapHeight = location.Map.DisplayHeight / 64;
        if (tile.X >= mapWidth || tile.Y >= mapHeight) return false;

        // Use the game's built-in passability check
        var tileVec = new Vector2(tile.X, tile.Y);
        return location.isTilePassable(tileVec);
    }
}
