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
			CharacterScanner.Log("Character Crash Scanner v1.3 loaded. Read-only scanner; no repairs or deletions.");
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
				int scannerWarnings = 0;

				Log("===== CHARACTER SCAN v1.3 START =====");
				Log("Characters enumerated: " + characters.Count);

				foreach (object c in characters)
				{
					ScanResult r = InspectCharacter(c);
					if (r.IsHero) heroCount++; else regularCount++;
					if (r.ScannerWarning) scannerWarnings++;
					if (r.Flags.Count > 0)
					{
						bad.Add(r);
						Log("BAD_CHARACTER " + r.ToLogLine());
					}
				}

				Log("Summary: heroes=" + heroCount + ", regular=" + regularCount + ", bad=" + bad.Count + ", scannerWarnings=" + scannerWarnings);
				Log("===== CHARACTER SCAN v1.3 END =====");

				string top = bad.Count == 0 ? "none" : string.Join(", ", bad.Take(25).Select(x => x.Id + "[" + string.Join("+", x.Flags.ToArray()) + "]").ToArray());
				return "CHAR_SCAN_V13 total=" + characters.Count + ", heroes=" + heroCount + ", regular=" + regularCount + ", bad=" + bad.Count + ", warnings=" + scannerWarnings + ". " + top;
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
				int rosterCount = 0;
				int rosterFailures = 0;
				int rosterEntries = 0;
				List<string> badHits = new List<string>();
				HashSet<string> uniqueBad = new HashSet<string>(StringComparer.Ordinal);

				Log("===== PARTY ROSTER SCAN v1.3 START =====");
				foreach (object party in parties)
				{
					if (party == null) continue;
					partyCount++;
					string partyId = GetStringId(party);

					foreach (string rosterName in new[] { "MemberRoster", "PrisonRoster" })
					{
						object roster = GetRosterFromParty(party, rosterName);
						if (roster == null)
						{
							rosterFailures++;
							Log("ROSTER_MISSING party=" + partyId + " roster=" + rosterName);
							continue;
						}

						rosterCount++;
						List<object> entries;
						string enumError;
						if (!TryEnumerateRosterEntries(roster, out entries, out enumError))
						{
							rosterFailures++;
							Log("ROSTER_ENUM_FAILED party=" + partyId + " roster=" + rosterName + " error=" + enumError);
							continue;
						}

						foreach (object entry in entries)
						{
							object character = GetMember(entry, "Character");
							if (character == null)
							{
								Log("ROSTER_ENTRY_CHARACTER_NULL party=" + partyId + " roster=" + rosterName);
								continue;
							}

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

				Log("Summary: parties=" + partyCount + ", rosters=" + rosterCount + ", rosterFailures=" + rosterFailures + ", rosterEntries=" + rosterEntries + ", badHits=" + badHits.Count + ", uniqueBad=" + uniqueBad.Count);
				Log("===== PARTY ROSTER SCAN v1.3 END =====");

				string sample = badHits.Count == 0 ? "none" : string.Join(" | ", badHits.Take(15).ToArray());
				return "PARTY_SCAN_V13 parties=" + partyCount + ", rosters=" + rosterCount + ", failures=" + rosterFailures + ", entries=" + rosterEntries + ", badHits=" + badHits.Count + ", uniqueBad=" + uniqueBad.Count + ". " + sample;
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
			object isHeroProperty = GetProperty(c, "IsHero");
			r.IsHero = hero != null || (isHeroProperty is bool && (bool)isHeroProperty);

			FieldInfo originField = FindField(c.GetType(), "_originCharacter");
			object origin = SafeFieldGet(originField, c);
			r.OriginId = origin == null ? "null/original" : GetStringId(origin);

			if (r.IsHero)
			{
				// Null origin is NORMAL for original heroes and is not an error.
				if (hero == null)
				{
					r.Flags.Add("ISHERO_TRUE_BUT_HEROOBJECT_NULL");
					return r;
				}

				object heroCharacter = GetProperty(hero, "CharacterObject");
				if (heroCharacter == null)
					r.Flags.Add("HERO_CHARACTEROBJECT_NULL");
				else if (!object.ReferenceEquals(heroCharacter, c))
					r.Flags.Add("HERO_CHARACTEROBJECT_MISMATCH");

				FieldInfo heroSkillsField = FindField(hero.GetType(), "_heroSkills");
				object heroSkills = SafeFieldGet(heroSkillsField, hero);
				r.HeroSkillsState = heroSkillsField == null ? "FIELD_NOT_FOUND" : (heroSkills == null ? "NULL_TOLERATED_BY_VANILLA" : "OK");
				r.DefaultSkillsState = "not_used_for_hero_GetSkillValue";
				return r;
			}

			// This is the path implicated by the crash dump: regular CharacterObject.GetSkillValue
			// dereferences DefaultCharacterSkills.Skills without the hero safety path.
			FieldInfo defaultSkillsField = FindField(c.GetType(), "DefaultCharacterSkills");
			if (defaultSkillsField == null)
			{
				r.ScannerWarning = true;
				r.DefaultSkillsState = "FIELD_NOT_FOUND";
				return r;
			}

			object defaultSkills = SafeFieldGet(defaultSkillsField, c);
			if (defaultSkills == null)
			{
				r.DefaultSkillsState = "NULL";
				r.Flags.Add("REGULAR_DEFAULT_SKILLS_NULL");
				return r;
			}

			object innerSkills = GetMember(defaultSkills, "Skills");
			if (innerSkills == null)
			{
				r.DefaultSkillsState = "INNER_SKILLS_NULL";
				r.Flags.Add("REGULAR_DEFAULT_SKILLS_INNER_NULL");
			}
			else
			{
				r.DefaultSkillsState = "OK";
			}
			r.HeroSkillsState = "n/a";
			return r;
		}

		private static object GetRosterFromParty(object party, string rosterName)
		{
			object roster = GetProperty(party, rosterName);
			if (roster != null) return roster;

			object partyBase = GetProperty(party, "Party");
			if (partyBase != null)
			{
				roster = GetProperty(partyBase, rosterName);
				if (roster != null) return roster;
			}
			return null;
		}

		private static bool TryEnumerateRosterEntries(object roster, out List<object> entries, out string error)
		{
			entries = new List<object>();
			error = null;
			try
			{
				int count = GetIntMember(roster, "Count", "_count");
				if (count < 0)
				{
					error = "COUNT_NOT_FOUND";
					return false;
				}

				MethodInfo getter = roster.GetType().GetMethods(InstanceFlags)
					.FirstOrDefault(m => m.Name == "GetElementCopyAtIndex" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(int));

				if (getter != null)
				{
					for (int i = 0; i < count; i++)
					{
						object entry = getter.Invoke(roster, new object[] { i });
						if (entry != null) entries.Add(entry);
					}
					return true;
				}

				FieldInfo dataField = FindField(roster.GetType(), "data") ?? FindField(roster.GetType(), "_data");
				object data = SafeFieldGet(dataField, roster);
				Array array = data as Array;
				if (array == null)
				{
					error = "NO_GETELEMENT_AND_NO_DATA_ARRAY";
					return false;
				}

				int limit = Math.Min(count, array.Length);
				for (int i = 0; i < limit; i++)
				{
					object entry = array.GetValue(i);
					if (entry != null) entries.Add(entry);
				}
				return true;
			}
			catch (Exception ex)
			{
				error = Flatten(ex);
				return false;
			}
		}

		private static int GetIntMember(object obj, string propertyName, string fieldName)
		{
			object p = GetProperty(obj, propertyName);
			if (p is int) return (int)p;
			FieldInfo f = FindField(obj.GetType(), fieldName);
			object v = SafeFieldGet(f, obj);
			return v is int ? (int)v : -1;
		}

		private static IEnumerable<object> GetAllCharacters()
		{
			Type characterType = FindType("TaleWorlds.CampaignSystem.CharacterObject");
			object all = GetStaticMember(characterType, "All");
			if (all is IEnumerable)
			{
				foreach (object c in (IEnumerable)all)
					yield return c;
				yield break;
			}

			object campaign = GetStaticMember(FindType("TaleWorlds.CampaignSystem.Campaign"), "Current");
			IEnumerable chars = campaign == null ? null : GetProperty(campaign, "Characters") as IEnumerable;
			if (chars != null)
				foreach (object c in chars)
					yield return c;
		}

		private static IEnumerable GetAllParties()
		{
			Type mobilePartyType = FindType("TaleWorlds.CampaignSystem.Party.MobileParty");
			object all = GetStaticMember(mobilePartyType, "All");
			if (all is IEnumerable) return (IEnumerable)all;

			object campaign = GetStaticMember(FindType("TaleWorlds.CampaignSystem.Campaign"), "Current");
			if (campaign == null) return null;
			return GetProperty(campaign, "MobileParties") as IEnumerable;
		}

		private static bool CampaignReady()
		{
			Type campaignType = FindType("TaleWorlds.CampaignSystem.Campaign");
			return GetStaticMember(campaignType, "Current") != null;
		}

		private static string GetStringId(object obj)
		{
			if (obj == null) return "<null>";
			object id = GetMember(obj, "StringId");
			return id == null ? "<no-id>" : id.ToString();
		}

		private static object GetMember(object obj, string name)
		{
			if (obj == null) return null;
			object value = GetProperty(obj, name);
			if (value != null) return value;
			FieldInfo f = FindField(obj.GetType(), name);
			return SafeFieldGet(f, obj);
		}

		private static object GetProperty(object obj, string name)
		{
			if (obj == null) return null;
			try
			{
				PropertyInfo p = obj.GetType().GetProperty(name, InstanceFlags);
				return p == null ? null : p.GetValue(obj, null);
			}
			catch { return null; }
		}

		private static object GetStaticMember(Type type, string name)
		{
			if (type == null) return null;
			try
			{
				PropertyInfo p = type.GetProperty(name, StaticFlags);
				if (p != null) return p.GetValue(null, null);
			}
			catch { }
			try
			{
				FieldInfo f = type.GetField(name, StaticFlags);
				if (f != null) return f.GetValue(null);
			}
			catch { }
			return null;
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

		private static string Flatten(Exception ex)
		{
			while (ex is TargetInvocationException && ex.InnerException != null)
				ex = ex.InnerException;
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
			public string Id = "<unknown>";
			public bool IsHero;
			public bool ScannerWarning;
			public string DefaultSkillsState = "unknown";
			public string HeroSkillsState = "unknown";
			public string OriginId = "unknown";
			public readonly List<string> Flags = new List<string>();

			public string ToLogLine()
			{
				return "id=" + Id +
					" isHero=" + IsHero +
					" defaultSkills=" + DefaultSkillsState +
					" heroSkills=" + HeroSkillsState +
					" origin=" + OriginId +
					" flags=" + (Flags.Count == 0 ? "none" : string.Join("+", Flags.ToArray()));
			}
		}
	}
}
