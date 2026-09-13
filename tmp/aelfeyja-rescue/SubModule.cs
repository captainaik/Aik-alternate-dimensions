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
		protected override void OnSubModuleLoad()
		{
			base.OnSubModuleLoad();
			CharacterScanner.Log("Character Crash Scanner v1.2 loaded. No automatic repairs are performed.");
		}
	}

	public static class RescueCommands
	{
		[CommandLineFunctionality.CommandLineArgumentFunction("scan_chars", "rescue")]
		public static string ScanChars(List<string> args)
		{
			return CharacterScanner.ScanAllCharacters();
		}

		[CommandLineFunctionality.CommandLineArgumentFunction("scan_parties", "rescue")]
		public static string ScanParties(List<string> args)
		{
			return CharacterScanner.ScanAllPartyRosters();
		}

		[CommandLineFunctionality.CommandLineArgumentFunction("scan_all", "rescue")]
		public static string ScanAll(List<string> args)
		{
			string a = CharacterScanner.ScanAllCharacters();
			string b = CharacterScanner.ScanAllPartyRosters();
			return a + " | " + b;
		}

		[CommandLineFunctionality.CommandLineArgumentFunction("scan_char", "rescue")]
		public static string ScanChar(List<string> args)
		{
			if (args == null || args.Count == 0)
				return "Usage: rescue.scan_char <CharacterObject StringId>";
			return CharacterScanner.ScanOne(args[0]);
		}
	}

	internal static class CharacterScanner
	{
		private static readonly BindingFlags InstanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
		private static readonly BindingFlags StaticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

		public static string ScanAllCharacters()
		{
			try
			{
				if (!CampaignReady())
					return "CAMPAIGN_NOT_READY";

				List<object> characters = GetAllCharacters().Where(x => x != null).ToList();
				List<ScanResult> bad = new List<ScanResult>();
				int heroCount = 0;
				int regularCount = 0;

				Log("===== CHARACTER SCAN START =====");
				Log("Characters enumerated: " + characters.Count);

				foreach (object c in characters)
				{
					ScanResult r = InspectCharacter(c);
					if (r.IsHero) heroCount++; else regularCount++;
					if (r.Flags.Count > 0)
					{
						bad.Add(r);
						Log(r.ToLogLine());
					}
				}

				Log("Summary: heroes=" + heroCount + ", regular=" + regularCount + ", flagged=" + bad.Count);
				Log("===== CHARACTER SCAN END =====");

				string top = bad.Count == 0 ? "none" : string.Join(", ", bad.Take(20).Select(x => x.Id + "[" + string.Join("+", x.Flags.ToArray()) + "]").ToArray());
				return "CHAR_SCAN total=" + characters.Count + ", heroes=" + heroCount + ", regular=" + regularCount + ", flagged=" + bad.Count + ". " + top;
			}
			catch (Exception ex)
			{
				Log("CHAR_SCAN ERROR: " + Flatten(ex));
				return "CHAR_SCAN ERROR: " + Flatten(ex);
			}
		}

		public static string ScanAllPartyRosters()
		{
			try
			{
				if (!CampaignReady())
					return "CAMPAIGN_NOT_READY";

				IEnumerable parties = GetAllParties();
				if (parties == null)
					return "PARTY_ENUMERATION_FAILED";

				int partyCount = 0;
				int rosterEntries = 0;
				List<string> badHits = new List<string>();
				HashSet<string> uniqueBad = new HashSet<string>(StringComparer.Ordinal);

				Log("===== PARTY ROSTER SCAN START =====");
				foreach (object party in parties)
				{
					if (party == null) continue;
					partyCount++;
					string partyId = GetStringId(party);

					foreach (string rosterName in new[] { "MemberRoster", "PrisonRoster" })
					{
						object roster = GetProperty(party, rosterName);
						if (roster == null) continue;
						foreach (object character in EnumerateRosterCharacters(roster))
						{
							if (character == null) continue;
							rosterEntries++;
							ScanResult r = InspectCharacter(character);
							if (r.Flags.Count == 0) continue;
							uniqueBad.Add(r.Id);
							string hit = "party=" + partyId + " roster=" + rosterName + " char=" + r.Id + " flags=" + string.Join("+", r.Flags.ToArray());
							badHits.Add(hit);
							Log("BAD_ROSTER_ENTRY " + hit);
						}
					}
				}

				Log("Summary: parties=" + partyCount + ", rosterEntries=" + rosterEntries + ", badHits=" + badHits.Count + ", uniqueBad=" + uniqueBad.Count);
				Log("===== PARTY ROSTER SCAN END =====");

				string sample = badHits.Count == 0 ? "none" : string.Join(" | ", badHits.Take(12).ToArray());
				return "PARTY_SCAN parties=" + partyCount + ", entries=" + rosterEntries + ", badHits=" + badHits.Count + ", uniqueBad=" + uniqueBad.Count + ". " + sample;
			}
			catch (Exception ex)
			{
				Log("PARTY_SCAN ERROR: " + Flatten(ex));
				return "PARTY_SCAN ERROR: " + Flatten(ex);
			}
		}

		public static string ScanOne(string id)
		{
			try
			{
				object c = GetAllCharacters().FirstOrDefault(x => string.Equals(GetStringId(x), id, StringComparison.Ordinal));
				if (c == null)
					return "NOT_FOUND: " + id;
				ScanResult r = InspectCharacter(c);
				Log("MANUAL_SCAN " + r.ToLogLine());
				return r.ToLogLine();
			}
			catch (Exception ex)
			{
				return "SCAN_ONE ERROR: " + Flatten(ex);
			}
		}

		private static ScanResult InspectCharacter(object c)
		{
			ScanResult r = new ScanResult();
			r.Id = GetStringId(c);

			FieldInfo heroField = FindField(c.GetType(), "_heroObject");
			object hero = SafeFieldGet(heroField, c);
			r.IsHero = hero != null;

			FieldInfo defaultSkillsField = FindField(c.GetType(), "DefaultCharacterSkills");
			object defaultSkills = SafeFieldGet(defaultSkillsField, c);
			r.DefaultSkillsState = defaultSkillsField == null ? "FIELD_NOT_FOUND" : (defaultSkills == null ? "NULL" : "OK");

			if (defaultSkillsField == null)
				r.Flags.Add("DEFAULT_SKILLS_FIELD_MISSING");
			else if (defaultSkills == null)
				r.Flags.Add("DEFAULT_SKILLS_NULL");
			else
			{
				object innerSkills = GetProperty(defaultSkills, "Skills");
				if (innerSkills == null)
					r.Flags.Add("DEFAULT_SKILLS_INNER_NULL");
			}

			FieldInfo originField = FindField(c.GetType(), "_originCharacter");
			object origin = SafeFieldGet(originField, c);
			r.OriginId = origin == null ? "null" : GetStringId(origin);

			if (r.IsHero)
			{
				object heroCharacter = GetProperty(hero, "CharacterObject");
				if (heroCharacter == null)
					r.Flags.Add("HERO_CHARACTEROBJECT_NULL");
				else if (!object.ReferenceEquals(heroCharacter, c))
					r.Flags.Add("HERO_CHARACTEROBJECT_MISMATCH");

				FieldInfo heroSkillsField = FindField(hero.GetType(), "_heroSkills");
				object heroSkills = SafeFieldGet(heroSkillsField, hero);
				r.HeroSkillsState = heroSkillsField == null ? "FIELD_NOT_FOUND" : (heroSkills == null ? "NULL" : "OK");

				// A null Hero._heroSkills is tolerated by vanilla Hero.GetSkillValue(), so log it
				// diagnostically but do not classify it as a fatal CharacterObject error by itself.
				if (originField != null && origin == null)
					r.Flags.Add("HERO_ORIGIN_NULL");
			}
			else
			{
				r.HeroSkillsState = "n/a";
			}

			return r;
		}

		private static IEnumerable<object> EnumerateRosterCharacters(object roster)
		{
			MethodInfo m = roster.GetType().GetMethod("GetTroopRoster", InstanceFlags);
			IEnumerable entries = null;
			try { entries = m == null ? null : m.Invoke(roster, null) as IEnumerable; } catch { }
			if (entries == null) yield break;

			foreach (object entry in entries)
			{
				if (entry == null) continue;
				object character = GetProperty(entry, "Character");
				if (character != null) yield return character;
			}
		}

		private static IEnumerable<object> GetAllCharacters()
		{
			Type characterType = FindType("TaleWorlds.CampaignSystem.CharacterObject");
			object all = GetStaticMember(characterType, "All");
			if (all is IEnumerable)
			{
				foreach (object c in (IEnumerable)all) yield return c;
				yield break;
			}

			object campaign = GetStaticMember(FindType("TaleWorlds.CampaignSystem.Campaign"), "Current");
			IEnumerable chars = campaign == null ? null : GetProperty(campaign, "Characters") as IEnumerable;
			if (chars != null)
				foreach (object c in chars) yield return c;
		}

		private static IEnumerable GetAllParties()
		{
			Type mobilePartyType = FindType("TaleWorlds.CampaignSystem.Party.MobileParty");
			object all = GetStaticMember(mobilePartyType, "All");
			if (all is IEnumerable) return (IEnumerable)all;

			object campaign = GetStaticMember(FindType("TaleWorlds.CampaignSystem.Campaign"), "Current");
			return campaign == null ? null : GetProperty(campaign, "MobileParties") as IEnumerable;
		}

		private static bool CampaignReady()
		{
			return GetStaticMember(FindType("TaleWorlds.CampaignSystem.Campaign"), "Current") != null;
		}

		private static FieldInfo FindField(Type type, string name)
		{
			for (Type t = type; t != null; t = t.BaseType)
			{
				FieldInfo f = t.GetField(name, InstanceFlags);
				if (f != null) return f;
			}
			return null;
		}

		private static object SafeFieldGet(FieldInfo field, object obj)
		{
			if (field == null || obj == null) return null;
			try { return field.GetValue(obj); } catch { return null; }
		}

		private static object GetProperty(object obj, string name)
		{
			if (obj == null) return null;
			PropertyInfo p = obj.GetType().GetProperty(name, InstanceFlags | BindingFlags.Static);
			if (p == null) return null;
			try { return p.GetValue(obj, null); } catch { return null; }
		}

		private static object GetStaticMember(Type type, string name)
		{
			if (type == null) return null;
			PropertyInfo p = type.GetProperty(name, StaticFlags);
			if (p != null) try { return p.GetValue(null, null); } catch { }
			FieldInfo f = type.GetField(name, StaticFlags);
			if (f != null) try { return f.GetValue(null); } catch { }
			return null;
		}

		private static Type FindType(string fullName)
		{
			foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
			{
				try
				{
					Type t = a.GetType(fullName, false);
					if (t != null) return t;
				}
				catch { }
			}
			return null;
		}

		private static string GetStringId(object obj)
		{
			if (obj == null) return "<null>";
			object id = GetProperty(obj, "StringId");
			return id == null ? "<null-id>" : id.ToString();
		}

		private static string Flatten(Exception ex)
		{
			if (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
			return ex.GetType().Name + ": " + ex.Message;
		}

		public static void Log(string message)
		{
			try
			{
				string baseDir = AppDomain.CurrentDomain.BaseDirectory;
				string root = Directory.GetParent(baseDir)?.Parent?.FullName ?? baseDir;
				string moduleDir = Path.Combine(root, "Modules", "AelfeyjaRescue");
				Directory.CreateDirectory(moduleDir);
				File.AppendAllText(Path.Combine(moduleDir, "character_scan.log"), "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + message + Environment.NewLine);
			}
			catch { }
		}

		private sealed class ScanResult
		{
			public string Id;
			public bool IsHero;
			public string DefaultSkillsState;
			public string HeroSkillsState;
			public string OriginId;
			public readonly List<string> Flags = new List<string>();

			public string ToLogLine()
			{
				return "CHAR id=" + Id +
					" isHero=" + IsHero +
					" defaultSkills=" + DefaultSkillsState +
					" heroSkills=" + HeroSkillsState +
					" origin=" + OriginId +
					" flags=" + (Flags.Count == 0 ? "OK" : string.Join("+", Flags.ToArray()));
			}
		}
	}
}
