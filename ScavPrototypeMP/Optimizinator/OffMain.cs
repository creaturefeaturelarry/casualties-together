using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using ICSharpCode.SharpZipLib.Checksum;
using ICSharpCode.SharpZipLib.Zip.Compression;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace Together;

// Plain work that used to sit inside the frame: gzip of the hot packets,
// sim-range cell tests, IK solves, limb vitals, and opus encode.
// The game thread only copies inputs and applies results.
internal static class OffMain
{
	const int GzipCap = 64;
	const int VoiceCap = 8;

	enum SendKind : byte { Client = 0, Server = 1 }

	sealed class GzipJob
	{
		internal byte[] Header;
		internal int HeaderLen;
		internal byte[] Payload;
		internal int PayloadLen;
		internal SendKind Kind;
		internal DeliveryMethod Delivery;
		internal knetid[] Who;
	}

	sealed class DoneSend
	{
		internal byte[] Packet;
		internal int Length;
		internal SendKind Kind;
		internal DeliveryMethod Delivery;
		internal knetid[] Who;
	}

	sealed class VoiceJob
	{
		internal float[] Samples;
		internal int Count;
		internal bool NotLast;
	}

	sealed class VoiceDone
	{
		internal byte[] Blob;
		internal int Samples;
		internal bool NotLast;
	}

	static readonly ConcurrentQueue<GzipJob> GzipIn = new ConcurrentQueue<GzipJob>();
	static readonly ConcurrentQueue<DoneSend> GzipOut = new ConcurrentQueue<DoneSend>();
	static readonly ConcurrentQueue<VoiceJob> VoiceIn = new ConcurrentQueue<VoiceJob>();
	static readonly ConcurrentQueue<VoiceDone> VoiceOut = new ConcurrentQueue<VoiceDone>();
	static readonly ConcurrentDictionary<int, Vector2> Watch = new ConcurrentDictionary<int, Vector2>();

	static Thread _thread;
	static volatile bool _stop;
	static readonly AutoResetEvent Wake = new AutoResetEvent(false);

	static float[] _prop = Array.Empty<float>();
	static float[] _enemy = Array.Empty<float>();
	static int _propN;
	static int _enemyN;
	static int _rectGen;
	static readonly Dictionary<int, byte> CoversA = new Dictionary<int, byte>(256);
	static readonly Dictionary<int, byte> CoversB = new Dictionary<int, byte>(256);
	static volatile Dictionary<int, byte> _covers = CoversA;
	static int _coverGen;

	static long _mainTicks;
	static long _mainMax;
	static long _gzipTicks;
	static long _ikTicks;
	static long _rangeTicks;
	static long _limbTicks;
	static long _opusTicks;
	static int _statusUs;

	static OffMain()
	{
		_thread = new Thread(Loop) { IsBackground = true, Name = "TogetherOffMain" };
		_thread.Start();
	}

	internal static int Cell(float x, float y)
	{
		int cx = (int)Math.Floor(x);
		int cy = (int)Math.Floor(y);
		return (cx << 16) ^ (cy & 0xffff);
	}

	internal static void SetRects(float[] prop, int propN, float[] enemy, int enemyN)
	{
		_prop = prop;
		_propN = propN;
		_enemy = enemy;
		_enemyN = enemyN;
		_rectGen++;
		Wake.Set();
	}

	internal static bool TryCover(int cell, bool enemy, out bool hit)
	{
		Dictionary<int, byte> map = _covers;
		if (map != null && map.TryGetValue(cell, out byte bits))
		{
			hit = enemy ? (bits & 2) != 0 : (bits & 1) != 0;
			return true;
		}
		hit = false;
		return false;
	}

	internal static void WatchCell(int cell, float x, float y)
	{
		Watch[cell] = new Vector2(x, y);
	}

	internal static bool SendClient(DeliveryMethod delivery, NetDataWriter writer)
	{
		return Send(writer, sizeof(NetmsgId), SendKind.Client, delivery, null);
	}

	internal static bool SendServer(DeliveryMethod delivery, NetDataWriter writer, int start, knetid who)
	{
		return Send(writer, start, SendKind.Server, delivery, new[] { who });
	}

	internal static bool SendServer(DeliveryMethod delivery, NetDataWriter writer, int start, IReadOnlyList<knetid> who)
	{
		if (who == null || who.Count == 0)
			return true;
		knetid[] copy = new knetid[who.Count];
		for (int i = 0; i < who.Count; i++)
			copy[i] = who[i];
		return Send(writer, start, SendKind.Server, delivery, copy);
	}

	static bool Send(NetDataWriter writer, int start, SendKind kind, DeliveryMethod delivery, knetid[] who)
	{
		if (writer == null || GzipIn.Count >= GzipCap)
			return false;
		int length = writer.Length;
		if (start < 0 || start > length)
			return false;
		byte[] data = writer.Data;
		if (data == null || data.Length < length)
			return false;
		var job = new GzipJob {
			Header = new byte[start],
			HeaderLen = start,
			Payload = new byte[length - start],
			PayloadLen = length - start,
			Kind = kind,
			Delivery = delivery,
			Who = who,
		};
		if (start > 0)
			Buffer.BlockCopy(data, 0, job.Header, 0, start);
		if (job.PayloadLen > 0)
			Buffer.BlockCopy(data, start, job.Payload, 0, job.PayloadLen);
		GzipIn.Enqueue(job);
		Wake.Set();
		return true;
	}

	internal static void QueueVoice(float[] samples, int count, bool notLast)
	{
		if (samples == null || count <= 0 || VoiceIn.Count >= VoiceCap)
			return;
		float[] copy = new float[count];
		Array.Copy(samples, copy, count);
		VoiceIn.Enqueue(new VoiceJob { Samples = copy, Count = count, NotLast = notLast });
		Wake.Set();
	}

	internal static void NoteStatus(int pollUs)
	{
		_statusUs = pollUs;
	}

	internal static void AddIk(long ticks) { Add(ref _ikTicks, ticks); }
	internal static void AddLimb(long ticks) { Add(ref _limbTicks, ticks); }
	internal static void AddMain(long ticks)
	{
		if (ticks <= 0)
			return;
		Interlocked.Add(ref _mainTicks, ticks);
		long prev = _mainMax;
		while (ticks > prev && Interlocked.CompareExchange(ref _mainMax, ticks, prev) != prev)
			prev = _mainMax;
	}

	static void Add(ref long slot, long ticks)
	{
		if (ticks > 0)
			Interlocked.Add(ref slot, ticks);
	}

	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
	internal static void Drain()
	{
		long t0 = Stopwatch.GetTimestamp();
		int n = 0;
		while (n < 128 && GzipOut.TryDequeue(out DoneSend done))
		{
			n++;
			Dispatch(done);
		}
		while (n < 128 && VoiceOut.TryDequeue(out VoiceDone voice))
		{
			n++;
			DispatchVoice(voice);
		}
		AddMain(Stopwatch.GetTimestamp() - t0);
	}

	static void Dispatch(DoneSend done)
	{
		if (done?.Packet == null || done.Length <= 0)
			return;
		var writer = new NetDataWriter();
		writer.Put(done.Packet, 0, done.Length);
		if (done.Kind == SendKind.Client)
			Net.Client_Send(done.Delivery, writer);
		else if (done.Who != null && done.Who.Length == 1)
			Net.Server_SendTo(done.Delivery, writer, done.Who[0]);
		else if (done.Who != null)
			Net.Server_SendToClients(done.Delivery, writer, done.Who);
	}

	static void DispatchVoice(VoiceDone voice)
	{
		if (voice?.Blob == null || voice.Blob.Length == 0 || !Net.IsConnected || Net.IsDedicatedServer)
			return;
		ushort rate = (ushort)Voicechat.SAMPLE_RATE;
		NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_VoiceChatBlob);
		writer.Put((byte)Array.IndexOf(AudioOpusEncoding.STANDART_FREQUENCIES, rate));
		writer.PutBytesWithLength(voice.Blob);
		writer.Put((ushort)voice.Samples);
		writer.Put(voice.NotLast);
		Net.Client_Send(DeliveryMethod.Unreliable, writer);
	}

	internal static string TakeReport()
	{
		long main = Interlocked.Exchange(ref _mainTicks, 0);
		long mainMax = Interlocked.Exchange(ref _mainMax, 0);
		long gzip = Interlocked.Exchange(ref _gzipTicks, 0);
		long ik = Interlocked.Exchange(ref _ikTicks, 0);
		long range = Interlocked.Exchange(ref _rangeTicks, 0);
		long limb = Interlocked.Exchange(ref _limbTicks, 0);
		long opus = Interlocked.Exchange(ref _opusTicks, 0);
		int status = _statusUs;
		double freq = Stopwatch.Frequency;
		string Ms(long ticks) => (ticks * 1000.0 / freq).ToString("0.00");
		return "offmain main=" + Ms(main) + "/" + Ms(mainMax)
			+ "ms worker gzip=" + Ms(gzip)
			+ " ik=" + Ms(ik)
			+ " range=" + Ms(range)
			+ " limb=" + Ms(limb)
			+ " opus=" + Ms(opus)
			+ " statusUs=" + status;
	}

	static void Loop()
	{
		Deflater deflater = new Deflater(Deflater.DEFAULT_COMPRESSION, true);
		Crc32 crc = new Crc32();
		byte[] scratch = new byte[8192];
		MemoryStream gzipOut = new MemoryStream(256);
		int seenRect = -1;
		while (!_stop)
		{
			bool slept = true;
			while (GzipIn.TryDequeue(out GzipJob job))
			{
				slept = false;
				long t0 = Stopwatch.GetTimestamp();
				FinishGzip(job, deflater, crc, scratch, gzipOut);
				Add(ref _gzipTicks, Stopwatch.GetTimestamp() - t0);
			}
			if (seenRect != _rectGen || !Watch.IsEmpty)
			{
				seenRect = _rectGen;
				long t0 = Stopwatch.GetTimestamp();
				ScoreCovers();
				Add(ref _rangeTicks, Stopwatch.GetTimestamp() - t0);
				slept = false;
			}
			while (VoiceIn.TryDequeue(out VoiceJob voice))
			{
				slept = false;
				long t0 = Stopwatch.GetTimestamp();
				FinishVoice(voice);
				Add(ref _opusTicks, Stopwatch.GetTimestamp() - t0);
			}
			if (slept)
				Wake.WaitOne(2);
		}
	}

	static void FinishGzip(GzipJob job, Deflater deflater, Crc32 crc, byte[] scratch, MemoryStream gzipOut)
	{
		try
		{
			KrokV5Optimization.ResettableDeflate.WriteGzipDedicated(deflater, crc, scratch, job.Payload, 0, job.PayloadLen, gzipOut);
			int n = (int)gzipOut.Length;
			if (n < 0 || n > ushort.MaxValue)
				return;
			byte[] gz = new byte[n];
			if (n > 0)
				Buffer.BlockCopy(gzipOut.GetBuffer(), 0, gz, 0, n);
			var writer = new NetDataWriter();
			if (job.HeaderLen > 0)
				writer.Put(job.Header, 0, job.HeaderLen);
			writer.PutBytesWithLength(gz);
			byte[] packet = new byte[writer.Length];
			Buffer.BlockCopy(writer.Data, 0, packet, 0, writer.Length);
			GzipOut.Enqueue(new DoneSend {
				Packet = packet,
				Length = packet.Length,
				Kind = job.Kind,
				Delivery = job.Delivery,
				Who = job.Who,
			});
		}
		catch (Exception ex)
		{
			Plugin.Logger.LogWarning("[offmain] gzip " + ex.GetType().Name);
		}
	}

	static void FinishVoice(VoiceJob job)
	{
		try
		{
			byte[] blob = Voicechat.EncodeOffThread(job.Samples);
			if (blob == null || blob.Length == 0)
				return;
			VoiceOut.Enqueue(new VoiceDone { Blob = blob, Samples = job.Count, NotLast = job.NotLast });
		}
		catch (Exception ex)
		{
			Plugin.Logger.LogWarning("[offmain] opus " + ex.GetType().Name);
		}
	}

	static void ScoreCovers()
	{
		float[] prop = _prop;
		float[] enemy = _enemy;
		int propN = _propN;
		int enemyN = _enemyN;
		if (Watch.Count > 4096)
			Watch.Clear();
		if (Watch.IsEmpty)
			return;
		Dictionary<int, byte> next = _coverGen == 0 ? CoversA : CoversB;
		next.Clear();
		foreach (KeyValuePair<int, Vector2> pair in Watch)
		{
			byte bits = 0;
			if (Hit(prop, propN, pair.Value.x, pair.Value.y))
				bits |= 1;
			if (Hit(enemy, enemyN, pair.Value.x, pair.Value.y))
				bits |= 2;
			next[pair.Key] = bits;
		}
		_covers = next;
		_coverGen ^= 1;
	}

	static bool Hit(float[] rects, int n, float x, float y)
	{
		if (rects == null)
			return false;
		int count = n * 4;
		if (count > rects.Length)
			count = rects.Length;
		for (int i = 0; i + 3 < count; i += 4)
		{
			if (x >= rects[i] && x <= rects[i + 2] && y >= rects[i + 1] && y <= rects[i + 3])
				return true;
		}
		return false;
	}
}
