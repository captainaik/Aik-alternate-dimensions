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
			CharacterScanner.Log("Character Crash Scanner v1.4 loaded. Repair is manual only.");
		}
	}

	public static class RescueCommands
	{
		[CommandLineFunctionality.CommandLineArgumentFunction("scan_chars", "rescue")]
		public static string ScanChars(List<string> args) => CharacterScanner.ScanAllCharacters();

		[CommandLineFunctionality.CommandLineArgumentFunction("scan_parties", "rescue")]
		public static string ScanParties(List<string> args) => CharacterScanner.ScanAllPartyRosters();

		[CommandLineFunctionality.CommandLineArgumentFunction("scan_all", "rescue")]
		public static string ScanAll(List<string> args)
		{
			return CharacterScanner.ScanAllCharacters() + " | " + CharacterScanner.ScanAllPartyRosters();
		}

		[CommandLineFunctionality.CommandLineArgumentFunction("scan_char", "rescue")]
		public static string ScanChar(List<string> args)
		{
			if (args == null || args.Count == 0)
				return "Usage: rescue.scan_char <CharacterObject StringId>";
			return CharacterScanner.ScanOne(args[0]);
		}

		[CommandLineFunctionality.CommandLineArgumentFunction("repair_rosters", "rescue")]
		public static string RepairRosters(List<string> args) => CharacterScanner.RepairRosterCharacterReferences();

		[CommandLineFunctionality.CommandLineArgumentFunction("log_path", "rescue")]
		public static string LogPath(List<string> args) => CharacterScanner.GetLogPathPublic();
	}

	internal static class CharacterScanner
	{
		private static readonly BindingFlags InstanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
		private static readonly BindingFlags StaticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

		public static string ScanAllCharacters()
		{
			try
			{
				if (!CampaignReady()) return "CAMPAIGN_NOT_READY";
				List<object> characters = GetAllCharacters().Where(x => x != null).ToList();
				List<ScanResult> bad = new List<ScanResult>();
				int heroes = 0, regulars = 0, warnings = 0;
				Log("===== CHARACTER SCAN v1.4 START =====");
				foreach (object c in characters)
				{
					ScanResult r = InspectCharacter(c);
					if (r.IsHero) heroes++; else regulars++;
					if (r.ScannerWarning) warnings++;
					if (r.Flags.Count > 0)
					{
						bad.Add(r);
						Log("BAD_CHARACTER " + r.ToLogLine());
					}
				}
				Log("Summary: total=" + characters.Count + ", heroes=" + heroes + ", regular=" + regulars + ", bad=" + bad.Count + ", warnings=" + warnings);
				Log("===== CHARACTER SCAN v1.4 END =====");
				string sample = bad.Count == 0 ? "none" : string.Join(", ", bad.Take(20).Select(x => x.Id + "[" + string.Join("+", x.Flags.ToArray()) + "]").ToArray());
				return "CHAR_SCAN_V14 total=" + characters.Count + ", heroes=" + heroes + ", regular=" + regulars + ", bad=" + bad.Count + ", warnings=" + warnings + ". " + sample;
			}
			catch (Exception ex)
			{
				return "CHAR_SCAN ERROR: " + Flatten(ex);
			}
		}

		public static string ScanAllPartyRosters()
		{
			try
			{
				if (!CampaignReady()) return "CAMPAIGN_NOT_READY";
				IEnumerable parties = GetAllParties();
				if (parties == null) return "PARTY_ENUMERATION_FAILED";

				int partyCount = 0, rosterCount = 0, failures = 0, entries = 0;
				List<string> hits = new List<string>();
				HashSet<string> uniqueBad = new HashSet<string>(StringComparer.Ordinal);
				Log("===== PARTY ROSTER SCAN v1.4 START =====");

				foreach (object party in parties)
				{
					if (party == null) continue;
					partyCount++;
					string partyId = GetStringId(party);
					foreach (string rosterName in new[] { "MemberRoster", "PrisonRoster" })
					{
						object roster = GetRosterFromParty(party, rosterName);
						if (roster == null) { failures++; continue; }
						rosterCount++;
						Array data;
						int count;
						string error;
						if (!TryGetRosterData(roster, out data, out count, out error))
						{
							failures++;
							Log("ROSTER_ENUM_FAILED party=" + partyId + " roster=" + rosterName + " error=" + error);
							continue;
						}

						for (int i = 0; i < count; i++)
						{
							object entry = data.GetValue(i);
							object character = GetMember(entry, "Character");
							if (character == null) continue;
							entries++;
							ScanResult r = InspectCharacter(character);
							if (!HasFatalRegularSkillFlag(r)) continue;
							uniqueBad.Add(r.Id);
							string hit = "party=" + partyId + " roster=" + rosterName + " char=" + r.Id + " flags=" + string.Join("+", r.Flags.ToArray());
							hits.Add(hit);
							Log("BAD_ROSTER_ENTRY " + hit);
						}
					}
				}

				Log("Summary: parties=" + partyCount + ", rosters=" + rosterCount + ", failures=" + failures + ", entries=" + entries + ", badHits=" + hits.Count + ", uniqueBad=" + uniqueBad.Count);
				Log("===== PARTY ROSTER SCAN v1.4 END =====");
				string sample = hits.Count == 0 ? "none" : string.Join(" | ", hits.Take(12).ToArray());
				return "PARTY_SCAN_V14 parties=" + partyCount + ", rosters=" + rosterCount + ", failures=" + failures + ", entries=" + entries + ", badHits=" + hits.Count + ", uniqueBad=" + uniqueBad.Count + ". " + sample;
			}
			catch (Exception ex)
			{
				return "PARTY_SCAN ERROR: " + Flatten(ex);
			}
		}

		public static string ScanOne(string id)
		{
			try
			{
				object c = GetAllCharacters().FirstOrDefault(x => string.Equals(GetStringId(x), id, StringComparison.Ordinal));
				if (c == null) return "NOT_FOUND: " + id;
				ScanResult r = InspectCharacter(c);
				Log("MANUAL_SCAN " + r.ToLogLine());
				return r.ToLogLine();
			}
			catch (Exception ex)
			{
				return "SCAN_ONE ERROR: " + Flatten(ex);
			}
		}

		public static string RepairRosterCharacterReferences()
		{
			try
			{
				if (!CampaignReady()) return "CAMPAIGN_NOT_READY";

				// Canonical CharacterObject.All entries are the safe donors. We only use regular
				// characters whose own DefaultCharacterSkills chain is healthy.
				Dictionary<string, object> canonical = new Dictionary<string, object>(StringComparer.Ordinal);
				foreach (object c in GetAllCharacters())
				{
					if (c == null) continue;
					ScanResult r = InspectCharacter(c);
					if (r.IsHero || HasFatalRegularSkillFlag(r)) continue;
					string id = GetStringId(c);
					if (!canonical.ContainsKey(id)) canonical[id] = c;
				}

				IEnumerable parties = GetAllParties();
				if (parties == null) return "PARTY_ENUMERATION_FAILED";

				int badSeen = 0, replaced = 0, merged = 0, unresolved = 0, rostersChanged = 0;
				HashSet<string> repairedIds = new HashSet<string>(StringComparer.Ordinal);
				List<string> unresolvedIds = new List<string>();
				Log("===== ROSTER REPAIR v1.4 START =====");

				foreach (object party in parties)
				{
					if (party == null) continue;
					string partyId = GetStringId(party);
					foreach (string rosterName in new[] { "MemberRoster", "PrisonRoster" })
					{
						object roster = GetRosterFromParty(party, rosterName);
						if (roster == null) continue;
						Array data;
						int count;
						string error;
						if (!TryGetRosterData(roster, out data, out count, out error)) continue;
						bool changed = false;
						int i = 0;
						while (i < count)
						{
							object entry = data.GetValue(i);
							object badCharacter = GetMember(entry, "Character");
							if (badCharacter == null) { i++; continue; }
							ScanResult badResult = InspectCharacter(badCharacter);
							if (!HasFatalRegularSkillFlag(badResult)) { i++; continue; }
							badSeen++;
							object donor;
							if (!canonical.TryGetValue(badResult.Id, out donor) || donor == null || object.ReferenceEquals(donor, badCharacter))
							{
								unresolved++;
								unresolvedIds.Add(badResult.Id);
								Log("UNRESOLVED party=" + partyId + " roster=" + rosterName + " char=" + badResult.Id);
								i++;
								continue;
							}

							// If the canonical troop is already present in this roster, merge the bad stack
							// into it before removing the malformed duplicate. Otherwise simply replace the
							// Character reference; counts/wounded/XP remain untouched.
							int existing = FindCharacterReferenceIndex(data, count, donor, i);
							if (existing >= 0)
							{
								if (!MergeEntryIntoCanonicalAndRemove(roster, data, existing, i))
								{
									unresolved++;
									unresolvedIds.Add(badResult.Id);
									i++;
									continue;
								}
								merged++;
								count--;
								changed = true;
								repairedIds.Add(badResult.Id);
								Log("MERGED_BAD_STACK party=" + partyId + " roster=" + rosterName + " char=" + badResult.Id);
								continue; // element at i was removed; inspect the shifted element now at i
							}

							FieldInfo characterField = FindField(entry.GetType(), "Character");
							if (characterField == null)
							{
								unresolved++;
								unresolvedIds.Add(badResult.Id);
								i++;
								continue;
							}
							characterField.SetValue(entry, donor);
							data.SetValue(entry, i);
							replaced++;
							changed = true;
							repairedIds.Add(badResult.Id);
							Log("REPLACED_BAD_REFERENCE party=" + partyId + " roster=" + rosterName + " char=" + badResult.Id);
							i++;
						}

						if (changed)
						{
							rostersChanged++;
							InvokeNoArg(roster, "UpdateVersion");
						}
					}
				}

				string verify = ScanAllPartyRosters();
				string result = "REPAIR_V14 badSeen=" + badSeen + ", replaced=" + replaced + ", merged=" + merged + ", rostersChanged=" + rostersChanged + ", uniqueIds=" + repairedIds.Count + ", unresolved=" + unresolved + ". IDs=" + (repairedIds.Count == 0 ? "none" : string.Join(",", repairedIds.Take(30).ToArray())) + ". VERIFY: " + verify;
				Log(result);
				Log("===== ROSTER REPAIR v1.4 END =====");
				return result;
			}
			catch (Exception ex)
			{
				string result = "REPAIR ERROR: " + Flatten(ex);
				Log(result);
				return result;
			}
		}

		private static bool MergeEntryIntoCanonicalAndRemove(object roster, Array data, int canonicalIndex, int badIndex)
		{
			try
			{
				object goodEntry = data.GetValue(canonicalIndex);
				object badEntry = data.GetValue(badIndex);
				FieldInfo numberField = FindField(goodEntry.GetType(), "_number");
				FieldInfo woundedField = FindField(goodEntry.GetType(), "_woundedNumber");
				FieldInfo xpField = FindField(goodEntry.GetType(), "_xp");
				if (numberField == null || woundedField == null || xpField == null) return false;

				int goodN = SafeInt(numberField.GetValue(goodEntry));
				int goodW = SafeInt(woundedField.GetValue(goodEntry));
				int goodXp = SafeInt(xpField.GetValue(goodEntry));
				int badN = SafeInt(numberField.GetValue(badEntry));
				int badW = SafeInt(woundedField.GetValue(badEntry));
				int badXp = SafeInt(xpField.GetValue(badEntry));
				numberField.SetValue(goodEntry, goodN + badN);
				woundedField.SetValue(goodEntry, goodW + badW);
				xpField.SetValue(goodEntry, goodXp + badXp);
				data.SetValue(goodEntry, canonicalIndex);

				MethodInfo remove = roster.GetType().GetMethod("RemoveRange", InstanceFlags, null, new[] { typeof(int), typeof(int) }, null);
				if (remove == null) return false;
				remove.Invoke(roster, new object[] { badIndex, badIndex + 1 });
				return true;
			}
			catch { return false; }
		}

		private static int FindCharacterReferenceIndex(Array data, int count, object target, int skip)
		{
			for (int i = 0; i < count; i++)
			{
				if (i == skip) continue;
				object entry = data.GetValue(i);
				object c = GetMember(entry, "Character");
				if (object.ReferenceEquals(c, target)) return i;
			}
			return -1;
		}

		private static bool HasFatalRegularSkillFlag(ScanResult r)
		{
			if (r == null || r.IsHero) return false;
			return r.Flags.Contains("REGULAR_DEFAULT_SKILLS_NULL") || r.Flags.Contains("REGULAR_DEFAULT_SKILLS_INNER_NULL");
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
				if (hero == null)
				{
					r.Flags.Add("ISHERO_TRUE_BUT_HEROOBJECT_NULL");
					return r;
				}
				object heroCharacter = GetProperty(hero, "CharacterObject");
				if (heroCharacter == null) r.Flags.Add("HERO_CHARACTEROBJECT_NULL");
				else if (!object.ReferenceEquals(heroCharacter, c)) r.Flags.Add("HERO_CHARACTEROBJECT_MISMATCH");
				FieldInfo heroSkillsField = FindField(hero.GetType(), "_heroSkills");
				object heroSkills = SafeFieldGet(heroSkillsField, hero);
				r.HeroSkillsState = heroSkillsField == null ? "FIELD_NOT_FOUND" : (heroSkills == null ? "NULL_TOLERATED_BY_VANILLA" : "OK");
				r.DefaultSkillsState = "not_used_for_hero_GetSkillValue";
				return r;
			}

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
			else r.DefaultSkillsState = "OK";
			r.HeroSkillsState = "n/a";
			return r;
		}

		private static object GetRosterFromParty(object party, string rosterName)
		{
			object roster = GetProperty(party, rosterName);
			if (roster != null) return roster;
			object partyBase = GetProperty(party, "Party");
			return partyBase == null ? null : GetProperty(partyBase, rosterName);
		}

		private static bool TryGetRosterData(object roster, out Array data, out int count, out string error)
		{
			data = null;
			count = -1;
			error = null;
			try
			{
				count = GetIntMember(roster, "Count", "_count");
				if (count < 0) { error = "COUNT_NOT_FOUND"; return false; }
				FieldInfo dataField = FindField(roster.GetType(), "data") ?? FindField(roster.GetType(), "_data");
				data = SafeFieldGet(dataField, roster) as Array;
				if (data == null) { error = "DATA_ARRAY_NOT_FOUND"; return false; }
				if (count > data.Length) { error = "COUNT_GT_ARRAY_LENGTH"; return false; }
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

		private static int SafeInt(object v) => v is int ? (int)v : 0;

		private static void InvokeNoArg(object obj, string methodName)
		{
			if (obj == null) return;
			try
			{
				MethodInfo m = obj.GetType().GetMethod(methodName, InstanceFlags, null, Type.EmptyTypes, null);
				if (m != null) m.Invoke(obj, null);
			}
			catch { }
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
			if (chars != null) foreach (object c in chars) yield return c;
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

		private static string GetStringId(object obj)
		{
			if (obj == null) return "<null>";
			object id = GetMember(obj, "StringId");
			return id == null ? "<no-id>" : id.ToString();
		}

		private static object GetMember(object obj, string name)
		{
			if (obj == null) return null;
			object p = GetProperty(obj, name);
			if (p != null) return p;
			return SafeFieldGet(FindField(obj.GetType(), name), obj);
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
			while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
			return ex.GetType().Name + ": " + ex.Message;
		}

		public static string GetLogPathPublic() => GetLogPath();

		private static string GetLogPath()
		{
			string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
			return Path.Combine(docs, "Mount and Blade II Bannerlord", "CharacterCrashScanner.log");
		}

		public static void Log(string message)
		{
			try
			{
				string path = GetLogPath();
				Directory.CreateDirectory(Path.GetDirectoryName(path));
				File.AppendAllText(path, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + message + Environment.NewLine);
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
				return "id=" + Id + " isHero=" + IsHero + " defaultSkills=" + DefaultSkillsState + " heroSkills=" + HeroSkillsState + " origin=" + OriginId + " flags=" + (Flags.Count == 0 ? "none" : string.Join("+", Flags.ToArray()));
			}
		}
	}
}
