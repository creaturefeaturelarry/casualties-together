using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using LiteEntitySystem;

namespace KrokV5Optimization;

/// <summary>
/// the accept-time snapshot listed objects and skipped current SyncVar values, so worn gear and slots never arrived.
/// MakeDiff also treated a zero ack as "already current" once the server had been up a while.
/// the first packet to that player sends every current value. later packets stay diffs. an existing object is updated, not spawned again.
/// </summary>
internal static class FixLateBaseline
{
	static bool ForceFull;

	// OnLogicTick decides when a joiner gets a full snapshot. MakeDiff decides which fields go in it.
	internal static void Arm(Harmony harmony)
	{
		MethodInfo tick = AccessTools.Method(typeof(ServerEntityManager), "OnLogicTick");
		Type serializer = AccessTools.TypeByName("LiteEntitySystem.Internal.StateSerializer");
		MethodInfo diff = AccessTools.Method(serializer, "MakeDiff");
		if (tick == null || diff == null)
			throw new MissingMethodException("late baseline hook");

		harmony.Patch(tick,
			transpiler: new HarmonyMethod(typeof(FixLateBaseline), nameof(TranspileTick)),
			finalizer: new HarmonyMethod(typeof(FixLateBaseline), nameof(Done)));
		try
		{
			harmony.Patch(diff, transpiler: new HarmonyMethod(typeof(FixLateBaseline), nameof(TranspileDiff)));
		}
		catch (Exception ex)
		{
			throw new InvalidOperationException("MakeDiff force failed: " + ex.Message, ex);
		}
		OptimizerMain.Log.LogDebug("[KrokV5Opt] late baseline hooked");
	}

	// called from the patched tick. alreadySent true means this player had a baseline before, so do not force.
	public static void SetForce(bool alreadySent)
	{
		ForceFull = !alreadySent;
	}

	// MakeDiff calls this. true means "include the field even if the ack says the client has it".
	public static bool Force()
	{
		return ForceFull;
	}

	// end of that player's send. later ticks go back to diffs.
	public static void Finish(NetPlayer player)
	{
		if (ForceFull && player != null)
			OptimizerMain.Log?.LogInfo("[KrokV5Opt] full baseline player=" + player.Id);
		ForceFull = false;
	}

	// finalizer. a throw in the tick must not leave the next player on a forced full send.
	static Exception Done(Exception __exception)
	{
		ForceFull = false;
		return __exception;
	}

	// stock: if (!player.FirstBaselineSent) { SendWorld; player.FirstBaselineSent = true; }
	// we always enter that block, and SetForce remembers whether it was the first time.
	static IEnumerable<CodeInstruction> TranspileTick(IEnumerable<CodeInstruction> instructions)
	{
		List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
		CodeInstruction branch = null;
		CodeInstruction playerLoad = null;
		CodeInstruction join = null;
		FieldInfo sent = null;
		for (int i = 1; i < codes.Count - 1; i++)
		{
			if (!IsField(codes[i], "FirstBaselineSent") || codes[i].opcode != OpCodes.Ldfld)
				continue;
			CodeInstruction next = codes[i + 1];
			if (next.opcode != OpCodes.Brfalse && next.opcode != OpCodes.Brfalse_S)
				continue;
			CodeInstruction target = TargetOf(codes, next.operand);
			if (target == null)
				continue;
			int at = codes.IndexOf(target);
			if (at < 0 || at + 3 >= codes.Count)
				continue;
			if (codes[at + 1].opcode != OpCodes.Ldc_I4_1 || !IsStore(codes[at + 2], "FirstBaselineSent"))
				continue;
			branch = next;
			playerLoad = codes[i - 1];
			sent = (FieldInfo)codes[at + 2].operand;
			join = codes[at + 3];
			break;
		}
		if (branch == null || join == null || sent == null)
			throw new InvalidOperationException("late baseline missed the first SendWorld branch");

		// the branch was "skip SendWorld if already sent". pop the bool and call SetForce instead, so SendWorld always runs.
		branch.opcode = OpCodes.Pop;
		branch.operand = null;
		int branchAt = codes.IndexOf(branch);
		codes.Insert(branchAt, new CodeInstruction(OpCodes.Dup));
		codes.Insert(branchAt + 1, CodeInstruction.Call(typeof(FixLateBaseline), nameof(SetForce)));

		// after the send, Finish clears the force flag, then the original store still sets FirstBaselineSent.
		CodeInstruction finishLoad = new CodeInstruction(playerLoad.opcode, playerLoad.operand);
		int joinAt = codes.IndexOf(join);
		codes.Insert(joinAt, finishLoad);
		codes.Insert(joinAt + 1, CodeInstruction.Call(typeof(FixLateBaseline), nameof(Finish), new[] { typeof(NetPlayer) }));
		codes.Insert(joinAt + 2, new CodeInstruction(playerLoad.opcode, playerLoad.operand));
		codes.Insert(joinAt + 3, new CodeInstruction(OpCodes.Ldc_I4_1));
		codes.Insert(joinAt + 4, new CodeInstruction(OpCodes.Stfld, sent));
		finishLoad.labels.AddRange(join.labels);
		join.labels.Clear();
		for (int i = 0; i < codes.Count; i++)
		{
			if (ReferenceEquals(codes[i].operand, join))
				codes[i].operand = finishLoad;
		}
		return codes;
	}

	// two skips inside MakeDiff. both treat "client has nothing" as "client is current" once the server tick is large.
	static IEnumerable<CodeInstruction> TranspileDiff(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
	{
		List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
		BypassAck(codes, generator);
		BypassFieldTick(codes, generator);
		return codes;
	}

	// if LastChangedTick is not ahead of the client's ack, MakeDiff returns 0 bytes.
	// on a forced baseline, jump past that return and write the fields anyway.
	static void BypassAck(List<CodeInstruction> codes, ILGenerator generator)
	{
		// the compare sits on ldarg.0. hopping there leaves the serializer on the stack and Harmony rejects the method. use the empty early return under that compare.
		int early = -1;
		CodeInstruction resume = null;
		int found = 0;
		for (int i = 0; i < codes.Count - 7; i++)
		{
			if (!IsField(codes[i], "LastChangedTick") || codes[i].opcode != OpCodes.Ldfld)
				continue;
			if (codes[i + 1].opcode != OpCodes.Ldarg_1 || !IsField(codes[i + 2], "LatestServerTick"))
				continue;
			if (!IsCall(codes[i + 3], "SequenceDiff") || codes[i + 4].opcode != OpCodes.Ldc_I4_0)
				continue;
			if (codes[i + 5].opcode != OpCodes.Bgt && codes[i + 5].opcode != OpCodes.Bgt_S)
				continue;
			if (codes[i + 6].opcode != OpCodes.Ldc_I4_0 || codes[i + 7].opcode != OpCodes.Ret)
				continue;
			early = i + 6;
			resume = TargetOf(codes, codes[i + 5].operand);
			found++;
		}
		if (found != 1 || early < 0 || resume == null)
			throw new InvalidOperationException("late baseline missed the ack check in MakeDiff");
		Label hopTo = generator.DefineLabel();
		resume.labels.Add(hopTo);
		codes.Insert(early, CodeInstruction.Call(typeof(FixLateBaseline), nameof(Force)));
		codes.Insert(early + 1, new CodeInstruction(OpCodes.Brtrue, hopTo));
	}

	// per field, stock skips values older than the client's tick. a forced baseline includes those too.
	static void BypassFieldTick(List<CodeInstruction> codes, ILGenerator generator)
	{
		int ticks = -1;
		int found = 0;
		for (int i = 1; i < codes.Count; i++)
		{
			if (!IsField(codes[i], "_fieldChangeTicks") || codes[i].opcode != OpCodes.Ldfld)
				continue;
			ticks = i;
			found++;
		}
		if (found != 1 || codes[ticks - 1].opcode != OpCodes.Ldarg_0)
			throw new InvalidOperationException("late baseline missed the field ticks in MakeDiff");

		CodeInstruction anchor = codes[ticks - 1];
		CodeInstruction resume = null;
		for (int i = ticks; i < codes.Count - 2; i++)
		{
			if (!IsCall(codes[i], "SequenceDiff") || codes[i + 1].opcode != OpCodes.Ldc_I4_0)
				continue;
			if (codes[i + 2].opcode != OpCodes.Ble && codes[i + 2].opcode != OpCodes.Ble_S)
				continue;
			resume = codes[i + 3];
			break;
		}
		if (resume == null)
			throw new InvalidOperationException("late baseline missed the field tick check in MakeDiff");
		InsertForceHop(codes, anchor, resume, generator);
	}

	// insert Force(); if (true) goto resume in front of anchor. branches that targeted anchor now target the call.
	static void InsertForceHop(List<CodeInstruction> codes, CodeInstruction anchor, CodeInstruction resume, ILGenerator generator)
	{
		CodeInstruction call = CodeInstruction.Call(typeof(FixLateBaseline), nameof(Force));
		Label hopTo = generator.DefineLabel();
		resume.labels.Add(hopTo);
		CodeInstruction hop = new CodeInstruction(OpCodes.Brtrue, hopTo);
		call.labels.AddRange(anchor.labels);
		anchor.labels.Clear();
		for (int i = 0; i < codes.Count; i++)
		{
			if (ReferenceEquals(codes[i].operand, anchor))
				codes[i].operand = call;
		}
		int at = codes.IndexOf(anchor);
		codes.Insert(at, call);
		codes.Insert(at + 1, hop);
	}

	// Harmony stores branch targets as either the instruction or a label. both show up in this method.
	static CodeInstruction TargetOf(List<CodeInstruction> codes, object operand)
	{
		if (operand is CodeInstruction code && codes.Contains(code))
			return code;
		if (operand is Label label)
		{
			for (int i = 0; i < codes.Count; i++)
			{
				if (codes[i].labels.Contains(label))
					return codes[i];
			}
		}
		return null;
	}

	// field name match. the declaring type differs between the tick and MakeDiff, so the name is the stable part.
	static bool IsField(CodeInstruction ins, string name)
	{
		return ins.operand is FieldInfo field && field.Name == name;
	}

	// stfld of that field. used to find `FirstBaselineSent = true` after the send.
	static bool IsStore(CodeInstruction ins, string name)
	{
		return ins.opcode == OpCodes.Stfld && IsField(ins, name);
	}

	// call or callvirt whose method name matches. SequenceDiff is the tick compare.
	static bool IsCall(CodeInstruction ins, string name)
	{
		return ins.operand is MethodBase method && method.Name == name;
	}
}
