using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

using Elements.Assets;
using Elements.Core;

using FrooxEngine;

using HarmonyLib;

using ResoniteModLoader;

using TagLib;

using File = TagLib.File;

namespace MetadataImporter;
public class MetadataImporter : ResoniteMod {
	internal const string VERSION_CONSTANT = "1.0.0"; //Changing the version here updates it in all locations needed
	public override string Name => "MetadataImporter";
	public override string Author => "Noble";
	public override string Version => VERSION_CONSTANT;
	public override string Link => "https://github.com/noblereign/ResoniteMetadataImporter/";

	public override void OnEngineInit() {
		Harmony harmony = new("dog.glacier.MetadataImporter");
		harmony.PatchAll();
	}

	[HarmonyPatch]
	public static class AudioImporterPatch {
		private static MethodBase GetMoveNext(MethodInfo method) {
			if (method == null) return null;
			var attr = method.GetCustomAttribute<AsyncStateMachineAttribute>();
			if (attr == null) return null;
			return AccessTools.Method(attr.StateMachineType, "MoveNext");
		}

		public static IEnumerable<MethodBase> TargetMethods() {
			// Vanilla
			var vanillaMethod = AccessTools.Method(typeof(UniversalImporter), "ImportTask");
			var vanillaTarget = GetMoveNext(vanillaMethod);
			if (vanillaTarget != null) yield return vanillaTarget;

			// CommunityBugFixCollection (didn't realize they use a prefix for this 😭)
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
			if (audioPlayer == null) { return; }
			Slot interfaceSlot = audioPlayer.Slot;
			Slot metadataSlot = interfaceSlot.FindChildOrAdd("Metadata", true);

			Msg($"Getting the metadata for {file ?? "(NULL?!)"}...");

			using (File TaggedFile = TagLib.File.Create(@file)) {
				Tag FileTags = TaggedFile.Tag;

				// Standard stuff that most music will probably have
				string artist = FileTags.JoinedPerformers;
				string title = FileTags.Title;
				string publisher = FileTags.Publisher;
				string album = FileTags.Album;
				string albumArtist = FileTags.JoinedAlbumArtists;
				uint year = FileTags.Year;
				string genres = FileTags.JoinedGenres;
				uint trackNumber = FileTags.Track;
				uint trackCount = FileTags.TrackCount;
				
				// Stuff that isn't as likely but still could appear
				uint bpm = FileTags.BeatsPerMinute;
				string lyrics = FileTags.Lyrics;
				string description = FileTags.Description;
				string copyright = FileTags.Copyright;
				string subtitle = FileTags.Subtitle;
				string remixedBy = FileTags.RemixedBy;
				string titleSort = FileTags.TitleSort;

				// ReplayGain
				double rg_AlbumGain = FileTags.ReplayGainAlbumGain;
				double rg_AlbumPeak = FileTags.ReplayGainAlbumPeak;
				double rg_TrackGain = FileTags.ReplayGainTrackGain;
				double rg_TrackPeak = FileTags.ReplayGainTrackPeak;

				// I feel like it's incredibly unlikely to have these, but sure, expose it anyway
				uint discNumber = FileTags.Disc;
				uint discCount = FileTags.DiscCount;
				string isrc = FileTags.ISRC;
				string ipid = FileTags.MusicIpId;

				// MusicBrainz specific things
				string mb_ArtistId = FileTags.MusicBrainzArtistId;
				string mb_DiscId = FileTags.MusicBrainzDiscId;
				string mb_ReleaseId = FileTags.MusicBrainzReleaseId;
				string mb_TrackId = FileTags.MusicBrainzTrackId;
				string mb_ReleaseArtistId = FileTags.MusicBrainzReleaseArtistId;
				string mb_ReleaseGroupId = FileTags.MusicBrainzReleaseGroupId;
				string mb_ReleaseType = FileTags.MusicBrainzReleaseType;


				TimeSpan duration = TaggedFile.Properties.Duration;
				Msg($"Artists: {artist}");
				Msg($"Title: {title}");
				Msg($"Publisher: {publisher}");
				Msg($"Album: {album}");
				Msg($"Album Artists: {albumArtist}");
				Msg($"Year: {year}");
				Msg($"Genres: {genres}");
			}
		}
	}
}
