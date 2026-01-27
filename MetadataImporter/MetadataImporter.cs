using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Elements.Core;
using FrooxEngine;
using HarmonyLib;
using ResoniteModLoader;
using File = TagLib.File;
using FrooxEngine.Store;





#if DEBUG
using ResoniteHotReloadLib;
#endif

namespace MetadataImporter;
public class MetadataImporter : ResoniteMod {
	internal const string VERSION_CONSTANT = "1.0.0";
	public override string Name => "MetadataImporter";
	public override string Author => "Noble";
	public override string Version => VERSION_CONSTANT;
	public override string Link => "https://github.com/noblereign/ResoniteMetadataImporter/";

	const string harmonyId = "dog.glacier.MetadataImporter";

	[AutoRegisterConfigKey]
	public static readonly ModConfigurationKey<bool> Enabled = new("Enabled", "Enables the mod, pretty self explanatory!", () => true);

	[AutoRegisterConfigKey]
	public static readonly ModConfigurationKey<bool> UseFilenameAsTrackName = new("Fallback to File Name", "Should the file name be used as a fallback when the track name is missing?", () => true);

	private static Dictionary<string, string[]> TagSynonyms = new Dictionary<string, string[]> {
		["Performers"] = ["Artist", "Artists", "JoinedPerformers"],
		["PerformersSort"] = ["ArtistSort", "ArtistsSort", "JoinedPerformersSort", "SortPerformers", "SortArtist", "SortArtists", "SortJoinedPerformers"],
		["Composers"] = ["Composer", "JoinedComposers"],
		["ComposersSort"] = ["ComposerSort", "JoinedComposersSort", "SortComposer", "SortComposers", "SortJoinedComposers"],
		["AlbumArtists"] = ["AlbumArtist", "JoinedAlbumArtists"],
		["AlbumArtistsSort"] = ["AlbumArtistSort", "JoinedAlbumArtistsSort", "SortAlbumArtists", "SortAlbumArtist", "SortJoinedAlbumArtists"],
		["Genres"] = ["Genre", "JoinedGenres"],
		["BeatsPerMinute"] = ["BPM"],
		["Description"] = ["Desc"],
		["Subtitle"] = ["Tagline", "ShortDescription", "ShortDesc"],
		["TitleSort"] = ["SortTitle"],
		["ReplayGainAlbumGain"] = ["AlbumGain"],
		["ReplayGainAlbumPeak"] = ["AlbumPeak"],
		["ReplayGainTrackGain"] = ["TrackGain"],
		["ReplayGainTrackPeak"] = ["TrackPeak"],
		["MusicIpId"] = ["IPID"],
		["MusicBrainzArtistId"] = ["ArtistId", "MBID.Artist"],
		["MusicBrainzDiscId"] = ["DiscId", "MBID.Disc"],
		["MusicBrainzReleaseId"] = ["ReleaseId", "MBID.Release"],
		["MusicBrainzTrackId"] = ["TrackId", "RecordingId", "MusicBrainzRecordingId", "MBID.Track", "MBID.Recording"],
		["MusicBrainzReleaseArtistId"] = ["ReleaseArtistId", "MBID.ReleaseArtist", "MBID.AlbumArtist"],
		["MusicBrainzReleaseGroupId"] = ["ReleaseGroupId", "MBID.ReleaseGroup"],
		["MusicBrainzReleaseType"] = ["ReleaseType", "MB.ReleaseType"],
	};
	
	private static Dictionary<string, string> ResMDRemaps = new Dictionary<string, string> {
		["Performers"] = "Artist",
		["PerformersSort"] = "ArtistSort",
		["Composers"] = "Composer",
		["ComposersSort"] = "ComposerSort",
		["AlbumArtists"] = "AlbumArtist",
		["AlbumArtistsSort"] = "AlbumArtistSort",
		["Genres"] = "Genre",
		["BeatsPerMinute"] = "BPM",
		["MusicBrainzArtistId"] = "MBID.Artist",
		["MusicBrainzDiscId"] = "MBID.Disc",
		["MusicBrainzReleaseId"]= "MBID.Release",
		["MusicBrainzTrackId"] = "MBID.Recording",
		["MusicBrainzReleaseArtistId"] = "MBID.AlbumArtist",
		["MusicBrainzReleaseGroupId"] = "MBID.AlbumGroup",
		["MusicBrainzReleaseType"] = "AlbumType"
	};

	private static string[] IgnoreTags = new string[] { // ignoring these as we generate them on our own.
		"JoinedPerformers",
		"JoinedAlbumArtists",
		"JoinedArtists",
		"JoinedComposers",
		"JoinedGenres",
		"JoinedPerformersSort",
	};

	private static ConditionalWeakTable<string, Uri?> _currentImportBatch = new ConditionalWeakTable<string, Uri?>();

	public static ModConfiguration? Config;
	public override void OnEngineInit() {
		#if DEBUG
		HotReloader.RegisterForHotReload(this);
		#endif

		Config = GetConfiguration()!;
		Config!.Save(true);

		// Call setup method
		Setup();
	}

	static void Setup() {
		// Patch Harmony
		Harmony harmony = new Harmony(harmonyId);
		harmony.PatchAll();
	}

	#if DEBUG
	// This is the method that should be used to unload your mod
	// This means removing patches, clearing memory that may be in use etc.
	static void BeforeHotReload() {
		// Unpatch Harmony
		Harmony harmony = new Harmony(harmonyId);
		harmony.UnpatchAll(harmonyId);
	}

	// This is called in the newly loaded assembly
	// Load your mod here like you normally would in OnEngineInit

	static void OnHotReload(ResoniteMod modInstance) {
		// Get the config if needed
		Config = modInstance.GetConfiguration()!;
		Config!.Save(true);

		// Call setup method
		Setup();
	}
#endif



	public static void ApplyMetadata(string file, AudioPlayerInterface audioPlayer) {
		if (!(Config != null ? Config.GetValue(Enabled)! : false)) return;

		if (audioPlayer == null) { Warn("No AudioPlayerInterface was passed. Ending early."); return; }
		if (file == null) { Warn("File was null, ending early."); return; }
		Debug($"Discovering audio player slot");
		Slot interfaceSlot = audioPlayer.Slot;
		Debug($"Discovering metadata slot");
		Slot metadataSlot = interfaceSlot.FindChildOrAdd("Metadata", true);

		//TODO: Inject dynvars if the slot didn't exist.
		Debug($"Discovering dynamic variable space");
		DynamicVariableSpace mainSpace = interfaceSlot.FindSpace(null!); // if there's nothing it'll probably just be the World one

		Debug($"Discovering settings...");

		interfaceSlot.RunInUpdates(3, () => {
			mainSpace.TryReadValue<string>("MetadataImporter.Separator", out string? requestedSeperator);
			string useSeparator = requestedSeperator ?? ", ";
			mainSpace.TryReadValue<bool>("MetadataImporter.CastToStrings", out bool requestedStringCast);
			bool castToStrings = requestedStringCast;

			bool isResMDCompliant = false;
			foreach (var identity in mainSpace._dynamicValues.Keys) {
				if (identity.name.Equals("_SMFIELDS") && identity.type == typeof(string)) {
					isResMDCompliant = true;
					break;
				}
			}

			bool injectDynVars = false;

			List<IDynamicVariable> dynamicVariableList = metadataSlot.GetComponents<IDynamicVariable>();

			if (dynamicVariableList.Count <= (isResMDCompliant ? 1 : 0)) {
				injectDynVars = true;
			}

			List<string> writtenDynVars = new List<string>();

			Debug($"Seperator: [{useSeparator}]");
			Debug($"Cast to strings: [{castToStrings}]");

			Debug($"Getting the metadata for {file}...");

			using (File TaggedFile = TagLib.File.Create(@file)) {
				TagLib.Tag FileTags = TaggedFile.Tag;

				PropertyInfo[] properties = typeof(TagLib.Tag).GetProperties();

				foreach (var prop in properties) {

					if (IgnoreTags.Contains(prop.Name)) continue;

					List<string> propertyNames = [prop.Name];

					if (TagSynonyms.ContainsKey(prop.Name)) {
						propertyNames.AddRange(TagSynonyms[prop.Name]);
					}

					foreach (string name in propertyNames.ToList()) {
						// allow matching to spaced names, e.g. "Beats Per Minute" instead of "BeatsPerMinute"
						string spacedName = Regex.Replace(name, @"((?<=\p{Ll})\p{Lu})|((?!\A)\p{Lu}(?>\p{Ll}))", " $0");
						if (spacedName != name) {
							propertyNames.Add(spacedName);
						}
					}

					var value = prop.GetValue(FileTags);

					if (value == null) {
						if (prop.Name == "Title" && (Config != null ? Config.GetValue(UseFilenameAsTrackName)! : true)) {
							Msg("No track title metadata found, falling back to file name.");
							value = Path.GetFileName(file);
						} else {
							continue;
						}
					}

					if (value is TagLib.IPicture[] photos) {
						//TODO: Maybe import album art?
						//Not really a priority for me right now, but it could be cool...
						Debug($"Skipping {prop.Name} for now, TagLib IPicture is not supported yet");
						continue;
					}

					string? joinedValue = null;

					if (value is string[] stringedValue) {
						Debug($"(Joining {prop.Name}...)");
						joinedValue = string.Join(useSeparator, stringedValue);

						if (useSeparator != ", ") {
							// we want to be doubly sure that the correct separator is being used
							// for some reason a lot of stuff doesn't *actually* give a proper 'array' of artists, instead its just comma seperated in the metadata
							// so let's process them to turn it into a array and then back again

							List<string> dividedText = joinedValue
								.Split(',')
								.Select(s => s.Trim())
								.Where(s => !string.IsNullOrEmpty(s)) // Optional: removes empty strings
								.ToList();

							joinedValue = string.Join(useSeparator, dividedText);
						}

					}

					var useValue = joinedValue ?? (castToStrings ? value.ToString() : value);

					// A bunch of sanity checks to make sure invalid metadata doesn't get through
					if (useValue is string stringUse) {
						if (stringUse.Trim().Length <= 0) continue;
					}

					if (useValue is Enum enumValue) {
						useValue = enumValue.ToString(); // dear GOD convert it to a string, evil things happen if you don't 😭
					}

					if (useValue is int intValue) {
						if (intValue <= 0) continue; // i think this should be okay?
					}
					if (useValue is string && int.TryParse((string)useValue, out int intValueBackup)) {
						if (intValueBackup <= 0) continue;
					}

					if (useValue is uint uintValue) {
						if (uintValue <= 0) continue;
					}
					if (useValue is string && uint.TryParse((string)useValue, out uint uintValueBackup)) {
						if (uintValueBackup <= 0) continue;
					}

					if (value is float floatValue) {
						if (float.IsNaN(floatValue)) continue;
						if (float.IsInfinity(floatValue)) continue;
					}
					if (useValue is string && float.TryParse((string)useValue, out float floatValueBackup)) {
						if (float.IsNaN(floatValueBackup)) continue;
						if (float.IsInfinity(floatValueBackup)) continue;
					}

					if (value is double doubleValue) {
						if (double.IsNaN(doubleValue)) continue;
						if (double.IsInfinity(doubleValue)) continue;
					}
					if (useValue is string && double.TryParse((string)useValue, out double doubleValueBackup)) {
						if (double.IsNaN(doubleValueBackup)) continue;
						if (double.IsInfinity(doubleValueBackup)) continue;
					}
					// there is probably a smarter way to do this but for some reason just "is" checks were NOT working :(

					Debug($"Scanning for field {prop.Name} using synonyms: {String.Join(",", propertyNames)}");

					foreach (string name in propertyNames) {
						bool wasWritten = false;
						foreach (var identity in mainSpace._dynamicValues.Keys) {
							if (identity.name.Equals(name, StringComparison.OrdinalIgnoreCase) && identity.type.IsInstanceOfType(useValue)) {
								MethodInfo method = typeof(DynamicVariableHelper).GetMethod(nameof(DynamicVariableHelper.WriteDynamicVariable))!;
								MethodInfo generic = method.MakeGenericMethod(identity.type);
								DynamicVariableWriteResult writeResult = (DynamicVariableWriteResult)generic.Invoke(null, [metadataSlot, name, Convert.ChangeType(useValue, identity.type)])!;

								if (writeResult == DynamicVariableWriteResult.Success) {
									Msg($"✅ {name}: {useValue}");
									writtenDynVars.Add(name);
									wasWritten = true;
									break;
								}
							}
						}

						if (wasWritten) {
							break;
						} else if (injectDynVars) {
							string useName = (isResMDCompliant ? (ResMDRemaps.ContainsKey(name) ? ResMDRemaps[name] : name) : name);

							MethodInfo method = typeof(DynamicVariableHelper).GetMethod(nameof(DynamicVariableHelper.CreateVariable))!;
							MethodInfo generic = method.MakeGenericMethod(useValue!.GetType());
							bool createdSuccessfully = (bool)generic.Invoke(null, [metadataSlot, useName, useValue, true])!;

							if (createdSuccessfully) {
								Msg($"✏ {useName}: {useValue}");
								writtenDynVars.Add(useName);
								break;
							}
						}
						;
					}
				}
			}

			if (isResMDCompliant) {
				DynamicVariableHelper.WriteDynamicVariable<string>(metadataSlot, "_SMFIELDS", string.Join(";", writtenDynVars));
				Msg("Wrote to ResMD compliant interface!");
			}
		});
	}


	[HarmonyPatch(typeof(UniversalImporter), nameof(UniversalImporter.ImportTask))]
	public static class FileBatchCapturer {

		[HarmonyPrefix]
		[HarmonyAfter("com.__Choco__.ResoniteMP3")]
		[HarmonyPriority(HarmonyLib.Priority.HigherThanNormal)] // to run before communitybugfix
		private static void Prefix(IEnumerable<string> files) {
			if (files != null) {
				_currentImportBatch.Clear();
				foreach (string file in files) {
					_currentImportBatch.Add(file, null);
				}
				Msg($"Found {_currentImportBatch.Count()} files!");
			}
		}
	}


	[HarmonyPatch(typeof(AudioPlayerInterface), nameof(AudioPlayerInterface.SetSource), typeof(Uri))]
	public static class MetadataApplier {
		[HarmonyPostfix]
		public static async void Postfix(AudioPlayerInterface __instance, Uri url) {
			foreach (KeyValuePair<string, Uri?> pair in _currentImportBatch.ToList()) {
				Uri? generatedUri = pair.Value;
				if (generatedUri == null) {
					Debug($"Generating hash for {pair.Key}");
					string hashSig = await FileUtil.GenerateFileSignatureAsync(pair.Key);
					AssetRecord assetInfo = await Engine.Current.LocalDB.TryFetchAssetBySignatureAsync(hashSig);
					generatedUri = new Uri(assetInfo.url);
					_currentImportBatch.AddOrUpdate(pair.Key, generatedUri);
					Debug($"Hash generated and stored");
				}

				if (Equals(generatedUri, url)) {
					ApplyMetadata(pair.Key, __instance);
					break;
				}
			}
		}
	}
}
