using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AelfeyjaRescue
{
	public sealed class BattleForensicsSubModule : MBSubModuleBase
	{
		protected override void OnGameStart(Game game, IGameStarter gameStarter)
		{
			base.OnGameStart(game, gameStarter);
			if (gameStarter is CampaignGameStarter campaignGameStarter)
			{
				campaignGameStarter.AddBehavior(new BattleForensicsBehavior());
				BattleForensics.Log("Battle Forensics v1.6 behavior added.");
			}
		}
	}

	public static class BattleForensicsCommands
	{
		[CommandLineFunctionality.CommandLineArgumentFunction("battle_status", "rescue")]
		public static string BattleStatus(List<string> args) => BattleForensics.Status();

		[CommandLineFunctionality.CommandLineArgumentFunction("battle_log_path", "rescue")]
		public static string BattleLogPath(List<string> args) => BattleForensics.GetLogPath();

		[CommandLineFunctionality.CommandLineArgumentFunction("battle_clear_log", "rescue")]
		public static string BattleClearLog(List<string> args) => BattleForensics.ClearLog();

		[CommandLineFunctionality.CommandLineArgumentFunction("battle_snapshot_now", "rescue")]
		public static string BattleSnapshotNow(List<string> args) => BattleForensics.ManualSnapshot();
	}

	internal sealed class BattleForensicsBehavior : CampaignBehaviorBase
	{
		public override void RegisterEvents()
		{
			CampaignEvents.MapEventStarted.AddNonSerializedListener(this, OnMapEventStarted);
			CampaignEvents.MapEventEnded.AddNonSerializedListener(this, OnMapEventEnded);
			CampaignEvents.OnPlayerBattleEndEvent.AddNonSerializedListener(this, OnPlayerBattleEnd);
			CampaignEvents.TickEvent.AddNonSerializedListener(this, OnTick);
			BattleForensics.Log("Battle Forensics v1.6 events registered.");
		}

		public override void SyncData(IDataStore dataStore)
		{
		}

		private void OnMapEventStarted(MapEvent mapEvent)
		{
			BattleForensics.OnBattleStarted(mapEvent);
		}

		private void OnMapEventEnded(MapEvent mapEvent)
		{
			BattleForensics.OnBattleStage("MapEventEnded", mapEvent, scheduleDelayed: true);
		}

		private void OnPlayerBattleEnd(MapEvent mapEvent)
		{
			BattleForensics.OnBattleStage("OnPlayerBattleEndEvent", mapEvent, scheduleDelayed: true);
		}

		private void OnTick(float dt)
		{
			BattleForensics.OnTick(dt);
		}
	}

	internal static class BattleForensics
	{
		private static readonly BindingFlags InstanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
		private static readonly BindingFlags StaticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
		private static readonly object Sync = new object();
		private static WorldSnapshot _baseline;
		private static string _activeBattle = "none";
		private static string _lastSummary = "none";
		private static bool _pendingDelayed;
		private static float _elapsedAfterBattle;
		private static bool _didFastScan;
		private static bool _didOneSecondScan;
		private static bool _didFiveSecondScan;
		private static int _battleCounter;
		private static int _incidents;

		public static void OnBattleStarted(MapEvent mapEvent)
		{
			try
			{
				if (mapEvent == null || !SafeBool(mapEvent, "IsPlayerMapEvent")) return;
				lock (Sync)
				{
					_battleCounter++;
					_activeBattle = "battle-" + _battleCounter;
					_pendingDelayed = false;
					_elapsedAfterBattle = 0f;
					_didFastScan = _didOneSecondScan = _didFiveSecondScan = false;
					_baseline = CaptureWorldSnapshot();
					Log("===== PLAYER BATTLE START " + _activeBattle + " =====");
					Log("mapEvent_ref=" + RuntimeHelpers.GetHashCode(mapEvent));
					Log("participants=" + DescribeParticipants(mapEvent));
					LogSnapshotSummary("PRE_BATTLE", _baseline);
					Log("===== END PLAYER BATTLE START =====");
				}
			}
			catch (Exception ex)
			{
				Log("BATTLE_START_EXCEPTION " + Flatten(ex));
			}
		}

		public static void OnBattleStage(string stage, MapEvent mapEvent, bool scheduleDelayed)
		{
			try
			{
				if (mapEvent == null || !SafeBool(mapEvent, "IsPlayerMapEvent")) return;
				lock (Sync)
				{
					if (_baseline == null)
					{
						_baseline = CaptureWorldSnapshot();
						_activeBattle = _activeBattle == "none" ? "late-attach" : _activeBattle;
						Log("WARNING no pre-battle baseline existed; created late baseline at stage=" + stage);
					}
					AnalyzeStage(stage, mapEvent);
					if (scheduleDelayed)
					{
						_pendingDelayed = true;
						_elapsedAfterBattle = 0f;
						_didFastScan = _didOneSecondScan = _didFiveSecondScan = false;
					}
				}
			}
			catch (Exception ex)
			{
				Log("BATTLE_STAGE_EXCEPTION stage=" + stage + " error=" + Flatten(ex));
			}
		}

		public static void OnTick(float dt)
		{
			if (!_pendingDelayed || _baseline == null) return;
			lock (Sync)
			{
				_elapsedAfterBattle += Math.Max(0f, dt);
				if (!_didFastScan && _elapsedAfterBattle >= 0.10f)
				{
					_didFastScan = true;
					AnalyzeStage("PostBattle+0.1s", null);
				}
				if (!_didOneSecondScan && _elapsedAfterBattle >= 1.0f)
				{
					_didOneSecondScan = true;
					AnalyzeStage("PostBattle+1s", null);
				}
				if (!_didFiveSecondScan && _elapsedAfterBattle >= 5.0f)
				{
					_didFiveSecondScan = true;
					AnalyzeStage("PostBattle+5s", null);
					_pendingDelayed = false;
					_baseline = null;
					_activeBattle = "none";
				}
			}
		}

		private static void AnalyzeStage(string stage, MapEvent mapEvent)
		{
			WorldSnapshot now = CaptureWorldSnapshot();
			List<RosterEntrySnapshot> currentBad = now.Entries.Values.Where(x => x.Malformed).ToList();
			List<RosterEntrySnapshot> newBad = currentBad.Where(x => _baseline == null || !_baseline.Entries.ContainsKey(x.IdentityKey)).ToList();
			List<RosterEntrySnapshot> newNonCanonical = now.Entries.Values
				.Where(x => x.IsRegular && !x.IsCanonical && !x.Malformed && (_baseline == null || !_baseline.Entries.ContainsKey(x.IdentityKey)))
				.ToList();

			List<RosterEntrySnapshot> disappeared = _baseline == null
				? new List<RosterEntrySnapshot>()
				: _baseline.Entries.Values.Where(x => !now.Entries.ContainsKey(x.IdentityKey)).ToList();

			Log("===== BATTLE FORENSICS STAGE " + stage + " " + _activeBattle + " =====");
			if (mapEvent != null) Log("participants=" + DescribeParticipants(mapEvent));
			LogSnapshotSummary(stage, now);
			Log("delta newBad=" + newBad.Count + ", currentBad=" + currentBad.Count + ", newNonCanonical=" + newNonCanonical.Count + ", disappearedRefs=" + disappeared.Count);

			foreach (RosterEntrySnapshot e in newBad)
			{
				_incidents++;
				Log("NEW_BAD_AFTER_BATTLE " + e.ToLogLine());
			}
			foreach (RosterEntrySnapshot e in currentBad.Where(x => !newBad.Contains(x)).Take(100))
				Log("EXISTING_BAD_AFTER_BATTLE " + e.ToLogLine());
			foreach (RosterEntrySnapshot e in newNonCanonical.Take(100))
				Log("NEW_NONCANONICAL_AFTER_BATTLE " + e.ToLogLine());

			// Group by party+roster+character so a canonical->clone swap is obvious even when counts stay unchanged.
			if (_baseline != null)
			{
				var beforeGroups = _baseline.Entries.Values.GroupBy(x => x.StackKey).ToDictionary(g => g.Key, g => g.ToList());
				var afterGroups = now.Entries.Values.GroupBy(x => x.StackKey).ToDictionary(g => g.Key, g => g.ToList());
				foreach (string key in beforeGroups.Keys.Union(afterGroups.Keys).Distinct())
				{
					List<RosterEntrySnapshot> b = beforeGroups.ContainsKey(key) ? beforeGroups[key] : new List<RosterEntrySnapshot>();
					List<RosterEntrySnapshot> a = afterGroups.ContainsKey(key) ? afterGroups[key] : new List<RosterEntrySnapshot>();
					string beforeRefs = string.Join(",", b.Select(x => x.RefId.ToString()).OrderBy(x => x).ToArray());
					string afterRefs = string.Join(",", a.Select(x => x.RefId.ToString()).OrderBy(x => x).ToArray());
					int beforeCount = b.Sum(x => x.Number);
					int afterCount = a.Sum(x => x.Number);
					if (beforeRefs != afterRefs)
						Log("REF_CHANGE stack=" + key + " beforeRefs=[" + beforeRefs + "] afterRefs=[" + afterRefs + "] beforeCount=" + beforeCount + " afterCount=" + afterCount);
				}
			}

			_lastSummary = stage + ": newBad=" + newBad.Count + ", currentBad=" + currentBad.Count + ", newNonCanonical=" + newNonCanonical.Count;
			Log("===== END BATTLE FORENSICS STAGE =====");

			if (newBad.Count > 0)
			{
				try
				{
					InformationManager.DisplayMessage(new InformationMessage("[Battle Forensics] " + newBad.Count + " malformed roster reference(s) appeared after battle. Check BattleForensics.log."));
				}
				catch { }
			}
		}

		private static WorldSnapshot CaptureWorldSnapshot()
		{
			WorldSnapshot snapshot = new WorldSnapshot();
			Dictionary<string, object> canonical = BuildCanonicalMap();
			IEnumerable parties = GetAllParties();
			if (parties == null) return snapshot;
			foreach (object party in parties)
			{
				if (party == null) continue;
				snapshot.PartyCount++;
				string partyId = GetStringId(party);
				string partyName = SafeToString(GetProperty(party, "Name"));
				foreach (string rosterName in new[] { "MemberRoster", "PrisonRoster" })
				{
					object roster = GetRosterFromParty(party, rosterName);
					if (roster == null) continue;
					snapshot.RosterCount++;
					Array data;
					int count;
					if (!TryGetRosterData(roster, out data, out count)) continue;
					for (int i = 0; i < count; i++)
					{
						object element = data.GetValue(i);
						object character = GetMember(element, "Character");
						if (character == null) continue;
						string charId = GetStringId(character);
						int refId = RuntimeHelpers.GetHashCode(character);
						bool isHero = IsHero(character);
						bool malformed = !isHero && IsMalformedRegular(character);
						object canonicalObject;
						canonical.TryGetValue(charId, out canonicalObject);
						bool isCanonical = canonicalObject != null && object.ReferenceEquals(canonicalObject, character);
						RosterEntrySnapshot e = new RosterEntrySnapshot
						{
							PartyId = partyId,
							PartyName = partyName,
							Roster = rosterName,
							CharacterId = charId,
							RefId = refId,
							CanonicalRefId = canonicalObject == null ? 0 : RuntimeHelpers.GetHashCode(canonicalObject),
							IsRegular = !isHero,
							IsCanonical = isCanonical,
							Malformed = malformed,
							Number = ReadInt(element, "_number", "Number"),
							Wounded = ReadInt(element, "_woundedNumber", "WoundedNumber"),
							Xp = ReadInt(element, "_xp", "Xp")
						};
						snapshot.Entries[e.IdentityKey] = e;
						snapshot.EntryCount++;
						if (malformed) snapshot.BadCount++;
						if (e.IsRegular && !e.IsCanonical) snapshot.NonCanonicalCount++;
					}
				}
			}
			return snapshot;
		}

		private static Dictionary<string, object> BuildCanonicalMap()
		{
			Dictionary<string, object> map = new Dictionary<string, object>(StringComparer.Ordinal);
			object all = GetStaticMember(FindType("TaleWorlds.CampaignSystem.CharacterObject"), "All");
			IEnumerable enumerable = all as IEnumerable;
			if (enumerable == null) return map;
			foreach (object c in enumerable)
			{
				if (c == null || IsHero(c) || IsMalformedRegular(c)) continue;
				string id = GetStringId(c);
				if (!map.ContainsKey(id)) map[id] = c;
			}
			return map;
		}

		private static bool IsHero(object character)
		{
			object value = GetProperty(character, "IsHero");
			if (value is bool && (bool)value) return true;
			return SafeFieldGet(FindField(character == null ? null : character.GetType(), "_heroObject"), character) != null;
		}

		private static bool IsMalformedRegular(object character)
		{
			if (character == null || IsHero(character)) return false;
			FieldInfo field = FindField(character.GetType(), "DefaultCharacterSkills");
			if (field == null) return false;
			object defaultSkills = SafeFieldGet(field, character);
			if (defaultSkills == null) return true;
			return GetMember(defaultSkills, "Skills") == null;
		}

		private static IEnumerable GetAllParties()
		{
			Type mobilePartyType = FindType("TaleWorlds.CampaignSystem.Party.MobileParty");
			object all = GetStaticMember(mobilePartyType, "All");
			if (all is IEnumerable) return (IEnumerable)all;
			object campaign = GetStaticMember(FindType("TaleWorlds.CampaignSystem.Campaign"), "Current");
			return campaign == null ? null : GetProperty(campaign, "MobileParties") as IEnumerable;
		}

		private static object GetRosterFromParty(object party, string rosterName)
		{
			object roster = GetProperty(party, rosterName);
			if (roster != null) return roster;
			object partyBase = GetProperty(party, "Party");
			return partyBase == null ? null : GetProperty(partyBase, rosterName);
		}

		private static bool TryGetRosterData(object roster, out Array data, out int count)
		{
			data = null;
			count = -1;
			try
			{
				object countObj = GetProperty(roster, "Count") ?? SafeFieldGet(FindField(roster.GetType(), "_count"), roster);
				if (!(countObj is int)) return false;
				count = (int)countObj;
				FieldInfo dataField = FindField(roster.GetType(), "data") ?? FindField(roster.GetType(), "_data");
				data = SafeFieldGet(dataField, roster) as Array;
				return data != null && count >= 0 && count <= data.Length;
			}
			catch { return false; }
		}

		private static string DescribeParticipants(MapEvent mapEvent)
		{
			try
			{
				object involved = GetProperty(mapEvent, "InvolvedParties");
				IEnumerable enumerable = involved as IEnumerable;
				if (enumerable == null) return "<unavailable>";
				List<string> items = new List<string>();
				foreach (object partyBase in enumerable)
				{
					if (partyBase == null) continue;
					object mobile = GetProperty(partyBase, "MobileParty");
					object target = mobile ?? partyBase;
					items.Add(GetStringId(target) + "(" + SafeToString(GetProperty(target, "Name")) + ")");
				}
				return items.Count == 0 ? "none" : string.Join("; ", items.ToArray());
			}
			catch { return "<error>"; }
		}

		private static bool SafeBool(object obj, string property)
		{
			object value = GetProperty(obj, property);
			return value is bool && (bool)value;
		}

		private static int ReadInt(object obj, string fieldName, string propertyName)
		{
			object p = GetProperty(obj, propertyName);
			if (p is int) return (int)p;
			object f = SafeFieldGet(FindField(obj == null ? null : obj.GetType(), fieldName), obj);
			return f is int ? (int)f : 0;
		}

		private static object GetMember(object obj, string name)
		{
			object p = GetProperty(obj, name);
			if (p != null) return p;
			return SafeFieldGet(FindField(obj == null ? null : obj.GetType(), name), obj);
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

		private static string GetStringId(object obj)
		{
			if (obj == null) return "<null>";
			object id = GetMember(obj, "StringId");
			return id == null ? "<no-id>" : id.ToString();
		}

		private static string SafeToString(object obj)
		{
			try { return obj == null ? "" : obj.ToString(); } catch { return ""; }
		}

		private static string Flatten(Exception ex)
		{
			while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
			return ex.GetType().Name + ": " + ex.Message;
		}

		private static void LogSnapshotSummary(string label, WorldSnapshot snapshot)
		{
			Log(label + " parties=" + snapshot.PartyCount + ", rosters=" + snapshot.RosterCount + ", entries=" + snapshot.EntryCount + ", bad=" + snapshot.BadCount + ", nonCanonicalRegular=" + snapshot.NonCanonicalCount);
		}

		public static string Status()
		{
			return "BATTLE_FORENSICS_V16 active=" + (_baseline != null) + ", battle=" + _activeBattle + ", pendingDelayed=" + _pendingDelayed + ", incidents=" + _incidents + ", last=" + _lastSummary + ", log=" + GetLogPath();
		}

		public static string ManualSnapshot()
		{
			try
			{
				WorldSnapshot s = CaptureWorldSnapshot();
				Log("===== MANUAL WORLD SNAPSHOT =====");
				LogSnapshotSummary("MANUAL", s);
				foreach (RosterEntrySnapshot e in s.Entries.Values.Where(x => x.Malformed).Take(200)) Log("MANUAL_BAD " + e.ToLogLine());
				Log("===== END MANUAL WORLD SNAPSHOT =====");
				return "MANUAL_SNAPSHOT parties=" + s.PartyCount + ", rosters=" + s.RosterCount + ", entries=" + s.EntryCount + ", bad=" + s.BadCount + ", nonCanonicalRegular=" + s.NonCanonicalCount;
			}
			catch (Exception ex) { return "MANUAL_SNAPSHOT_ERROR " + Flatten(ex); }
		}

		public static string ClearLog()
		{
			try
			{
				string path = GetLogPath();
				Directory.CreateDirectory(Path.GetDirectoryName(path));
				File.WriteAllText(path, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] Battle Forensics v1.6 log cleared by user.\r\n");
				return "CLEARED: " + path;
			}
			catch (Exception ex) { return "CLEAR_LOG_ERROR " + Flatten(ex); }
		}

		public static string GetLogPath()
		{
			string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
			return Path.Combine(docs, "Mount and Blade II Bannerlord", "BattleForensics.log");
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

		private sealed class WorldSnapshot
		{
			public int PartyCount;
			public int RosterCount;
			public int EntryCount;
			public int BadCount;
			public int NonCanonicalCount;
			public readonly Dictionary<string, RosterEntrySnapshot> Entries = new Dictionary<string, RosterEntrySnapshot>(StringComparer.Ordinal);
		}

		private sealed class RosterEntrySnapshot
		{
			public string PartyId;
			public string PartyName;
			public string Roster;
			public string CharacterId;
			public int RefId;
			public int CanonicalRefId;
			public bool IsRegular;
			public bool IsCanonical;
			public bool Malformed;
			public int Number;
			public int Wounded;
			public int Xp;
			public string IdentityKey => PartyId + "|" + Roster + "|" + CharacterId + "|" + RefId;
			public string StackKey => PartyId + "|" + Roster + "|" + CharacterId;
			public string ToLogLine()
			{
				return "party=" + PartyId + " name=\"" + PartyName + "\" roster=" + Roster + " char=" + CharacterId + " ref=" + RefId + " canonicalRef=" + CanonicalRefId + " canonical=" + IsCanonical + " malformed=" + Malformed + " number=" + Number + " wounded=" + Wounded + " xp=" + Xp;
			}
		}
	}
}
