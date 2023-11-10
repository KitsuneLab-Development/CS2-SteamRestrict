using System.Text.Json;

internal class CFG
{
	public static Config config = new();

	public void CheckConfig(string moduleDirectory)
	{
		string path = Path.Join(moduleDirectory, "config.json");

		if (!File.Exists(path))
		{
			CreateAndWriteFile(path);
		}

		using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
		using (StreamReader sr = new StreamReader(fs))
		{
			// Deserialize the JSON from the file and load the configuration.
			config = JsonSerializer.Deserialize<Config>(sr.ReadToEnd())!;
		}
	}

	private static void CreateAndWriteFile(string path)
	{

		using (FileStream fs = File.Create(path))
		{
			// File is created, and fs will automatically be disposed when the using block exits.
		}

		Console.WriteLine($"File created: {File.Exists(path)}");

		Config config = new Config
		{
			SteamWebAPI = "-",
		};

		// Serialize the config object to JSON and write it to the file.
		string jsonConfig = JsonSerializer.Serialize(config, new JsonSerializerOptions()
		{
			WriteIndented = true
		});
		File.WriteAllText(path, jsonConfig);
	}
}

internal class Config
{
	public string? SteamWebAPI { get; set; }
	public int MinimumHourNonPrime { get; set; }
	public int MinimumHourPrime { get; set; }
	public int MinimumLevelNonPrime { get; set; }
	public int MinimumLevelPrime { get; set; }
	public bool BlockPrivateProfile { get; set; }
	public bool BlockNonPrime { get; set; }
}