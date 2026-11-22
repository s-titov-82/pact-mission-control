using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Pact.Infrastructure.Skills;

internal static partial class SkillPackageStreams
{
	internal static void AppendHashes(IncrementalHash hash, string path)
	{
		foreach (var stream in NamedStreams(path))
		{
			hash.AppendData(Encoding.UTF8.GetBytes("S" + stream + "\0"));
			using var input = File.OpenRead(path + stream);
			hash.AppendData(SHA256.HashData(input));
		}
	}

	internal static void CopyDirectoryStreams(string source, string destination)
	{
		foreach (var stream in NamedStreams(source))
		{
			using var input = File.OpenRead(source + stream);
			using var output = File.Create(destination + stream);
			input.CopyTo(output);
		}
	}

	private static unsafe List<string> NamedStreams(string path)
	{
		var streams = new List<string>();
		if (!OperatingSystem.IsWindows())
		{ return streams; }
		var handle = FindFirstStream(path, 0, out var data, 0);
		if (handle == -1)
		{
			var error = Marshal.GetLastPInvokeError();
			if (error is 1 or 38)
			{ return streams; } // No named streams on this file/volume.
			throw StreamError(path, error);
		}
		try
		{
			do
			{
				var name = new string((char*)data.Name);
				if (name != "::$DATA")
				{ streams.Add(name); }
			} while (FindNextStream(handle, out data) != 0);
			var error = Marshal.GetLastPInvokeError();
			if (error != 38)
			{ throw StreamError(path, error); }
			streams.Sort(StringComparer.Ordinal);
			return streams;
		}
		finally { _ = FindClose(handle); }
	}

	private static IOException StreamError(string path, int error) =>
		new($"Could not inspect the data streams of {path}: {new Win32Exception(error).Message}");

	[StructLayout(LayoutKind.Sequential)]
	private unsafe struct StreamData
	{
		internal long Size;
		internal fixed ushort Name[296];
	}

	[LibraryImport("kernel32.dll", EntryPoint = "FindFirstStreamW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
	private static partial nint FindFirstStream(string path, int level, out StreamData data, uint flags);

	[LibraryImport("kernel32.dll", EntryPoint = "FindNextStreamW", SetLastError = true)]
	private static partial int FindNextStream(nint handle, out StreamData data);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	private static partial int FindClose(nint handle);
}