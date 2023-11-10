using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using Newtonsoft.Json.Linq;
using CounterStrikeSharp.API;

namespace K4ryuuSteamRestrict
{
	[MinimumApiVersion(16)]
	public class SteamRestrictPlugin : BasePlugin
	{
		public class SteamUserInfo
		{
			public int SteamLevel { get; set; }
			public int CSGOPlaytime { get; set; }
			public bool IsPrivate { get; set; }
			public bool HasPrime { get; set; }
			public bool IsTradeBanned { get; set; }
			public bool IsGameBanned { get; set; }
		}

		public override string ModuleName => "Steam Restrict";
		public override string ModuleVersion => "1.1.0";
		public override string ModuleAuthor => "K4ryuu";

		public override void Load(bool hotReload)
		{
			new CFG().CheckConfig(ModuleDirectory);
		}

		[GameEventHandler]
		public HookResult OnClientConnect(EventPlayerConnectFull @event, GameEventInfo info)
		{
			CCSPlayerController player = @event.Userid;

			if (player == null || !player.IsValid || player.IsBot || CFG.config.SteamWebAPI == "-")
				return HookResult.Continue;

			_ = FetchSteamUserInfo(player);


			return HookResult.Continue;
		}

		private async Task<SteamUserInfo> FetchSteamUserInfo(CCSPlayerController player)
		{
			SteamUserInfo userInfo = new SteamUserInfo();

			using (HttpClient httpClient = new HttpClient())
			{
				string steamId = player.SteamID.ToString();
				string steamWebAPIKey = CFG.config.SteamWebAPI!;

				string gamesUrl = $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?key={steamWebAPIKey}&steamid={steamId}&format=json";
				HttpResponseMessage gamesResponse = await httpClient.GetAsync(gamesUrl);

				if (gamesResponse.IsSuccessStatusCode)
				{
					string gamesJson = await gamesResponse.Content.ReadAsStringAsync();
					userInfo.CSGOPlaytime = ParseCS2Playtime(gamesJson) / 60;

					JArray games = (JObject.Parse(gamesJson)["response"]!["games"] as JArray)!;
					bool hasPrime = games.Any(game => (int)game["appid"]! == 54029);
					userInfo.HasPrime = hasPrime;
				}
				else
				{
					userInfo.HasPrime = false;
					userInfo.CSGOPlaytime = 0;
				}

				// Fetch Steam level
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

				// Fetch other user information
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

				if (IsRestrictionViolated(userInfo))
				{
					Server.ExecuteCommand($"kickid {player.UserId} \"You have been kicked for not meeting the minimum requirements.\"");
				}

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

		private bool IsRestrictionViolated(SteamUserInfo userInfo)
		{
			bool isViolated = false;

			if (userInfo.HasPrime)
			{
				if (CFG.config.MinimumHourPrime != -1 && userInfo.CSGOPlaytime < CFG.config.MinimumHourPrime
					|| CFG.config.MinimumLevelPrime != -1 && userInfo.SteamLevel < CFG.config.MinimumLevelPrime)
				{
					isViolated = true;
				}
			}
			else
			{
				if (CFG.config.MinimumHourNonPrime != -1 && userInfo.CSGOPlaytime < CFG.config.MinimumHourNonPrime
					|| CFG.config.MinimumLevelNonPrime != -1 && userInfo.SteamLevel < CFG.config.MinimumLevelNonPrime)
				{
					isViolated = true;
				}
			}

			if (CFG.config.BlockPrivateProfile && userInfo.IsPrivate)
			{
				isViolated = true;
			}

			if (CFG.config.BlockTradeBanned && userInfo.IsTradeBanned)
			{
				isViolated = true;
			}

			if (CFG.config.BlockGameBanned && userInfo.IsGameBanned)
			{
				isViolated = true;
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
				Console.WriteLine($"Error parsing CS:GO playtime: {ex.Message}");
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
				Console.WriteLine($"Error parsing Steam level: {ex.Message}");
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
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error parsing Steam user info: {ex.Message}");
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
				Console.WriteLine($"Error parsing trade ban status: {ex.Message}");
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
				Console.WriteLine($"Error parsing game ban status: {ex.Message}");
			}
		}
	}
}
