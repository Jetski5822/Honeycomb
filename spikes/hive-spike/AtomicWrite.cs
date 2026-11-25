using System.IO.Abstractions;

// Measures how reliably "write a temp file then replace the old file" works on the real file system.
static class AtomicWrite
{
	public static void Run()
	{
		var fs = new FileSystem();
		foreach (var (name, replace) in new (string, Action<IFileSystem, string, string>)[]
		{
			("File.Move(temp, dest, overwrite: true)", (f, tmp, dest) => f.File.Move(tmp, dest, overwrite: true)),
			("File.Replace(temp, dest, null)",         (f, tmp, dest) => { if (f.File.Exists(dest)) f.File.Replace(tmp, dest, null); else f.File.Move(tmp, dest); }),
			("File.Copy(temp, dest, true) + delete",   (f, tmp, dest) => { f.File.Copy(tmp, dest, true); f.File.Delete(tmp); }),
		})
		{
			var dir = Path.Combine(Path.GetTempPath(), "atomic-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dir);
			var dest = Path.Combine(dir, "checkpoint.json");
			int ok = 0, denied = 0, other = 0; var firstFail = -1;
			for (var i = 0; i < 300; i++)
			{
				var tmp = dest + ".tmp";
				try
				{
					fs.File.WriteAllText(tmp, $"{{\"n\":{i}}}");
					replace(fs, tmp, dest);
					if (fs.File.ReadAllText(dest) != $"{{\"n\":{i}}}") other++; else ok++;
				}
				catch (UnauthorizedAccessException) { denied++; if (firstFail < 0) firstFail = i; try { fs.File.Delete(tmp); } catch { } }
				catch (IOException) { other++; try { fs.File.Delete(tmp); } catch { } }
			}
			Console.WriteLine($"{name,-42} ok={ok} access-denied={denied} other-io={other} firstFailAt={firstFail}");
			try { Directory.Delete(dir, true); } catch { }
		}
	}
}

static class AtomicFresh
{
	public static void Run()
	{
		var fs = new System.IO.Abstractions.FileSystem();
		foreach (var delayMs in new[] { 0, 50, 500 })
		{
			int ok = 0, denied = 0;
			for (var i = 0; i < 40; i++)
			{
				var dir = Path.Combine(Path.GetTempPath(), "fresh-" + Guid.NewGuid().ToString("N"));
				var store = new SnapshotStore(fs, dir);
				var a = new Snapshot(1, RunId.From(Guid.NewGuid()), "a", DateTimeOffset.UtcNow, null, null, [], []);
				try
				{
					store.Save(a);
					if (delayMs > 0) Thread.Sleep(delayMs);
					store.Save(a with { Question = "b" });
					ok++;
				}
				catch (UnauthorizedAccessException) { denied++; }
				try { Directory.Delete(dir, true); } catch { }
			}
			Console.WriteLine($"fresh directory, two saves, {delayMs,3} ms apart: ok={ok} access-denied={denied} of 40 (total retries used so far: {SnapshotStore.RetriesUsed})");
		}
	}
}
