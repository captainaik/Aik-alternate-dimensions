using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TaleWorlds.Library;

namespace AelfeyjaRescue
{
	public static class CorruptMilitiaCommands
	{
		private static readonly HashSet<string> TargetPartyIds = new HashSet<string>(StringComparer.Ordinal)
		{
			"militias_of_militias_of_town_V8_aaa1_aaa1",
			"militias_of_militias_of_village_V9_1_aaa1_aaa1",
			"militias_of_militias_of_village_B2_2_aaa1_aaa1"
		};

		[CommandLineFunctionality.CommandLineArgumentFunction("scan_corrupt_militias", "rescue")]
		public static string Scan(List<string> args)
		{
			try
			{
				var parties = GetAllMobileParties().ToList();
				var found = parties.Where(p => TargetPartyIds.Contains(GetStringId(p))).ToList();
				if (found.Count == 0)
					return "SCAN: none of the 3 crash-dump militia parties are currently active.";

				return "SCAN: found " + found.Count + "/3 targeted parties: "
					+ string.Join(" | ", found.Select(DescribeParty));
			}
			catch (Exception ex)
			{
				return "SCAN ERROR: " + Flatten(ex);
			}
		}

		[CommandLineFunctionality.CommandLineArgumentFunction("purge_corrupt_militias", "rescue")]
		public static string Purge(List<string> args)
		{
			try
			{
				var parties = GetAllMobileParties().ToList();
				var targets = parties.Where(p => TargetPartyIds.Contains(GetStringId(p))).ToList();

				if (targets.Count == 0)
					return "PURGE: none of the 3 crash-dump militia parties were found; no changes made.";

				var results = new List<string>();
				int destroyed = 0;
				foreach (object party in targets)
				{
					string id = GetStringId(party);
					try
					{
						if (TryDestroyParty(party))
						{
							destroyed++;
							results.Add(id + "=DESTROYED");
						}
						else
						{
							results.Add(id + "=FAILED");
						}
					}
					catch (Exception ex)
					{
						results.Add(id + "=ERROR(" + Flatten(ex) + ")");
					}
				}

				return "PURGE: destroyed " + destroyed + "/" + targets.Count + " targeted corrupt militia parties. "
					+ string.Join(" | ", results);
			}
			catch (Exception ex)
			{
				return "PURGE ERROR: " + Flatten(ex);
			}
		}

		private static IEnumerable<object> GetAllMobileParties()
		{
			Type mobilePartyType = FindType("TaleWorlds.CampaignSystem.MobileParty")
				?? FindType("TaleWorlds.CampaignSystem.Party.MobileParty");
			if (mobilePartyType == null)
				throw new InvalidOperationException("MobileParty type not found.");

			foreach (string memberName in new[] { "All", "AllParties", "AllMobileParties" })
			{
				PropertyInfo p = mobilePartyType.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
				if (p != null)
				{
					object value = p.GetValue(null, null);
					if (value is IEnumerable enumerable)
					{
						foreach (object item in enumerable)
							if (item != null)
								yield return item;
						yield break;
					}
				}
			}

			throw new InvalidOperationException("Could not enumerate MobileParty.All.");
		}

		private static bool TryDestroyParty(object party)
		{
			Type destroyType = FindType("TaleWorlds.CampaignSystem.Actions.DestroyPartyAction");
			if (destroyType == null)
				return false;

			foreach (MethodInfo method in destroyType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
				.Where(m => m.Name == "Apply"))
			{
				ParameterInfo[] ps = method.GetParameters();
				if (ps.Length != 2 || !ps[1].ParameterType.IsInstanceOfType(party))
					continue;

				if (ps[0].ParameterType.IsValueType && Nullable.GetUnderlyingType(ps[0].ParameterType) == null)
					continue;

				method.Invoke(null, new object[] { null, party });
				return true;
			}
			return false;
		}

		private static string DescribeParty(object party)
		{
			string id = GetStringId(party);
			object name = GetProperty(party, "Name");
			object leader = GetProperty(party, "LeaderHero");
			object memberRoster = GetProperty(party, "MemberRoster");
			object total = memberRoster == null ? null : GetProperty(memberRoster, "TotalManCount");
			return id + " (Name=" + Safe(name) + ", Leader=" + Safe(leader) + ", Men=" + Safe(total) + ")";
		}

		private static string GetStringId(object obj)
		{
			object value = GetProperty(obj, "StringId");
			return value == null ? "" : value.ToString();
		}

		private static object GetProperty(object obj, string name)
		{
			if (obj == null)
				return null;
			PropertyInfo p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
			if (p == null)
				return null;
			try { return p.GetValue(obj, null); }
			catch { return null; }
		}

		private static Type FindType(string fullName)
		{
			foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				try
				{
					Type type = assembly.GetType(fullName, false);
					if (type != null)
						return type;
				}
				catch { }
			}
			return null;
		}

		private static string Safe(object value)
		{
			if (value == null)
				return "null";
			try { return value.ToString(); }
			catch { return value.GetType().Name; }
		}

		private static string Flatten(Exception ex)
		{
			if (ex is TargetInvocationException && ex.InnerException != null)
				ex = ex.InnerException;
			return ex.GetType().Name + ": " + ex.Message;
		}
	}
}
