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
			if (gameStarter is CampaignGameStarter starter)
			{
				starter.AddBehavior(new BattleForensicsBehavior());
				BattleForensics.Log("Battle Forensics v1.6 behavior added.");
			}
		}
	}

	public static class BattleForensicsCommands
	{
		[CommandLineFunctionality.CommandLineArgumentFunction("battle_status", "rescue")]
		public static string Status(List<string> args) => BattleForensics.Status();
		[CommandLineFunctionality.CommandLineArgumentFunction("battle_log_path", "rescue")]
		public static string LogPath(List<string> args) => BattleForensics.GetLogPath();
		[CommandLineFunctionality.CommandLineArgumentFunction("battle_clear_log", "rescue")]
		public static string Clear(List<string> args) => BattleForensics.ClearLog();
		[CommandLineFunctionality.CommandLineArgumentFunction("battle_snapshot_now", "rescue")]
		public static string Snapshot(List<string> args) => BattleForensics.ManualSnapshot();
	}

	internal sealed class BattleForensicsBehavior : CampaignBehaviorBase
	{
		public override void RegisterEvents()
		{
			CampaignEvents.MapEventStarted.AddNonSerializedListener(this, (mapEvent, party1, party2) => OnMapEventStarted(mapEvent));
			CampaignEvents.MapEventEnded.AddNonSerializedListener(this, OnMapEventEnded);
			CampaignEvents.OnPlayerBattleEndEvent.AddNonSerializedListener(this, OnPlayerBattleEnd);
			CampaignEvents.TickEvent.AddNonSerializedListener(this, OnTick);
			BattleForensics.Log("Battle Forensics v1.6 events registered.");
		}
		public override void SyncData(IDataStore dataStore) { }
		private void OnMapEventStarted(MapEvent mapEvent) => BattleForensics.Start(mapEvent);
		private void OnMapEventEnded(MapEvent mapEvent) => BattleForensics.Stage("MapEventEnded", mapEvent, true);
		private void OnPlayerBattleEnd(MapEvent mapEvent) => BattleForensics.Stage("OnPlayerBattleEndEvent", mapEvent, true);
		private void OnTick(float dt) => BattleForensics.Tick(dt);
	}

	internal static class BattleForensics
	{
		private static readonly BindingFlags IF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
		private static readonly BindingFlags SF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
		private static SnapshotState _before;
		private static string _battle = "none";
		private static string _last = "none";
		private static bool _pending;
		private static float _elapsed;
		private static bool _s01, _s1, _s5;
		private static int _battleNo, _incidents;

		public static void Start(MapEvent mapEvent)
		{
			try
			{
				if (mapEvent == null || !BoolProp(mapEvent, "IsPlayerMapEvent")) return;
				_battle = "battle-" + (++_battleNo);
				_before = Capture();
				_pending = false; _elapsed = 0f; _s01 = _s1 = _s5 = false;
				Log("===== PLAYER BATTLE START " + _battle + " =====");
				Log("participants=" + Participants(mapEvent));
				Log(Summary("PRE_BATTLE", _before));
				Log("===== END PLAYER BATTLE START =====");
			}
			catch (Exception ex) { Log("START_EXCEPTION " + Flat(ex)); }
		}

		public static void Stage(string stage, MapEvent mapEvent, bool delay)
		{
			try
			{
				if (mapEvent == null || !BoolProp(mapEvent, "IsPlayerMapEvent")) return;
				if (_before == null)
				{
					_before = Capture();
					_battle = "late-attach";
					Log("WARNING late baseline created at " + stage);
				}
				Analyze(stage, mapEvent);
				if (delay) { _pending = true; _elapsed = 0f; _s01 = _s1 = _s5 = false; }
			}
			catch (Exception ex) { Log("STAGE_EXCEPTION stage=" + stage + " error=" + Flat(ex)); }
		}

		public static void Tick(float dt)
		{
			if (!_pending || _before == null) return;
			_elapsed += Math.Max(0f, dt);
			if (!_s01 && _elapsed >= .1f) { _s01 = true; Analyze("PostBattle+0.1s", null); }
			if (!_s1 && _elapsed >= 1f) { _s1 = true; Analyze("PostBattle+1s", null); }
			if (!_s5 && _elapsed >= 5f)
			{
				_s5 = true; Analyze("PostBattle+5s", null);
				_pending = false; _before = null; _battle = "none";
			}
		}

		private static void Analyze(string stage, MapEvent mapEvent)
		{
			SnapshotState now = Capture();
			var bad = now.Entries.Values.Where(e => e.Malformed).ToList();
			var newBad = bad.Where(e => !_before.Entries.ContainsKey(e.IdentityKey)).ToList();
			var newClone = now.Entries.Values.Where(e => e.Regular && !e.Canonical && !e.Malformed && !_before.Entries.ContainsKey(e.IdentityKey)).ToList();
			Log("===== BATTLE FORENSICS " + stage + " " + _battle + " =====");
			if (mapEvent != null) Log("participants=" + Participants(mapEvent));
			Log(Summary(stage, now));
			Log("delta newBad=" + newBad.Count + ", currentBad=" + bad.Count + ", newNonCanonical=" + newClone.Count);
			foreach (Entry e in newBad) { _incidents++; Log("NEW_BAD_AFTER_BATTLE " + e.Line()); }
			foreach (Entry e in newClone.Take(100)) Log("NEW_NONCANONICAL_AFTER_BATTLE " + e.Line());

			var bGroups = _before.Entries.Values.GroupBy(e => e.StackKey).ToDictionary(g => g.Key, g => g.ToList());
			var aGroups = now.Entries.Values.GroupBy(e => e.StackKey).ToDictionary(g => g.Key, g => g.ToList());
			foreach (string key in bGroups.Keys.Union(aGroups.Keys).Distinct())
			{
				List<Entry> b = bGroups.ContainsKey(key) ? bGroups[key] : new List<Entry>();
				List<Entry> a = aGroups.ContainsKey(key) ? aGroups[key] : new List<Entry>();
				string br = string.Join(",", b.Select(x => x.Ref.ToString()).OrderBy(x => x).ToArray());
				string ar = string.Join(",", a.Select(x => x.Ref.ToString()).OrderBy(x => x).ToArray());
				if (br != ar) Log("REF_CHANGE stack=" + key + " beforeRefs=[" + br + "] afterRefs=[" + ar + "] beforeCount=" + b.Sum(x => x.Number) + " afterCount=" + a.Sum(x => x.Number));
			}
			_last = stage + " newBad=" + newBad.Count + " currentBad=" + bad.Count + " newNonCanonical=" + newClone.Count;
			Log("===== END BATTLE FORENSICS =====");
			if (newBad.Count > 0)
			{
				try { InformationManager.DisplayMessage(new InformationMessage("[Battle Forensics] " + newBad.Count + " malformed roster reference(s) appeared after battle. Check BattleForensics.log.")); } catch { }
			}
		}

		private static SnapshotState Capture()
		{
			SnapshotState s = new SnapshotState();
			Dictionary<string, object> canon = CanonicalMap();
			IEnumerable parties = AllParties();
			if (parties == null) return s;
			foreach (object party in parties)
			{
				if (party == null) continue;
				s.Parties++;
				string pid = StringId(party), pname = Text(GetProperty(party, "Name"));
				foreach (string rn in new[] { "MemberRoster", "PrisonRoster" })
				{
					object roster = Roster(party, rn); if (roster == null) continue;
					s.Rosters++;
					Array data; int count; if (!RosterData(roster, out data, out count)) continue;
					for (int i = 0; i < count; i++)
					{
						object el = data.GetValue(i), ch = Member(el, "Character"); if (ch == null) continue;
						string cid = StringId(ch); object c; canon.TryGetValue(cid, out c);
						bool hero = IsHero(ch), malformed = !hero && IsMalformed(ch), canonical = c != null && object.ReferenceEquals(c, ch);
						Entry e = new Entry { Party = pid, PartyName = pname, Roster = rn, Character = cid, Ref = RuntimeHelpers.GetHashCode(ch), CanonicalRef = c == null ? 0 : RuntimeHelpers.GetHashCode(c), Regular = !hero, Canonical = canonical, Malformed = malformed, Number = IntMember(el, "Number", "_number"), Wounded = IntMember(el, "WoundedNumber", "_woundedNumber"), Xp = IntMember(el, "Xp", "_xp") };
						s.Entries[e.IdentityKey] = e; s.EntryCount++; if (malformed) s.Bad++; if (e.Regular && !e.Canonical) s.NonCanonical++;
					}
				}
			}
			return s;
		}

		private static Dictionary<string, object> CanonicalMap()
		{
			var d = new Dictionary<string, object>(StringComparer.Ordinal);
			IEnumerable all = GetStatic(FindType("TaleWorlds.CampaignSystem.CharacterObject"), "All") as IEnumerable;
			if (all == null) return d;
			foreach (object c in all) { if (c == null || IsHero(c) || IsMalformed(c)) continue; string id = StringId(c); if (!d.ContainsKey(id)) d[id] = c; }
			return d;
		}

		private static bool IsHero(object c)
		{
			object p = GetProperty(c, "IsHero"); if (p is bool && (bool)p) return true;
			return FieldValue(FindField(c == null ? null : c.GetType(), "_heroObject"), c) != null;
		}
		private static bool IsMalformed(object c)
		{
			if (c == null || IsHero(c)) return false;
			FieldInfo f = FindField(c.GetType(), "DefaultCharacterSkills"); if (f == null) return false;
			object ds = FieldValue(f, c); return ds == null || Member(ds, "Skills") == null;
		}
		private static IEnumerable AllParties()
		{
			Type t = FindType("TaleWorlds.CampaignSystem.Party.MobileParty"); object all = GetStatic(t, "All"); if (all is IEnumerable) return (IEnumerable)all;
			object camp = GetStatic(FindType("TaleWorlds.CampaignSystem.Campaign"), "Current"); return camp == null ? null : GetProperty(camp, "MobileParties") as IEnumerable;
		}
		private static object Roster(object p, string name) { object r = GetProperty(p, name); if (r != null) return r; object pb = GetProperty(p, "Party"); return pb == null ? null : GetProperty(pb, name); }
		private static bool RosterData(object r, out Array data, out int count)
		{
			data = null; count = -1; try { object c = GetProperty(r, "Count") ?? FieldValue(FindField(r.GetType(), "_count"), r); if (!(c is int)) return false; count = (int)c; data = FieldValue(FindField(r.GetType(), "data") ?? FindField(r.GetType(), "_data"), r) as Array; return data != null && count >= 0 && count <= data.Length; } catch { return false; }
		}
		private static string Participants(MapEvent m)
		{
			try { IEnumerable e = GetProperty(m, "InvolvedParties") as IEnumerable; if (e == null) return "<unavailable>"; var a = new List<string>(); foreach (object pb in e) { object mob = GetProperty(pb, "MobileParty"); object x = mob ?? pb; a.Add(StringId(x) + "(" + Text(GetProperty(x, "Name")) + ")"); } return a.Count == 0 ? "none" : string.Join("; ", a.ToArray()); } catch { return "<error>"; }
		}
		private static bool BoolProp(object o, string n) { object v = GetProperty(o, n); return v is bool && (bool)v; }
		private static int IntMember(object o, string prop, string field) { object v = GetProperty(o, prop); if (v is int) return (int)v; v = FieldValue(FindField(o == null ? null : o.GetType(), field), o); return v is int ? (int)v : 0; }
		private static object Member(object o, string n) { object v = GetProperty(o, n); return v ?? FieldValue(FindField(o == null ? null : o.GetType(), n), o); }
		private static object GetProperty(object o, string n) { if (o == null) return null; try { PropertyInfo p = o.GetType().GetProperty(n, IF); return p == null ? null : p.GetValue(o, null); } catch { return null; } }
		private static object GetStatic(Type t, string n) { if (t == null) return null; try { PropertyInfo p = t.GetProperty(n, SF); if (p != null) return p.GetValue(null, null); } catch { } try { FieldInfo f = t.GetField(n, SF); return f == null ? null : f.GetValue(null); } catch { return null; } }
		private static FieldInfo FindField(Type t, string n) { for (Type x = t; x != null; x = x.BaseType) { FieldInfo f = x.GetField(n, IF); if (f != null) return f; } return null; }
		private static object FieldValue(FieldInfo f, object o) { if (f == null || o == null) return null; try { return f.GetValue(o); } catch { return null; } }
		private static Type FindType(string n) { foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) try { Type t = a.GetType(n, false); if (t != null) return t; } catch { } return null; }
		private static string StringId(object o) { if (o == null) return "<null>"; object v = Member(o, "StringId"); return v == null ? "<no-id>" : v.ToString(); }
		private static string Text(object o) { try { return o == null ? "" : o.ToString(); } catch { return ""; } }
		private static string Flat(Exception ex) { while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException; return ex.GetType().Name + ": " + ex.Message; }
		private static string Summary(string label, SnapshotState s) => label + " parties=" + s.Parties + ", rosters=" + s.Rosters + ", entries=" + s.EntryCount + ", bad=" + s.Bad + ", nonCanonicalRegular=" + s.NonCanonical;

		public static string Status() => "BATTLE_FORENSICS_V16 active=" + (_before != null) + ", battle=" + _battle + ", pendingDelayed=" + _pending + ", incidents=" + _incidents + ", last=" + _last + ", log=" + GetLogPath();
		public static string ManualSnapshot() { try { SnapshotState s = Capture(); Log("===== MANUAL SNAPSHOT ====="); Log(Summary("MANUAL", s)); foreach (Entry e in s.Entries.Values.Where(x => x.Malformed).Take(200)) Log("MANUAL_BAD " + e.Line()); Log("===== END MANUAL SNAPSHOT ====="); return Summary("MANUAL", s); } catch (Exception ex) { return "MANUAL_SNAPSHOT_ERROR " + Flat(ex); } }
		public static string ClearLog() { try { string p = GetLogPath(); Directory.CreateDirectory(Path.GetDirectoryName(p)); File.WriteAllText(p, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] Battle Forensics v1.6 log cleared by user.\r\n"); return "CLEARED: " + p; } catch (Exception ex) { return "CLEAR_LOG_ERROR " + Flat(ex); } }
		public static string GetLogPath() { string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); return Path.Combine(docs, "Mount and Blade II Bannerlord", "BattleForensics.log"); }
		public static void Log(string m) { try { string p = GetLogPath(); Directory.CreateDirectory(Path.GetDirectoryName(p)); File.AppendAllText(p, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + m + Environment.NewLine); } catch { } }

		private sealed class SnapshotState { public int Parties, Rosters, EntryCount, Bad, NonCanonical; public readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>(StringComparer.Ordinal); }
		private sealed class Entry
		{
			public string Party, PartyName, Roster, Character; public int Ref, CanonicalRef, Number, Wounded, Xp; public bool Regular, Canonical, Malformed;
			public string IdentityKey => Party + "|" + Roster + "|" + Character + "|" + Ref;
			public string StackKey => Party + "|" + Roster + "|" + Character;
			public string Line() => "party=" + Party + " name=\"" + PartyName + "\" roster=" + Roster + " char=" + Character + " ref=" + Ref + " canonicalRef=" + CanonicalRef + " canonical=" + Canonical + " malformed=" + Malformed + " number=" + Number + " wounded=" + Wounded + " xp=" + Xp;
		}
	}
}
