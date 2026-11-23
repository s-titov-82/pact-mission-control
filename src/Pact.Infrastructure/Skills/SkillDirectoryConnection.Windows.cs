using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pact.Infrastructure.Skills;

internal static partial class SkillDirectoryConnection
{
	private static unsafe void CreateJunction(string path, string destination)
	{
		const uint mountPointTag = 0xA0000003;
		const uint setReparsePoint = 0x000900A4;
		const uint genericWrite = 0x40000000;
		const uint openDirectoryReparsePoint = 0x02000000 | 0x00200000;
		const int reparseHeaderSize = 8;
		const int mountPointHeaderSize = 8;
		const int maxReparseBufferSize = 16_384;

		var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
		var substitute = @"\??\" + ExtendedPath(target)[4..];
		var substituteLength = Encoding.Unicode.GetByteCount(substitute);
		var targetLength = Encoding.Unicode.GetByteCount(target);
		var targetOffset = substituteLength + sizeof(char);
		var pathOffset = reparseHeaderSize + mountPointHeaderSize;
		var bufferSize = pathOffset + targetOffset + targetLength + sizeof(char);
		if (bufferSize > maxReparseBufferSize)
		{
			throw new IOException($"The junction target path is too long: {target}.");
		}

		// REPARSE_DATA_BUFFER / MountPointReparseBuffer offsets are byte counts
		// relative to PathBuffer. Both UTF-16 names are followed by a null.
		var buffer = new byte[bufferSize];
		BinaryPrimitives.WriteUInt32LittleEndian(buffer, mountPointTag);
		BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), (ushort)(bufferSize - reparseHeaderSize));
		BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), (ushort)substituteLength);
		BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), (ushort)targetOffset);
		BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), (ushort)targetLength);
		Encoding.Unicode.GetBytes(substitute, buffer.AsSpan(pathOffset));
		Encoding.Unicode.GetBytes(target, buffer.AsSpan(pathOffset + targetOffset));

		Directory.CreateDirectory(path);
		using var handle = OpenJunction(ExtendedPath(path), genericWrite, FileShare.ReadWrite | FileShare.Delete,
			0, FileMode.Open, openDirectoryReparsePoint, 0);
		fixed (byte* input = buffer)
		{
			if (handle.IsInvalid || SetJunction(handle, setReparsePoint, input, (uint)buffer.Length, 0, 0, out _, 0) == 0)
			{
				throw new IOException($"Could not create the skill junction at {path} to {target}.", new Win32Exception(Marshal.GetLastPInvokeError()));
			}
		}
	}

	internal static string ExtendedPath(string path)
	{
		var fullPath = Path.GetFullPath(path);
		return fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? fullPath :
			fullPath.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + fullPath[2..] : @"\\?\" + fullPath;
	}

	[LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
	private static partial SafeFileHandle OpenJunction(string path, uint access, FileShare share, nint security,
		FileMode disposition, uint flags, nint template);

	[LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
	private static unsafe partial int SetJunction(SafeFileHandle file, uint code, byte* input, uint inputSize,
		nint output, uint outputSize, out uint bytesReturned, nint overlapped);
}
