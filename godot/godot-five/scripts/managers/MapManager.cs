using System.Collections.Generic;
using System.Linq;
using Godot;

#region MapConfigClasses

public class LightInfo
{
	public bool active;
	public string objectName;
	public string objectPrefabName;
	public Vector3 position;
	public Vector3 rotation;
	public Color color;
	public float intensity;
}

public class ObjectInfo
{
	public bool active;
	public string objectName;
	public string objectPrefabName;
	public string dataFolder;
	public Vector3 position;
	public Vector3 rotation;

	public double comRadio;
}

public class InfoCollection
{
	public LightInfo[] lights;
	public ObjectInfo[] objects;
}

#endregion

public enum MapGenerationError
{
	OK,
	CompletedWithLightWarnings,
	FileError,
	ParsingError
}

public readonly struct MapInfo
{
	private readonly Vector2I mapSize;

	public Vector2I GetMapSize()
	{
		return mapSize;
	}

	//Store the map as an array of rows
	/*
	 * e.g a field like
	 *  AAAA
	 *  BBBB
	 *  CCCC
	 *
	 * will be stored as an array [3][4] where array[0] will be the first row (AAAA)
	 */
	private readonly char[,] mapSymbolMatrix;

	public char[,] GetMapSymbolMatrix()
	{
		return mapSymbolMatrix;
	}

	public MapInfo(in char[,] mapSymbolMatrix)
	{
		mapSize = new Vector2I(mapSymbolMatrix.GetLength(0), mapSymbolMatrix.GetLength(1));
		this.mapSymbolMatrix = mapSymbolMatrix;
	}
}

// Class in charge of parsing the map information from the map text file and scaling the ground
/*
 */

public partial class MapManager : Node
{
	[ExportGroup("Map Configuration")] [Export(PropertyHint.File)]
	private string MapFilePath = "";

	[Export(PropertyHint.File)] private string MapConfigFilePath = "";

	[Export(PropertyHint.File)] private string SunLight;
	[Export(PropertyHint.File)] private string MoonLight;

	[Export(PropertyHint.NodeType, "StaticBody3D")]
	private StaticBody3D GroundBody = null;

	[Export] private bool AdaptGroundSize = false;

	//Signals can only use Variant types, so we need to send it as an int and cast it on the signal handler
	//More info: https://docs.godotengine.org/en/stable/tutorials/scripting/c_sharp/c_sharp_variant.html#variant-compatible-types
	[Signal]
	public delegate void OnMapGeneratedEventHandler(int mapGenerationError);

	private string MapFileContent;
	private FileAccess MapFileAccess;

	private static MapInfo MapInfo;

	public static ref MapInfo GetMapInfo()
	{
		return ref MapInfo;
	}

	private static InfoCollection MapConfingInfo;

	public static ref InfoCollection GetMapConfigInfo()
	{
		return ref MapConfingInfo;
	}

	private static MapManager instance = null;

	public static MapManager GetInstance()
	{
		return instance;
	}

	public override void _Ready()
	{
		base._Ready();
		if (instance == null)
		{
			instance = this;
		}
		else
		{
			GD.PushWarning("[MapManager::OnReady] Found an existing instance of MapManager");
			QueueFree();
		}
	}

	public void StartMapGeneration()
	{
		//Extract map information from map.txt
		Error readingFileError = Utilities.Files.GetFileContent(MapFilePath, out string fileContent);

		if (readingFileError != Error.Ok)
		{
			EmitSignal(SignalName.OnMapGenerated, (int)MapGenerationError.FileError);
			return;
		}

		if (!ParseMapInfo(fileContent))
		{
			EmitSignal(SignalName.OnMapGenerated, (int)MapGenerationError.ParsingError);
			return;
		}

		ResizeGround();
		if (AdaptGroundSize)
		{
			AlignMapToOrigin();
		}

		MapConfingInfo = Utilities.Files.ParseJsonFile<InfoCollection>(
			MapConfigFilePath,
			out Error error
		);

		if (error != Error.Ok || MapConfingInfo == null)
		{
			GD.PushWarning("[MapManager::StartMapGeneration] Couldn't get Light info");
		}

		else
		{
			CorrectInfoCollectionTransforms();
			SetupLightning();
		}

		MapGenerationError resultSignal = MapConfingInfo == null
			? MapGenerationError.CompletedWithLightWarnings
			: MapGenerationError.OK;
		EmitSignal(SignalName.OnMapGenerated, (int)resultSignal);
	}

	#region File Reading and Parsing

	private bool TryGetMapFileContent()
	{
		if (!FileAccess.FileExists(MapFilePath))
		{
			GD.PushError($"[MapManager::StartMapGeneration] File {MapFilePath} doesn't exist");
			return false;
		}

		MapFileAccess = FileAccess.Open(MapFilePath, FileAccess.ModeFlags.Read);
		Error openingError = MapFileAccess.GetError();

		if (openingError == Error.Ok)
		{
			return true;
		}

		GD.PrintErr(
			$"[MapManager::TryGetMapFileContents] Found error {openingError.ToString()} when trying to open file {MapFilePath} "
		);
		return false;
	}

	//Parse the file text until we have it line by line
	private bool ParseMapInfo(in string fileContents)
	{
		if (fileContents.Length == 0)
		{
			GD.PushError("[MapManager::ParseMapInfo] Map file is empty");
			return false;
		}

		SplitMapInfo(fileContents, out List<string> listInfo);

		StoreMapInfo(ref listInfo);

		return true;
	}

	private void SplitMapInfo(in string cleanFileText, out List<string> listOfLines)
	{
		//Split the map info into lines
		string[] arrayLines = cleanFileText.Split("\n");

		//Convert to a list so it's easier to remove empty lines
		listOfLines = arrayLines.ToList();
		//listOfLines.RemoveAll(line => line.Length == 0);
		for (int i = 0; i < listOfLines.Count; i++)
		{
			listOfLines[i] = listOfLines[i].TrimEnd('\r', '\n');
		}
	}

	private void StoreMapInfo(ref List<string> listStrings)
	{
		int x = listStrings.Count;
		int z = GetColumnNumber(ref listStrings);
		char[,] symbolMap = new char[x, z];
		for (int i = 0; i <x; i++)
		{
			for (int j = 0; j < z; j++)
			{
				if (j < listStrings[i].Length)
				{
					symbolMap[i, j] = listStrings[i][j];
				}
				else
				{
					symbolMap[i, j] = ' ';
				}
			}
		}

		MapInfo = new MapInfo(symbolMap);
	}


	private int GetColumnNumber(ref List<string> listString)
	{
		int columNumber = 0;

		foreach (string stringColumn in listString)
		{
			if (stringColumn.Length > columNumber)
			{
				columNumber = stringColumn.Length;
			}
		}

		return columNumber;
	}
	#endregion

	private void ResizeGround()
	{
		if (!AdaptGroundSize)
		{
			// GroundBody.Scale = new Vector3(500, GroundBody.Scale.Y, 500);
			return;
		}

		MapConfiguration mapConfigData = Utilities.ConfigData.GetMapConfigurationData();
		Vector2 mapSize = MapInfo.GetMapSize();
		var newGroundScale = new Vector3(
			mapSize.X * mapConfigData.distance.X,
			GroundBody.Scale.Y,
			mapSize.Y * mapConfigData.distance.Y
		);
		GroundBody.Scale = newGroundScale;
	}


	private void AlignMapToOrigin()
	{
		MapConfiguration mapConfigData = Utilities.ConfigData.GetMapConfigurationData();
		Vector3 newGroundPosition =
			mapConfigData.origin + new Vector3(GroundBody.Scale.X / 2, 0, GroundBody.Scale.Z / 2);
		GroundBody.Position = newGroundPosition;
		//Adding some padding so the map is slightly larger than the field we are representing
		GroundBody.Scale = GroundBody.Scale * new Vector3(1.2f, 1f, 1.2f);
	}

	private void CorrectInfoCollectionTransforms()
	{
		foreach (LightInfo lightInfo in MapConfingInfo.lights)
		{
			lightInfo.position = Utilities.Math.OrientVector3(lightInfo.position);
		}

		foreach (ObjectInfo objectInfo in MapConfingInfo.objects)
		{
			objectInfo.position = Utilities.Math.OrientVector3(objectInfo.position);
		}
	}

	private void SetupLightning()
	{
		foreach (LightInfo lightInfo in MapConfingInfo.lights)
		{
			if (!lightInfo.active)
			{
				continue;
			}

			if (lightInfo.objectName == "Sun Light")
			{
				//TODO: Check if we need different types of assets based on what light it is
				//DirectionalLight3D lightSource =(DirectionalLight3D) Utilities.Entities.SpawnNewEntity(SunLight);
				var lightSource = new DirectionalLight3D();

				GroundBody.AddChild(lightSource);
				lightSource.GlobalPosition = lightInfo.position;
				lightSource.GlobalRotation = lightInfo.rotation;
				lightSource.LightColor = lightInfo.color;

				//TODO: Comprobar que esto funca bien
				lightSource.LightEnergy = lightInfo.intensity;
			}
		}
	}
}