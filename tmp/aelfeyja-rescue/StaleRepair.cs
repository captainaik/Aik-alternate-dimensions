using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.ObjectSystem;

namespace AelfeyjaRescue
{
	public static class StaleRepairCommands
	{
		[CommandLineFunctionality.CommandLineArgumentFunction("scan_stale", "rescue")]
		public static string ScanStale(List<string> args)
		{
			return StaleCharacterRepairService.ScanReport();
		}

		[CommandLineFunctionality.CommandLineArgumentFunction("repair_stale", "rescue")]
		public static string RepairStale(List<string> args)
		{
			return StaleCharacterRepairService.RepairAll();
		}
	}

	internal static class StaleCharacterRepairService
	{
		private static readonly FieldInfo DefaultSkillsField =
			typeof(BasicCharacterObject).GetField("DefaultCharacterSkills", BindingFlags.Instance | BindingFlags.NonPublic);

		private static readonly FieldInfo BasicNameField =
			typeof(BasicCharacterObject).GetField("_basicName", BindingFlags.Instance | BindingFlags.NonPublic);

		private static readonly PropertyInfo BodyPropertyRangeProperty =
			typeof(BasicCharacterObject).GetProperty("BodyPropertyRange", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

		private static readonly PropertyInfo UpgradeTargetsProperty =
			typeof(CharacterObject).GetProperty("UpgradeTargets", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

		private static readonly string[] DumpConfirmedIds = new[]
		{
			"eastern_mercenary_t5",
			"sword_sisters_sister_infantry_t5",
			"eastern_mounted_mercenary_t5",
			"western_mercenary_t5",
			"western_crossbow_t5",
			"sword_sisters_sister_t5",
			"mercenary_7",
			"mercenary_8",
			"mercenary_9",
			"western_mercenary_t4",
			"sword_sisters_sister_t4",
			"western_crossbow_t4",
			"eastern_mercenary_t4",
			"eastern_mounted_mercenary_t4",
			"mercenary_4",
			"mercenary_5",
			"mercenary_6"
		};

		public static string ScanReport()
		{
			try
			{
				List<BasicCharacterObject> stale = FindStaleCharacters();
				string[] ids = stale.Select(SafeId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
				int dumpMatches = ids.Count(x => DumpConfirmedIds.Contains(x));
				string list = ids.Length == 0 ? "none" : string.Join(", ", ids.Take(40));
				string result = "STALE SCAN: count=" + stale.Count + ", dump-confirmed=" + dumpMatches + ". IDs: " + list;
				Log(result);
				return result;
			}
			catch (Exception ex)
			{
				string result = "STALE SCAN ERROR: " + Flatten(ex);
				Log(result);
				return result;
			}
		}

		public static string RepairAll()
		{
			try
			{
				if (DefaultSkillsField == null || BasicNameField == null || BodyPropertyRangeProperty == null || UpgradeTargetsProperty == null)
				{
					string bindings = "Bindings: skills=" + (DefaultSkillsField != null)
						+ ", name=" + (BasicNameField != null)
						+ ", body=" + (BodyPropertyRangeProperty != null)
						+ ", upgrades=" + (UpgradeTargetsProperty != null);
					Log("STALE REPAIR ABORTED. " + bindings);
					return "STALE REPAIR ABORTED. " + bindings;
				}

				List<BasicCharacterObject> stale = FindStaleCharacters();
				var repaired = new List<string>();
				var failed = new List<string>();

				foreach (BasicCharacterObject character in stale)
				{
					string id = SafeId(character);
					try
					{
						RepairOne(character);
						if (IsStale(character))
							failed.Add(id + "(still stale)");
						else
							repaired.Add(id);
					}
					catch (Exception ex)
					{
						failed.Add(id + "(" + Flatten(ex) + ")");
					}
				}

				List<BasicCharacterObject> remaining = FindStaleCharacters();
				string repairedIds = repaired.Count == 0 ? "none" : string.Join(", ", repaired.Take(40));
				string failedIds = failed.Count == 0 ? "none" : string.Join(", ", failed.Take(20));
				string remainingIds = remaining.Count == 0 ? "none" : string.Join(", ", remaining.Select(SafeId).Take(20));
				string result = "STALE REPAIR: found=" + stale.Count
					+ ", repaired=" + repaired.Count
					+ ", failed=" + failed.Count
					+ ", remaining=" + remaining.Count
					+ ". Repaired IDs: " + repairedIds
					+ ". Failed: " + failedIds
					+ ". Remaining: " + remainingIds;
				Log(result);
				return result;
			}
			catch (Exception ex)
			{
				string result = "STALE REPAIR ERROR: " + Flatten(ex);
				Log(result);
				return result;
			}
		}

		private static List<BasicCharacterObject> FindStaleCharacters()
		{
			var result = new List<BasicCharacterObject>();
			var objects = MBObjectManager.Instance.GetObjectTypeList<BasicCharacterObject>();
			if (objects == null)
				return result;

			for (int i = 0; i < objects.Count; i++)
			{
				BasicCharacterObject character = objects[i];
				if (character == null)
					continue;

				try
				{
					if (IsStale(character))
						result.Add(character);
				}
				catch
				{
					// A malformed object should itself be treated as suspect, but do not let
					// one bad getter abort the whole scan.
					if (DefaultSkillsField != null && DefaultSkillsField.GetValue(character) == null)
						result.Add(character);
				}
			}

			return result;
		}

		private static bool IsStale(BasicCharacterObject character)
		{
			if (character == null)
				return false;

			if (DefaultSkillsField == null || DefaultSkillsField.GetValue(character) == null)
				return true;

			if (BodyPropertyRangeProperty == null || BodyPropertyRangeProperty.GetValue(character, null) == null)
				return true;

			object name = BasicNameField == null ? null : BasicNameField.GetValue(character);
			if (name == null)
				return true;

			CharacterObject troop = character as CharacterObject;
			if (troop != null)
			{
				object upgrades = UpgradeTargetsProperty == null ? null : UpgradeTargetsProperty.GetValue(troop, null);
				if (upgrades == null)
					return true;
			}

			return false;
		}

		private static void RepairOne(BasicCharacterObject character)
		{
			if (DefaultSkillsField.GetValue(character) == null)
				DefaultSkillsField.SetValue(character, new MBCharacterSkills());

			if (BasicNameField.GetValue(character) == null)
				BasicNameField.SetValue(character, new TextObject(SafeId(character)));

			if (character is CharacterObject troop && UpgradeTargetsProperty.GetValue(troop, null) == null)
			{
				MethodInfo setter = UpgradeTargetsProperty.GetSetMethod(true);
				if (setter == null)
					throw new InvalidOperationException("UpgradeTargets setter not found");
				setter.Invoke(troop, new object[] { new CharacterObject[0] });
			}

			if (BodyPropertyRangeProperty.GetValue(character, null) == null)
			{
				MethodInfo setter = BodyPropertyRangeProperty.GetSetMethod(true);
				if (setter == null)
					throw new InvalidOperationException("BodyPropertyRange setter not found");

				MBBodyProperty range = new MBBodyProperty(SafeId(character));
				range = MBObjectManager.Instance.RegisterPresumedObject(range);
				if (range == null)
					throw new InvalidOperationException("Could not register fallback MBBodyProperty");
				range.Init(default(BodyProperties), default(BodyProperties));
				setter.Invoke(character, new object[] { range });
			}
		}

		private static string SafeId(BasicCharacterObject character)
		{
			try
			{
				return character == null ? "<null>" : (character.StringId ?? "<blank-id>");
			}
			catch
			{
				return "<unreadable-id>";
			}
		}

		private static string Flatten(Exception ex)
		{
			while (ex is TargetInvocationException && ex.InnerException != null)
				ex = ex.InnerException;
			return ex.GetType().Name + ": " + ex.Message;
		}

		private static void Log(string message)
		{
			try
			{
				string baseDir = AppDomain.CurrentDomain.BaseDirectory;
				string root = Directory.GetParent(baseDir)?.Parent?.FullName ?? baseDir;
				string moduleDir = Path.Combine(root, "Modules", "AelfeyjaRescue");
				Directory.CreateDirectory(moduleDir);
				File.AppendAllText(Path.Combine(moduleDir, "rescue.log"),
					"[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + message + Environment.NewLine);
			}
			catch
			{
			}
		}
	}
}
