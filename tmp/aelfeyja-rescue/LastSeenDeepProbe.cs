using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.ObjectSystem;

namespace AelfeyjaRescue
{
	public static class LastSeenDeepProbeCommands
	{
		[CommandLineFunctionality.CommandLineArgumentFunction("diag_last_seen_deep", "rescue")]
		public static string DeepProbe(List<string> args)
		{
			if (args == null || args.Count == 0)
				return "Usage: rescue.diag_last_seen_deep <Hero StringId>";
			return LastSeenDeepProbe.Run(args[0]);
		}
	}

	internal static class LastSeenDeepProbe
	{
		private const string Version = "v1.7.3";
		private static readonly Dictionary<short, OpCode> OpCodesByValue = BuildOpCodeMap();

		public static string Run(string heroId)
		{
			StringBuilder sb = new StringBuilder(16384);
			sb.AppendLine();
			sb.AppendLine("============================================================");
			sb.AppendLine("CHARACTER CRASH GUARD - LAST SEEN DEEP PROBE");
			sb.AppendLine("============================================================");
			sb.AppendLine("Timestamp: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"));
			sb.AppendLine("Version: " + Version);
			sb.AppendLine("Hero requested: " + heroId);

			try
			{
				CharacterObject co = MBObjectManager.Instance.GetObject<CharacterObject>(heroId);
				Hero hero = co?.HeroObject;
				if (hero == null)
				{
					sb.AppendLine("Hero resolution: FAILED");
					Write(sb.ToString());
					return "DEEP_PROBE failed: hero not found for " + heroId;
				}

				sb.AppendLine("Hero resolution: OK " + hero.Name + " / " + hero.StringId);
				sb.AppendLine();
				sb.AppendLine("[BRANCH INPUTS]");
				Probe(sb, "Hero.LastKnownClosestSettlement", () => hero.LastKnownClosestSettlement);
				Probe(sb, "Hero.CurrentSettlement", () => hero.CurrentSettlement);
				Probe(sb, "Hero.PartyBelongedToAsPrisoner", () => hero.PartyBelongedToAsPrisoner);
				Settlement last = null;
				try { last = hero.LastKnownClosestSettlement; } catch { }
				if (last != null)
				{
					Probe(sb, "LastKnown.StringId", () => last.StringId);
					Probe(sb, "LastKnown.Name", () => last.Name);
					Probe(sb, "LastKnown.EncyclopediaLink", () => last.EncyclopediaLink);
					Probe(sb, "LastKnown.EncyclopediaLinkWithName", () => last.EncyclopediaLinkWithName);
				}

				sb.AppendLine();
				sb.AppendLine("[GAMETEXT PROBES]");
				TextObject lastSeen = null;
				TextObject neverSeen = null;
				try
				{
					lastSeen = GameTexts.FindText("str_last_seen_encyclopedia_entry");
					sb.AppendLine("GameTexts.FindText(str_last_seen_encyclopedia_entry): " + DescribeText(lastSeen));
				}
				catch (Exception ex)
				{
					sb.AppendLine("GameTexts.FindText(str_last_seen_encyclopedia_entry): THREW " + Flatten(ex));
				}
				try
				{
					neverSeen = GameTexts.FindText("str_never_seen_encyclopedia_entry");
					sb.AppendLine("GameTexts.FindText(str_never_seen_encyclopedia_entry): " + DescribeText(neverSeen));
				}
				catch (Exception ex)
				{
					sb.AppendLine("GameTexts.FindText(str_never_seen_encyclopedia_entry): THREW " + Flatten(ex));
				}

				if (lastSeen != null && last != null)
				{
					sb.AppendLine();
					sb.AppendLine("[SAFE CLONE VARIABLE TEST]");
					try
					{
						TextObject clone = new TextObject(lastSeen.ToString());
						clone.SetTextVariable("SETTLEMENT", last.EncyclopediaLinkWithName);
						sb.AppendLine("Clone.SetTextVariable(SETTLEMENT): OK");
						clone.SetTextVariable("IS_IN_SETTLEMENT", last == hero.CurrentSettlement ? 1 : 0);
						sb.AppendLine("Clone.SetTextVariable(IS_IN_SETTLEMENT): OK");
						sb.AppendLine("Clone.ToString(): " + SafeToString(clone));
					}
					catch (Exception ex)
					{
						sb.AppendLine("Safe clone variable test: THREW " + Flatten(ex));
					}
				}

				Type helperType = AccessTools.TypeByName("Helpers.HeroHelper");
				MethodInfo helper = helperType?.GetMethod("GetLastSeenText", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(Hero) }, null);
				sb.AppendLine();
				sb.AppendLine("[HARMONY PATCH OWNERS]");
				if (helper == null)
				{
					sb.AppendLine("HeroHelper.GetLastSeenText(Hero): NOT FOUND");
				}
				else
				{
					sb.AppendLine("Target: " + helper.DeclaringType.FullName + "." + helper.Name);
					AppendPatchInfo(sb, helper);
					sb.AppendLine();
					sb.AppendLine("[RUNTIME IL]");
					AppendIL(sb, helper);
				}

				sb.AppendLine();
				sb.AppendLine("[DIRECT HELPER INVOCATION]");
				if (helper != null)
				{
					try
					{
						object result = helper.Invoke(null, new object[] { hero });
						sb.AppendLine("Result: " + (result == null ? "<null>" : SafeToString(result)));
					}
					catch (TargetInvocationException tie)
					{
						sb.AppendLine("THREW: " + Flatten(tie.InnerException ?? tie));
					}
					catch (Exception ex)
					{
						sb.AppendLine("THREW: " + Flatten(ex));
					}
				}
			}
			catch (Exception ex)
			{
				sb.AppendLine("DEEP PROBE OUTER FAILURE: " + Flatten(ex));
			}

			sb.AppendLine("============================================================");
			string text = sb.ToString();
			Write(text);
			return "DEEP_PROBE_V173 complete hero=" + heroId + " log=" + ResolveLogPath();
		}

		private static void AppendPatchInfo(StringBuilder sb, MethodBase method)
		{
			try
			{
				Patches info = Harmony.GetPatchInfo(method);
				if (info == null)
				{
					sb.AppendLine("No Harmony patch info.");
					return;
				}
				AppendPatchList(sb, "Prefixes", info.Prefixes);
				AppendPatchList(sb, "Postfixes", info.Postfixes);
				AppendPatchList(sb, "Transpilers", info.Transpilers);
				AppendPatchList(sb, "Finalizers", info.Finalizers);
			}
			catch (Exception ex)
			{
				sb.AppendLine("Patch info error: " + Flatten(ex));
			}
		}

		private static void AppendPatchList(StringBuilder sb, string label, IEnumerable<Patch> patches)
		{
			Patch[] arr = patches == null ? Array.Empty<Patch>() : patches.ToArray();
			sb.AppendLine(label + " (" + arr.Length + "):");
			foreach (Patch p in arr)
			{
				string owner = p.owner ?? "<null>";
				string method = p.PatchMethod == null ? "<null>" : p.PatchMethod.DeclaringType?.FullName + "." + p.PatchMethod.Name;
				sb.AppendLine("  owner=" + owner + " priority=" + p.priority + " method=" + method);
			}
		}

		private static void AppendIL(StringBuilder sb, MethodInfo method)
		{
			try
			{
				MethodBody body = method.GetMethodBody();
				if (body == null)
				{
					sb.AppendLine("<no method body>");
					return;
				}
				byte[] il = body.GetILAsByteArray();
				Module module = method.Module;
				int pos = 0;
				while (pos < il.Length)
				{
					int offset = pos;
					short value = il[pos++];
					if (value == 0xFE)
						value = (short)(0xFE00 | il[pos++]);
					if (!OpCodesByValue.TryGetValue(value, out OpCode op))
					{
						sb.AppendLine("IL_" + offset.ToString("X4") + ": <unknown opcode 0x" + value.ToString("X") + ">");
						break;
					}
					string operand = ReadOperand(module, method, il, ref pos, op.OperandType);
					sb.AppendLine("IL_" + offset.ToString("X4") + ": " + op.Name + (string.IsNullOrEmpty(operand) ? "" : " " + operand));
				}
			}
			catch (Exception ex)
			{
				sb.AppendLine("IL dump error: " + Flatten(ex));
			}
		}

		private static string ReadOperand(Module module, MethodInfo context, byte[] il, ref int pos, OperandType type)
		{
			try
			{
				switch (type)
				{
					case OperandType.InlineNone: return "";
					case OperandType.ShortInlineI: return ((sbyte)il[pos++]).ToString();
					case OperandType.InlineI: { int v = BitConverter.ToInt32(il, pos); pos += 4; return v.ToString(); }
					case OperandType.InlineI8: { long v = BitConverter.ToInt64(il, pos); pos += 8; return v.ToString(); }
					case OperandType.ShortInlineR: { float v = BitConverter.ToSingle(il, pos); pos += 4; return v.ToString(); }
					case OperandType.InlineR: { double v = BitConverter.ToDouble(il, pos); pos += 8; return v.ToString(); }
					case OperandType.ShortInlineVar: return "V_" + il[pos++];
					case OperandType.InlineVar: { ushort v = BitConverter.ToUInt16(il, pos); pos += 2; return "V_" + v; }
					case OperandType.ShortInlineBrTarget: { sbyte d = (sbyte)il[pos++]; return "IL_" + (pos + d).ToString("X4"); }
					case OperandType.InlineBrTarget: { int d = BitConverter.ToInt32(il, pos); pos += 4; return "IL_" + (pos + d).ToString("X4"); }
					case OperandType.InlineString:
					{
						int token = BitConverter.ToInt32(il, pos); pos += 4;
						return "\"" + module.ResolveString(token) + "\"";
					}
					case OperandType.InlineField:
					case OperandType.InlineMethod:
					case OperandType.InlineType:
					case OperandType.InlineTok:
					{
						int token = BitConverter.ToInt32(il, pos); pos += 4;
						Type[] typeArgs = context.DeclaringType?.GetGenericArguments();
						Type[] methodArgs = context.GetGenericArguments();
						MemberInfo member = module.ResolveMember(token, typeArgs, methodArgs);
						return member == null ? "token=0x" + token.ToString("X8") : member.DeclaringType?.FullName + "." + member.Name;
					}
					case OperandType.InlineSwitch:
					{
						int count = BitConverter.ToInt32(il, pos); pos += 4;
						int basePos = pos + count * 4;
						string[] targets = new string[count];
						for (int i = 0; i < count; i++)
						{
							int d = BitConverter.ToInt32(il, pos); pos += 4;
							targets[i] = "IL_" + (basePos + d).ToString("X4");
						}
						return string.Join(", ", targets);
					}
					default: return "<operand " + type + ">";
				}
			}
			catch (Exception ex)
			{
				return "<operand error: " + ex.GetType().Name + ">";
			}
		}

		private static Dictionary<short, OpCode> BuildOpCodeMap()
		{
			Dictionary<short, OpCode> map = new Dictionary<short, OpCode>();
			foreach (FieldInfo f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
			{
				if (f.GetValue(null) is OpCode op)
					map[op.Value] = op;
			}
			return map;
		}

		private static void Probe(StringBuilder sb, string label, Func<object> getter)
		{
			try
			{
				object value = getter();
				sb.AppendLine(label + ": " + Describe(value));
			}
			catch (Exception ex)
			{
				sb.AppendLine(label + ": THREW " + Flatten(ex));
			}
		}

		private static string DescribeText(TextObject t)
		{
			if (t == null) return "<null>";
			return "TextObject{ToString=" + SafeToString(t) + "}";
		}

		private static string Describe(object value)
		{
			if (value == null) return "<null>";
			try
			{
				PropertyInfo idp = value.GetType().GetProperty("StringId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				PropertyInfo np = value.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				string id = idp == null ? "<n/a>" : Convert.ToString(idp.GetValue(value, null));
				string name = np == null ? SafeToString(value) : SafeToString(np.GetValue(value, null));
				return value.GetType().Name + "{Name=" + name + ", StringId=" + id + "}";
			}
			catch { return value.GetType().FullName + "{" + SafeToString(value) + "}"; }
		}

		private static string SafeToString(object o)
		{
			if (o == null) return "<null>";
			try { return o.ToString(); } catch (Exception ex) { return "<ToString threw " + ex.GetType().Name + ">"; }
		}

		private static string Flatten(Exception ex)
		{
			if (ex == null) return "<null>";
			StringBuilder sb = new StringBuilder();
			for (Exception cur = ex; cur != null; cur = cur.InnerException)
			{
				if (sb.Length > 0) sb.Append(" -> ");
				sb.Append(cur.GetType().FullName).Append(": ").Append(cur.Message);
			}
			return sb.ToString();
		}

		private static string ResolveLogPath()
		{
			try
			{
				string bin = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
				DirectoryInfo d = new DirectoryInfo(bin);
				for (int i = 0; i < 2 && d != null; i++) d = d.Parent;
				string root = d?.FullName ?? bin;
				string logs = Path.Combine(root, "Logs");
				Directory.CreateDirectory(logs);
				FileInfo latest = new DirectoryInfo(logs).GetFiles("CharacterCrashGuard_*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
				return latest != null ? latest.FullName : Path.Combine(logs, "CharacterCrashGuard_" + DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + ".log");
			}
			catch
			{
				return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CharacterCrashGuard_DeepProbe.log");
			}
		}

		private static void Write(string text)
		{
			try { File.AppendAllText(ResolveLogPath(), text + Environment.NewLine, Encoding.UTF8); }
			catch { }
		}
	}
}
