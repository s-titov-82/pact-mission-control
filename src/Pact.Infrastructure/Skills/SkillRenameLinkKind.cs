using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pact.Infrastructure.Skills;

internal static partial class SkillRenameLinkKind
{
	internal static bool IsSymbolic(string path)
	{
		if (!OperatingSystem.IsWindows())
		{ return true; }
		// Open the directory entry itself. Following it would report the target's attributes.
		const uint openDirectoryReparsePoint = 0x02000000 | 0x00200000;
		using var handle = OpenEntry(path, 0, FileShare.ReadWrite | FileShare.Delete, 0, FileMode.Open, openDirectoryReparsePoint, 0);
		if (handle.IsInvalid || ReadTag(handle, 9, out var info, (uint)Marshal.SizeOf<AttributeTag>()) == 0)
		{
			throw new IOException($"Could not inspect the link at {path}.", new Win32Exception(Marshal.GetLastPInvokeError()));
		}
		return info.Tag switch
		{
			0xA000000C => true, // IO_REPARSE_TAG_SYMLINK
			0xA0000003 => false, // IO_REPARSE_TAG_MOUNT_POINT
			_ => throw new IOException($"Unsupported reparse point at {path}.")
		};
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct AttributeTag
	{
		internal uint Attributes;
		internal uint Tag;
	}

	[LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
	private static partial SafeFileHandle OpenEntry(string path, uint access, FileShare share, nint security, FileMode disposition, uint flags, nint template);

	[LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
	private static partial int ReadTag(SafeFileHandle handle, int infoClass, out AttributeTag info, uint size);
}