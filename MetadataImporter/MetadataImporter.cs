using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Elements.Core;
using FrooxEngine;
using HarmonyLib;
using ResoniteModLoader;
using File = TagLib.File;

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



	[HarmonyPatch]
	public static class AudioImporterPatch {
		private static MethodBase GetMoveNext(MethodInfo method) {
			if (method == null) return null!;
			var attr = method.GetCustomAttribute<AsyncStateMachineAttribute>();
			if (attr == null) return null!;
			return AccessTools.Method(attr.StateMachineType, "MoveNext");
		}

		public static IEnumerable<MethodBase> TargetMethods() {
			// Vanilla
			var vanillaMethod = AccessTools.Method(typeof(UniversalImporter), "ImportTask");
			var vanillaTarget = GetMoveNext(vanillaMethod);
			if (vanillaTarget != null) yield return vanillaTarget;

			// CommunityBugFixCollection (didn't realize they
			// use a prefix for this 😭)
			var modType = AccessTools.TypeByName("CommunityBugFixCollection.ImportMultipleAudioFiles");
			if (modType != null) {
				Msg("[AudioImporterPatch] Found CommunityBugFixCollection! Attempting to patch its ImportAudioAsync...");

				var modMethod = AccessTools.Method(modType, "ImportAudioAsync");
				var modTarget = GetMoveNext(modMethod);

				if (modTarget != null) {
					yield return modTarget;
					Msg("[AudioImporterPatch] Added CommunityBugFixCollection to patch targets.");
				} else {
					Warn("[AudioImporterPatch] Found the mod, but couldn't find the async mover for ImportAudioAsync </3");
				}
			}
		}

		public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator il) {
			Msg("[AudioImporterPatch] Starting Transpiler...");
			var matcher = new CodeMatcher(instructions, il);

			// ldfld <audioPlayer>
			// ldarg.0
			// ldfld <file>
			// call Path.GetFileName
			// callvirt EntityInterface.InitializeEntity

			var initializeEntityMethod = AccessTools.Method(typeof(EntityInterface), nameof(EntityInterface.InitializeEntity));
			var getFileNameMethod = AccessTools.Method(typeof(Path), nameof(Path.GetFileName), new Type[] { typeof(string) });

			matcher.MatchStartForward(
				new CodeMatch(OpCodes.Ldfld),   // audioPlayer field
				new CodeMatch(OpCodes.Ldarg_0),
				new CodeMatch(OpCodes.Ldfld),   // file field
				new CodeMatch(OpCodes.Call, getFileNameMethod),
				new CodeMatch(OpCodes.Callvirt, initializeEntityMethod)
			);

			if (matcher.IsInvalid) {
				Warn("[AudioImporterPatch] ERROR: Could not find field capture pattern </3");
				return instructions;
			}

			// We are at the start of the match (Ldfld <audioPlayer>)
			FieldInfo audioPlayerField = (FieldInfo)matcher.Operand;

			// The file field is 2 instructions ahead (Ldfld <file>)
			FieldInfo fileField = (FieldInfo)matcher.InstructionAt(2).operand;

			Msg($"[AudioImporterPatch] Fields captured. AudioPlayer: {audioPlayerField.Name}, File: {fileField.Name}");

			// add function after setType

			matcher.Start();

			int patchCount = 0;

			while (true) {
				// Find next call to SetType using NAME MATCHING (Safe against overloads)
				matcher.MatchStartForward(
					new CodeMatch(op => op.opcode == OpCodes.Callvirt && (op.operand as MethodInfo)?.Name == "SetType")
				);

				if (matcher.IsInvalid) break;

				// Advance 1 to go AFTER the Callvirt
				matcher.Advance(1);

				// Insert our custom code
				matcher.InsertAndAdvance(
					// Load 'this' (State Machine)
					new CodeInstruction(OpCodes.Ldarg_0),
					// Load 'file'
					new CodeInstruction(OpCodes.Ldfld, fileField),

					// Load 'this' (State Machine)
					new CodeInstruction(OpCodes.Ldarg_0),
					// Load 'audioPlayer'
					new CodeInstruction(OpCodes.Ldfld, audioPlayerField),

					// Call custom method
					new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(AudioImporterPatch), nameof(ApplyMetadata)))
				);

				patchCount++;
			}

			Msg($"[AudioImporterPatch] Patch applied in {patchCount} place(s)!");
			return matcher.InstructionEnumeration();
		}

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

						if (useValue is int intValue) {
							if (intValue <= 0) continue; // i think this should be okay?
						}
						if (useValue is string && int.TryParse((string)useValue, out int intValueBackup)) {
							if (intValueBackup <= 0) continue;
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
							};
						}
					}
				}

				if (isResMDCompliant) {
					DynamicVariableHelper.WriteDynamicVariable<string>(metadataSlot, "_SMFIELDS", string.Join(";", writtenDynVars));
					Msg("Wrote to ResMD compliant interface!");
				}
			});
		}
	}
}
