using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Timers;

using Steamworks;
using Newtonsoft.Json.Linq;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API.Modules.Admin;

namespace KitsuneSteamRestrict;

public class KitsuneSteamRestrictConfig : BasePluginConfig
{
    [JsonPropertyName("SteamWebAPI")]
    public string SteamWebAPI { get; set; } = "";

    [JsonPropertyName("MinimumCS2LevelPrime")]
    public int MinimumCS2LevelPrime { get; set; } = -1;

    [JsonPropertyName("MinimumCS2LevelNonPrime")]
    public int MinimumCS2LevelNonPrime { get; set; } = -1;

    [JsonPropertyName("MinimumHourPrime")]
    public int MinimumHourPrime { get; set; } = -1;

    [JsonPropertyName("MinimumHourNonPrime")]
    public int MinimumHourNonPrime { get; set; } = -1;

    [JsonPropertyName("MinimumLevelPrime")]
    public int MinimumLevelPrime { get; set; } = -1;

    [JsonPropertyName("MinimumLevelNonPrime")]
    public int MinimumLevelNonPrime { get; set; } = -1;

    [JsonPropertyName("MinimumSteamAccountAgeInDays")]
    public int MinimumSteamAccountAgeInDays { get; set; } = -1;

    [JsonPropertyName("BlockPrivateProfile")]
    public bool BlockPrivateProfile { get; set; } = false;

    [JsonPropertyName("BlockTradeBanned")]
    public bool BlockTradeBanned { get; set; } = false;

    [JsonPropertyName("BlockGameBanned")]
    public bool BlockGameBanned { get; set; } = false;
}

[MinimumApiVersion(154)]
public class SteamRestrictPlugin : BasePlugin, IPluginConfig<KitsuneSteamRestrictConfig>
{
    public override string ModuleName => "Steam Restrict";
    public override string ModuleVersion => "1.1.3";
    public override string ModuleAuthor => "K4ryuu, Cruze @ KitsuneLab";
    public override string ModuleDescription => "Restrict certain players from connecting to your server.";

    private bool g_bSteamAPIActivated = false;

    private CounterStrikeSharp.API.Modules.Timers.Timer?[] g_hAuthorize = new CounterStrikeSharp.API.Modules.Timers.Timer?[65];

    public class SteamUserInfo
    {
        public DateTime SteamAccountAge { get; set; }
        public int SteamLevel { get; set; }
        public int CS2Level { get; set; }
        public int CS2Playtime { get; set; }
        public bool IsPrivate { get; set; }
        public bool HasPrime { get; set; }
        public bool IsTradeBanned { get; set; }
        public bool IsGameBanned { get; set; }
    }

    public KitsuneSteamRestrictConfig Config { get; set; } = new();

    public void OnConfigParsed(KitsuneSteamRestrictConfig config)
    {
        Config = config;

        if (string.IsNullOrEmpty(config.SteamWebAPI))
        {
            Logger.LogError("[K4ryuuSteamRestrict] This plugin won't work because Web API is empty.");
        }
    }

    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnGameServerSteamAPIActivated>(OnGameServerSteamAPIActivated);
        RegisterListener<Listeners.OnClientConnect>(OnClientConnect);
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);

        if (hotReload)
        {
            g_bSteamAPIActivated = true;

            foreach (var player in Utilities.GetPlayers().Where(m => m.Connected == PlayerConnectedState.PlayerConnected && !m.IsHLTV && !m.IsBot && m.SteamID.ToString().Length == 17))
            {
                OnPlayerConnectFull(player);
            }
        }
    }

    public override void Unload(bool hotReload)
    {
        base.Unload(hotReload);
    }

    private void OnClientConnect(int slot, string name, string ipAddress)
    {
        g_hAuthorize[slot]?.Kill();
    }

    private void OnClientDisconnect(int slot)
    {
        g_hAuthorize[slot]?.Kill();
    }

    private void OnGameServerSteamAPIActivated()
    {
        g_bSteamAPIActivated = true;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        CCSPlayerController player = @event.Userid;
        if (player == null) return HookResult.Continue;

        OnPlayerConnectFull(player);

        return HookResult.Continue;
    }

    private void OnPlayerConnectFull(CCSPlayerController player)
    {
        if (string.IsNullOrEmpty(Config.SteamWebAPI))
            return;

        if (player.IsBot || player.IsHLTV)
            return;

        if (player.AuthorizedSteamID == null)
        {
            Logger.LogInformation($"{player.PlayerName ?? "Unknown"} not validated. Waiting for him to get validated.");

            g_hAuthorize[player.Slot] = AddTimer(1.0f, () =>
            {
                if (player.AuthorizedSteamID != null)
                {
                    g_hAuthorize[player.Slot]?.Kill();
                    Logger.LogInformation($"{player.PlayerName ?? "Unknown"} is now validated.");
                    OnPlayerConnectFull(player);
                    return;
                }
            }, TimerFlags.REPEAT);
            return;
        }

        if (!g_bSteamAPIActivated)
            return;

        nint handle = player.Handle;
        ulong steamID = player.SteamID;
        ulong authorizedSteamID = player.AuthorizedSteamID.SteamId64;

        _ = CheckUserViolations(handle, steamID, authorizedSteamID);
    }

    private async Task CheckUserViolations(nint handle, ulong steamID, ulong authorizedSteamID)
    {
        CSteamID cSteamID = new CSteamID(steamID);

        SteamUserInfo userInfo = new SteamUserInfo
        {
            // Check for both CSGO's prime and CS2's prime.
            HasPrime = SteamGameServer.UserHasLicenseForApp(cSteamID, (AppId_t)624820) == EUserHasLicenseForAppResult.k_EUserHasLicenseResultHasLicense
                    || SteamGameServer.UserHasLicenseForApp(cSteamID, (AppId_t)54029) == EUserHasLicenseForAppResult.k_EUserHasLicenseResultHasLicense,
            // Fetch CS2 Level
            CS2Level = new CCSPlayerController_InventoryServices(handle).PersonaDataPublicLevel
        };

        SteamUserInfo updatedUserInfo = await FetchSteamUserInfo(userInfo, authorizedSteamID);

        Server.NextWorldUpdate(() =>
        {
            CCSPlayerController? player = Utilities.GetPlayerFromSteamId(steamID);

            if (player?.IsValid == true)
            {
                Logger.LogInformation($"{player.PlayerName ?? "Unknown"} info:");
                Logger.LogInformation($"CS2Playtime: {updatedUserInfo.CS2Playtime}");
                Logger.LogInformation($"CS2Level: {updatedUserInfo.CS2Level}");
                Logger.LogInformation($"SteamLevel: {updatedUserInfo.SteamLevel}");
                if ((DateTime.Now - updatedUserInfo.SteamAccountAge).TotalSeconds > 30)
                    Logger.LogInformation($"Steam Account Creation Date: {userInfo.SteamAccountAge:dd-MM-yyyy}");
                else
                    Logger.LogInformation($"Steam Account Creation Date: N/A");
                Logger.LogInformation($"HasPrime: {updatedUserInfo.HasPrime}");
                Logger.LogInformation($"HasPrivateProfile: {updatedUserInfo.IsPrivate}");
                Logger.LogInformation($"IsTradeBanned: {updatedUserInfo.IsTradeBanned}");
                Logger.LogInformation($"IsGameBanned: {updatedUserInfo.IsGameBanned}");

                var x = player.Handle;

                if (IsRestrictionViolated(player, updatedUserInfo))
                {
                    Server.ExecuteCommand($"kickid {player.UserId} \"You have been kicked for not meeting the minimum requirements.\"");
                }
            }
        });
    }

    private async Task<SteamUserInfo> FetchSteamUserInfo(SteamUserInfo userInfo, ulong authorizedSteamID)
    {
        using (HttpClient httpClient = new HttpClient())
        {
            string steamId = authorizedSteamID.ToString();
            string steamWebAPIKey = Config.SteamWebAPI;

            // Fetch CS2 Playtime
            string gamesUrl = $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?key={steamWebAPIKey}&steamid={steamId}&format=json";
            HttpResponseMessage gamesResponse = await httpClient.GetAsync(gamesUrl);

            if (gamesResponse.IsSuccessStatusCode)
            {
                string gamesJson = await gamesResponse.Content.ReadAsStringAsync();
                userInfo.CS2Playtime = ParseCS2Playtime(gamesJson) / 60;
            }
            else
            {
                userInfo.CS2Playtime = 0;
            }

            // Fetch Steam Level
            string levelUrl = $"http://api.steampowered.com/IPlayerService/GetSteamLevel/v1/?key={steamWebAPIKey}&steamid={steamId}";
            HttpResponseMessage levelResponse = await httpClient.GetAsync(levelUrl);

            if (levelResponse.IsSuccessStatusCode)
            {
                string levelJson = await levelResponse.Content.ReadAsStringAsync();
                userInfo.SteamLevel = ParseSteamLevel(levelJson);
            }
            else
            {
                userInfo.SteamLevel = 0;
            }

            // Fetch Profile Privacy
            string userUrl = $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={steamWebAPIKey}&steamids={steamId}";
            HttpResponseMessage userResponse = await httpClient.GetAsync(userUrl);

            if (userResponse.IsSuccessStatusCode)
            {
                string userJson = await userResponse.Content.ReadAsStringAsync();
                ParseSteamUserInfo(userJson, userInfo);
            }
            else
            {
                userInfo.IsPrivate = false;
            }

            // Fetch TradeBan Status
            string tradeBanUrl = $"https://api.steampowered.com/ISteamUser/GetPlayerBans/v1/?key={steamWebAPIKey}&steamids={steamId}";
            HttpResponseMessage tradeBanResponse = await httpClient.GetAsync(tradeBanUrl);

            if (tradeBanResponse.IsSuccessStatusCode)
            {
                string tradeBanJson = await tradeBanResponse.Content.ReadAsStringAsync();
                ParseTradeBanStatus(tradeBanJson, userInfo);
            }
            else
            {
                userInfo.IsTradeBanned = false;
            }

            // Fetch GameBan Status
            string gameBanUrl = $"https://api.steampowered.com/ISteamUser/GetUserGameBan/v1/?key={steamWebAPIKey}&steamids={steamId}";
            HttpResponseMessage gameBanResponse = await httpClient.GetAsync(gameBanUrl);

            if (gameBanResponse.IsSuccessStatusCode)
            {
                string gameBanJson = await gameBanResponse.Content.ReadAsStringAsync();
                ParseGameBanStatus(gameBanJson, userInfo);
            }
            else
            {
                userInfo.IsGameBanned = false;
            }
        }

        return userInfo;
    }

    private bool IsRestrictionViolated(CCSPlayerController player, SteamUserInfo userInfo)
    {
        bool isViolated = false;

        if (userInfo.HasPrime)
        {
            if ((Config.MinimumHourPrime != -1 && userInfo.CS2Playtime < Config.MinimumHourPrime)
                || (Config.MinimumLevelPrime != -1 && userInfo.SteamLevel < Config.MinimumLevelPrime)
                || (Config.MinimumCS2LevelPrime != -1 && userInfo.CS2Level < Config.MinimumCS2LevelPrime))
            {
                isViolated = true;
            }
        }
        else
        {
            if ((Config.MinimumHourNonPrime != -1 && userInfo.CS2Playtime < Config.MinimumHourNonPrime)
                || (Config.MinimumLevelNonPrime != -1 && userInfo.SteamLevel < Config.MinimumLevelNonPrime)
                || (Config.MinimumCS2LevelNonPrime != -1 && userInfo.CS2Level < Config.MinimumCS2LevelNonPrime))
            {
                isViolated = true;
            }
        }

        if (Config.MinimumSteamAccountAgeInDays != -1)
        {
            if ((DateTime.Now - userInfo.SteamAccountAge).TotalDays < Config.MinimumSteamAccountAgeInDays)
            {
                isViolated = true;
            }
        }

        if (Config.BlockPrivateProfile && userInfo.IsPrivate)
        {
            isViolated = true;
        }

        if (Config.BlockTradeBanned && userInfo.IsTradeBanned)
        {
            isViolated = true;
        }

        if (Config.BlockGameBanned && userInfo.IsGameBanned)
        {
            isViolated = true;
        }

        if (AdminManager.PlayerHasPermissions(player, "@css/bypasspremiumcheck") && isViolated)
        {
            isViolated = false;
        }

        return isViolated;
    }

    private int ParseCS2Playtime(string json)
    {
        int csPlaytime = 0;

        try
        {
            JObject data = JObject.Parse(json);
            JArray games = (data["response"]!["games"] as JArray)!;

            if (games != null)
            {
                foreach (var game in games)
                {
                    int appId = (int)game["appid"]!;
                    if (appId == 730)
                    {
                        csPlaytime = (int)game["playtime_forever"]!;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"[K4ryuuSteamRestrict] Error parsing CS2 playtime: {ex.Message}");
        }

        return csPlaytime;
    }

    private int ParseSteamLevel(string json)
    {
        int steamLevel = 0;

        try
        {
            JObject data = JObject.Parse(json);
            steamLevel = (int)data["response"]!["player_level"]!;
        }
        catch (Exception ex)
        {
            Logger.LogError($"[K4ryuuSteamRestrict] Error parsing Steam level: {ex.Message}");
        }

        return steamLevel;
    }

    private void ParseSteamUserInfo(string json, SteamUserInfo userInfo)
    {
        try
        {
            JObject data = JObject.Parse(json);
            JArray players = (data["response"]!["players"] as JArray)!;

            if (players != null && players.Count > 0)
            {
                var player = players[0];

                userInfo.IsPrivate = (int)player["communityvisibilitystate"]! != 3;
                if (player["timecreated"] != null && (int)player["timecreated"]! > 0)
                    userInfo.SteamAccountAge = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds((int)player["timecreated"]!);
                else
                    userInfo.SteamAccountAge = DateTime.Now;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"[K4ryuuSteamRestrict] Error parsing Steam user info: {ex.Message}");
        }
    }

    private void ParseTradeBanStatus(string json, SteamUserInfo userInfo)
    {
        try
        {
            JObject data = JObject.Parse(json);
            JArray playerBans = (data["players"] as JArray)!;

            if (playerBans != null && playerBans.Count > 0)
            {
                var playerBan = playerBans[0];
                userInfo.IsTradeBanned = (bool)playerBan["CommunityBanned"]!;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"[K4ryuuSteamRestrict] Error parsing trade ban status: {ex.Message}");
        }
    }

    private void ParseGameBanStatus(string json, SteamUserInfo userInfo)
    {
        try
        {
            JObject data = JObject.Parse(json);
            JArray userGameBans = (data["players"] as JArray)!;

            if (userGameBans != null && userGameBans.Count > 0)
            {
                var userGameBan = userGameBans[0];
                userInfo.IsGameBanned = (bool)userGameBan["IsGameBanned"]!;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"[K4ryuuSteamRestrict] Error parsing game ban status: {ex.Message}");
        }
    }
}
