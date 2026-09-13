using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AelfeyjaRescue
{
	public sealed class SubModule : MBSubModuleBase
	{
		private static DateTime _nextAttempt = DateTime.MinValue;
		private static bool _autoRepairCompleted;

		protected override void OnSubModuleLoad()
		{
			base.OnSubModuleLoad();
			RescueService.LogPublic("Crash Rescue v1.1 loaded. Crash-dump target: western_mercenary_t4 / Leadership / null DefaultCharacterSkills.");
		}

		protected override void OnApplicationTick(float dt)
		{
			base.OnApplicationTick(dt);
			if (_autoRepairCompleted || DateTime.UtcNow < _nextAttempt)
				return;

			_nextAttempt = DateTime.UtcNow.AddMilliseconds(100);
			if (!RescueService.IsCampaignReady())
				return;

			string result = RescueService.RepairBrokenSkillTemplates(false);
			if (result.IndexOf("CAMPAIGN_NOT_READY", StringComparison.Ordinal) < 0 &&
				result.IndexOf("TARGET_NOT_FOUND", StringComparison.Ordinal) < 0)
			{
				_autoRepairCompleted = true;
				RescueService.LogPublic("AUTO " + result);
			}
		}
	}

	public static class RescueCommands
	{
		[CommandLineFunctionality.CommandLineArgumentFunction("fix_skills", "rescue")]
		public static string FixSkills(List<string> args)
		{
			return RescueService.RepairBrokenSkillTemplates(true);
		}

		[CommandLineFunctionality.CommandLineArgumentFunction("scan_skills", "rescue")]
		public static string ScanSkills(List<string> args)
		{
			return RescueService.ScanBrokenSkillTemplates();
		}

		[CommandLineFunctionality.CommandLineArgumentFunction("scan_parties", "rescue")]
		public static string ScanParties(List<string> args)
		{
			return RescueService.ScanParties();
		}

		[CommandLineFunctionality.CommandLineArgumentFunction("status", "rescue")]
		public static string Status(List<string> args)
		{
			return RescueService.Status();
		}
	}

	internal static class RescueService
	{
		private const string CrashTargetId = "western_mercenary_t4";
		private static string _lastResult = "Not run yet.";

		public static bool IsCampaignReady()
		{
			Type campaignType = FindType("TaleWorlds.CampaignSystem.Campaign");
			if (campaignType == null)
				return false;
			object current = GetStaticMember(campaignType, "Current");
			return current != null;
		}

		public static string Status()
		{
			try
			{
				object target = FindCharacter(CrashTargetId);
				if (target == null)
					return Finish("TARGET_NOT_FOUND: " + CrashTargetId);

				FieldInfo field = FindDefaultSkillsField(target.GetType());
				object skills = field == null ? null : field.GetValue(target);
				return Finish("Crash target=" + CrashTargetId + ", IsHero=" + SafeValue(GetProperty(target, "IsHero")) +
					", DefaultCharacterSkills=" + (skills == null ? "NULL" : "OK") + ". Last=" + _lastResult);
			}
			catch (Exception ex)
			{
				return Finish("STATUS ERROR: " + Flatten(ex));
			}
		}

		public static string ScanBrokenSkillTemplates()
		{
			try
			{
				if (!IsCampaignReady())
					return Finish("CAMPAIGN_NOT_READY");

				List<object> characters = GetAllCharacters().ToList();
				var broken = new List<string>();
				foreach (object c in characters)
				{
					if (c == null || IsHero(c))
						continue;
					FieldInfo f = FindDefaultSkillsField(c.GetType());
					if (f != null && f.GetValue(c) == null)
						broken.Add(GetStringId(c));
				}

				string ids = broken.Count == 0 ? "none" : string.Join(", ", broken.Take(25).ToArray());
				return Finish("BROKEN_SKILLS=" + broken.Count + ": " + ids);
			}
			catch (Exception ex)
			{
				return Finish("SCAN ERROR: " + Flatten(ex));
			}
		}

		public static string RepairBrokenSkillTemplates(bool verbose)
		{
			try
			{
				if (!IsCampaignReady())
					return Finish("CAMPAIGN_NOT_READY");

				List<object> characters = GetAllCharacters().ToList();
				object target = characters.FirstOrDefault(c => string.Equals(GetStringId(c), CrashTargetId, StringComparison.Ordinal));
				if (target == null)
					return Finish("TARGET_NOT_FOUND: " + CrashTargetId);

				FieldInfo field = FindDefaultSkillsField(target.GetType());
				if (field == null)
					return Finish("Could not locate BasicCharacterObject.DefaultCharacterSkills field.");

				object preferredDonor = FindDonor(characters, field);
				if (preferredDonor == null)
					return Finish("No valid skill-template donor could be found. No changes made.");

				object donorSkills = field.GetValue(preferredDonor);
				var repaired = new List<string>();
				foreach (object c in characters)
				{
					if (c == null || IsHero(c))
						continue;
					FieldInfo cf = FindDefaultSkillsField(c.GetType());
					if (cf == null || cf.GetValue(c) != null)
						continue;

					cf.SetValue(c, donorSkills);
					repaired.Add(GetStringId(c));
				}

				bool targetOk = field.GetValue(target) != null;
				string verification = VerifyLeadershipLookup(target);
				string result = "SKILL REPAIR: repaired=" + repaired.Count +
					", target=" + CrashTargetId + "=" + (targetOk ? "OK" : "STILL_NULL") +
					", donor=" + GetStringId(preferredDonor) +
					", LeadershipTest=" + verification +
					". IDs=" + (repaired.Count == 0 ? "none" : string.Join(",", repaired.Take(20).ToArray()));
				return Finish(result);
			}
			catch (Exception ex)
			{
				return Finish("SKILL REPAIR ERROR: " + Flatten(ex));
			}
		}

		public static string ScanParties()
		{
			try
			{
				if (!IsCampaignReady())
					return Finish("CAMPAIGN_NOT_READY");

				IEnumerable parties = GetAllParties();
				if (parties == null)
					return Finish("Could not enumerate mobile parties.");

				var hits = new List<string>();
				foreach (object p in parties)
				{
					if (p == null)
						continue;
					object roster = GetProperty(p, "MemberRoster");
					if (roster == null)
						continue;
					bool contains = RosterContains(roster, CrashTargetId);
					if (!contains)
						continue;
					string pname = SafeName(p);
					string pid = SafeValue(GetProperty(p, "StringId"));
					string leader = SafeName(GetProperty(p, "LeaderHero"));
					hits.Add(pname + "[" + pid + "] leader=" + leader);
				}

				return Finish("PARTIES_WITH_" + CrashTargetId + "=" + hits.Count + ": " + (hits.Count == 0 ? "none" : string.Join(" | ", hits.Take(15).ToArray())));
			}
			catch (Exception ex)
			{
				return Finish("PARTY SCAN ERROR: " + Flatten(ex));
			}
		}

		private static object FindDonor(List<object> characters, FieldInfo field)
		{
			string[] preferred = { "western_mercenary_t5", "western_mercenary", "western_crossbow_t4", "vlandian_infantry" };
			foreach (string id in preferred)
			{
				object c = characters.FirstOrDefault(x => string.Equals(GetStringId(x), id, StringComparison.Ordinal));
				if (c != null && !IsHero(c) && field.GetValue(c) != null)
					return c;
			}
			return characters.FirstOrDefault(c => c != null && !IsHero(c) && field.GetValue(c) != null);
		}

		private static string VerifyLeadershipLookup(object character)
		{
			try
			{
				Type defaultSkills = FindType("TaleWorlds.Core.DefaultSkills");
				object leadership = defaultSkills == null ? null : GetStaticMember(defaultSkills, "Leadership");
				if (leadership == null)
					return "skill_object_not_found";

				MethodInfo m = character.GetType().GetMethod("GetSkillValue", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				if (m == null)
					return "method_not_found";
				object value = m.Invoke(character, new[] { leadership });
				return value == null ? "null" : value.ToString();
			}
			catch (Exception ex)
			{
				return "FAILED:" + Flatten(ex);
			}
		}

		private static bool RosterContains(object roster, string characterId)
		{
			try
			{
				MethodInfo getRoster = roster.GetType().GetMethod("GetTroopRoster", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				IEnumerable entries = getRoster == null ? null : getRoster.Invoke(roster, null) as IEnumerable;
				if (entries == null)
					return false;
				foreach (object entry in entries)
				{
					object ch = GetProperty(entry, "Character");
					if (ch != null && string.Equals(GetStringId(ch), characterId, StringComparison.Ordinal))
						return true;
				}
			}
			catch { }
			return false;
		}

		private static IEnumerable GetAllParties()
		{
			Type mobilePartyType = FindType("TaleWorlds.CampaignSystem.Party.MobileParty");
			if (mobilePartyType != null)
			{
				object all = GetStaticMember(mobilePartyType, "All");
				if (all is IEnumerable)
					return (IEnumerable)all;
			}
			object campaign = GetStaticMember(FindType("TaleWorlds.CampaignSystem.Campaign"), "Current");
			return campaign == null ? null : GetProperty(campaign, "MobileParties") as IEnumerable;
		}

		private static IEnumerable<object> GetAllCharacters()
		{
			Type characterType = FindType("TaleWorlds.CampaignSystem.CharacterObject");
			if (characterType != null)
			{
				object all = GetStaticMember(characterType, "All");
				if (all is IEnumerable)
				{
					foreach (object c in (IEnumerable)all)
						yield return c;
					yield break;
				}
			}
			object campaign = GetStaticMember(FindType("TaleWorlds.CampaignSystem.Campaign"), "Current");
			IEnumerable chars = campaign == null ? null : GetProperty(campaign, "Characters") as IEnumerable;
			if (chars != null)
				foreach (object c in chars)
					yield return c;
		}

		private static object FindCharacter(string id)
		{
			return GetAllCharacters().FirstOrDefault(c => string.Equals(GetStringId(c), id, StringComparison.Ordinal));
		}

		private static FieldInfo FindDefaultSkillsField(Type type)
		{
			for (Type t = type; t != null; t = t.BaseType)
			{
				FieldInfo f = t.GetField("DefaultCharacterSkills", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
				if (f != null)
					return f;
			}
			return null;
		}

		private static bool IsHero(object c)
		{
			object v = GetProperty(c, "IsHero");
			return v is bool && (bool)v;
		}

		private static string GetStringId(object obj)
		{
			object id = GetProperty(obj, "StringId");
			return id == null ? "<null-id>" : id.ToString();
		}

		private static object GetProperty(object obj, string name)
		{
			if (obj == null)
				return null;
			PropertyInfo p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
			if (p == null)
				return null;
			try { return p.GetValue(obj, null); } catch { return null; }
		}

		private static object GetStaticMember(Type type, string name)
		{
			if (type == null)
				return null;
			PropertyInfo p = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
			if (p != null)
			{
				try { return p.GetValue(null, null); } catch { }
			}
			FieldInfo f = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
			if (f != null)
			{
				try { return f.GetValue(null); } catch { }
			}
			return null;
		}

		private static Type FindType(string fullName)
		{
			foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
			{
				try
				{
					Type t = a.GetType(fullName, false);
					if (t != null)
						return t;
				}
				catch { }
			}
			return null;
		}

		private static string SafeName(object obj)
		{
			if (obj == null)
				return "null";
			try
			{
				object name = GetProperty(obj, "Name");
				return name == null ? GetStringId(obj) : name.ToString();
			}
			catch { return obj.GetType().Name; }
		}

		private static string SafeValue(object obj)
		{
			return obj == null ? "null" : obj.ToString();
		}

		private static string Flatten(Exception ex)
		{
			while (ex is TargetInvocationException && ex.InnerException != null)
				ex = ex.InnerException;
			return ex.GetType().Name + ": " + ex.Message;
		}

		private static string Finish(string message)
		{
			_lastResult = message;
			LogPublic(message);
			return message;
		}

		public static void LogPublic(string message)
		{
			try
			{
				string baseDir = AppDomain.CurrentDomain.BaseDirectory;
				string root = Directory.GetParent(baseDir)?.Parent?.FullName ?? baseDir;
				string moduleDir = Path.Combine(root, "Modules", "AelfeyjaRescue");
				Directory.CreateDirectory(moduleDir);
				File.AppendAllText(Path.Combine(moduleDir, "rescue.log"), "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + message + Environment.NewLine);
			}
			catch { }
		}
	}
}
