using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Web.WebView2.Core;

namespace Pact.App.Avalonia.Web;

internal interface IWebView2ProcessInfoReader
{
	Task<WebViewProcessAttribution> ReadAsync(
		NativeWebView webView,
		CancellationToken cancellationToken);
}

internal sealed class WindowsWebView2ProcessInfoReader : IWebView2ProcessInfoReader
{
	private const int InterfaceNotSupported = unchecked((int)0x80004002);
	private const int BrowserProcessIdVtableSlot = 37;
	private const int EnvironmentVtableSlot = 67;
	private const int FrameIdVtableSlot = 121;
	private const int GetProcessExtendedInfosVtableSlot = 25;
	private const int FrameInfoCollectionIteratorVtableSlot = 3;
	private const int FrameInfoIteratorHasCurrentVtableSlot = 3;
	private const int FrameInfoIteratorCurrentVtableSlot = 4;
	private const int FrameInfoIteratorMoveNextVtableSlot = 5;
	private const int FrameInfo2ParentVtableSlot = 5;
	private const int FrameInfo2IdVtableSlot = 6;
	private const int MaximumFrameDepth = 128;
	private static readonly Guid Core2InterfaceId =
		new("9E8F0CF8-E670-4B5E-B2BC-73E061E3184C");
	private static readonly Guid Core20InterfaceId =
		new("B4BC1926-7305-11EE-B962-0242AC120002");
	private static readonly Guid Environment13InterfaceId =
		new("AF641F58-72B2-11EE-B962-0242AC120002");
	private static readonly Guid FrameInfo2InterfaceId =
		new("56F85CFA-72C4-11EE-B962-0242AC120002");

	public async Task<WebViewProcessAttribution> ReadAsync(
		NativeWebView webView,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(webView);
		cancellationToken.ThrowIfCancellationRequested();
		EnsureUiThread();
		if (webView.TryGetPlatformHandle() is not IWindowsWebView2PlatformHandle platformHandle)
		{
			throw new InvalidOperationException(
				"The selected web tab has no active WebView2 adapter.");
		}

		var corePointer = platformHandle.CoreWebView2;
		if (corePointer == IntPtr.Zero)
		{
			throw new InvalidOperationException(
				"The selected web tab has no active WebView2 adapter.");
		}

		var environmentPointer = IntPtr.Zero;
		try
		{
			var browserProcessId = ReadUInt32(corePointer, BrowserProcessIdVtableSlot);
			try
			{
				var selectedFrameId = ReadUInt32(
					QueryRequiredInterface(corePointer, Core20InterfaceId),
					FrameIdVtableSlot,
					releasePointer: true);
				if (selectedFrameId == 0)
				{
					throw new InvalidOperationException(
						"WebView2 has not assigned a frame identifier to the selected tab yet.");
				}

				environmentPointer = GetEnvironmentPointer(corePointer);
				var completion = StartProcessEnumeration(environmentPointer);
				var collection = await completion
					.WaitAsync(cancellationToken);
				EnsureUiThread();
				return WebViewProcessAttributionClassifier.Classify(
					selectedFrameId,
					ReadProcesses(collection));
			}
			catch (RequiredWebView2InterfaceUnavailableException)
			{
				return CreateAggregateAttribution(browserProcessId);
			}
		}
		finally
		{
			if (environmentPointer != IntPtr.Zero)
			{
				Marshal.Release(environmentPointer);
			}

			Marshal.Release(corePointer);
		}
	}

	private static unsafe Task<IWebView2ProcessExtendedInfoCollectionNative> StartProcessEnumeration(
		IntPtr environmentPointer)
	{
		var environment13Pointer = QueryRequiredInterface(
			environmentPointer,
			Environment13InterfaceId);
		try
		{
			ProcessExtendedInfosCompletion completion = new();
			var callbackPointer = ComInterfaceMarshaller<
				IWebView2GetProcessExtendedInfosCompletedHandlerNative>.ConvertToUnmanaged(completion);
			try
			{
				var method = GetVtableDelegate<GetProcessExtendedInfos>(
					environment13Pointer,
					GetProcessExtendedInfosVtableSlot);
				Marshal.ThrowExceptionForHR(method(
					environment13Pointer,
					(IntPtr)callbackPointer));
				return completion.Task;
			}
			finally
			{
				ComInterfaceMarshaller<
					IWebView2GetProcessExtendedInfosCompletedHandlerNative>.Free(callbackPointer);
			}
		}
		finally
		{
			Marshal.Release(environment13Pointer);
		}
	}

	private static List<WebViewRuntimeProcessInfo> ReadProcesses(
		IWebView2ProcessExtendedInfoCollectionNative collection)
	{
		List<WebViewRuntimeProcessInfo> processes = [];
		for (uint index = 0; index < collection.GetCount(); index++)
		{
			var extendedInfo = collection.GetValueAtIndex(index);
			var processInfo = extendedInfo.GetProcessInfo();
			var processId = processInfo.GetProcessId();
			if (processId <= 0)
			{
				continue;
			}

			var kind = MapKind(processInfo.GetKind());
			processes.Add(new(
				processId,
				kind,
				kind == WebViewRuntimeProcessKind.Renderer
					? ReadRootFrameIds(extendedInfo.GetAssociatedFrameInfos())
					: []));
		}

		return processes;
	}

	private static uint[] ReadRootFrameIds(IWebView2FrameInfoCollectionNative frames)
	{
		if (!ComWrappers.TryGetComInstance(frames, out var framesPointer))
		{
			throw new InvalidOperationException(
				"WebView2 returned a non-COM frame collection wrapper.");
		}

		try
		{
			var iteratorPointer = ReadComPointer(
				framesPointer,
				FrameInfoCollectionIteratorVtableSlot);
			try
			{
				HashSet<uint> rootFrameIds = [];
				while (ReadInt32(
					iteratorPointer,
					FrameInfoIteratorHasCurrentVtableSlot) != 0)
				{
					var rootFrameId = ReadRootFrameId(ReadComPointer(
						iteratorPointer,
						FrameInfoIteratorCurrentVtableSlot));
					if (rootFrameId != 0)
					{
						rootFrameIds.Add(rootFrameId);
					}

					if (ReadInt32(
						iteratorPointer,
						FrameInfoIteratorMoveNextVtableSlot) == 0)
					{
						break;
					}
				}

				return rootFrameIds.ToArray();
			}
			finally
			{
				Marshal.Release(iteratorPointer);
			}
		}
		finally
		{
			Marshal.Release(framesPointer);
		}
	}

	private static uint ReadRootFrameId(IntPtr framePointer)
	{
		var currentPointer = framePointer;
		uint frameId = 0;
		try
		{
			for (var depth = 0;
				currentPointer != IntPtr.Zero && depth < MaximumFrameDepth;
				depth++)
			{
				var extendedPointer = QueryRequiredInterface(
					currentPointer,
					FrameInfo2InterfaceId);
				IntPtr parentPointer;
				try
				{
					frameId = ReadUInt32(extendedPointer, FrameInfo2IdVtableSlot);
					parentPointer = ReadComPointer(
						extendedPointer,
						FrameInfo2ParentVtableSlot);
				}
				finally
				{
					Marshal.Release(extendedPointer);
				}

				Marshal.Release(currentPointer);
				currentPointer = parentPointer;
			}

			return frameId;
		}
		finally
		{
			if (currentPointer != IntPtr.Zero)
			{
				Marshal.Release(currentPointer);
			}
		}
	}

	private static IntPtr GetEnvironmentPointer(IntPtr corePointer)
	{
		var core2Pointer = QueryRequiredInterface(corePointer, Core2InterfaceId);
		try
		{
			return ReadComPointer(core2Pointer, EnvironmentVtableSlot);
		}
		finally
		{
			Marshal.Release(core2Pointer);
		}
	}

	private static uint ReadUInt32(IntPtr pointer, int vtableSlot, bool releasePointer = false)
	{
		try
		{
			var method = GetVtableDelegate<GetUInt32>(pointer, vtableSlot);
			Marshal.ThrowExceptionForHR(method(pointer, out var value));
			return value;
		}
		finally
		{
			if (releasePointer)
			{
				Marshal.Release(pointer);
			}
		}
	}

	private static int ReadInt32(IntPtr pointer, int vtableSlot)
	{
		var method = GetVtableDelegate<GetInt32>(pointer, vtableSlot);
		Marshal.ThrowExceptionForHR(method(pointer, out var value));
		return value;
	}

	private static IntPtr ReadComPointer(IntPtr pointer, int vtableSlot)
	{
		var method = GetVtableDelegate<GetComPointer>(pointer, vtableSlot);
		Marshal.ThrowExceptionForHR(method(pointer, out var value));
		return value;
	}

	private static IntPtr QueryRequiredInterface(IntPtr pointer, Guid interfaceId)
	{
		var result = Marshal.QueryInterface(
			pointer,
			in interfaceId,
			out var interfacePointer);
		if (result == InterfaceNotSupported)
		{
			throw new RequiredWebView2InterfaceUnavailableException(interfaceId);
		}

		Marshal.ThrowExceptionForHR(result);
		return interfacePointer;
	}

	private static T GetVtableDelegate<T>(IntPtr pointer, int slot) where T : Delegate
	{
		var vtable = Marshal.ReadIntPtr(pointer);
		var methodPointer = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
		return Marshal.GetDelegateForFunctionPointer<T>(methodPointer);
	}

	private static WebViewProcessAttribution CreateAggregateAttribution(uint browserProcessId)
	{
		if (browserProcessId == 0 || browserProcessId > int.MaxValue)
		{
			throw new InvalidOperationException(
				"WebView2 returned no usable browser process identifier.");
		}

		var rootProcessId = checked((int)browserProcessId);
		return new(
			PageProcessIds: [],
			SharedProcessIds: [rootProcessId],
			PageAttributionAvailable: false,
			RuntimeRootProcessId: rootProcessId);
	}

	private static void EnsureUiThread()
	{
		if (!Dispatcher.UIThread.CheckAccess())
		{
			throw new InvalidOperationException(
				"WebView2 process enumeration must remain on the UI thread.");
		}
	}

	private static WebViewRuntimeProcessKind MapKind(int kind) => kind switch
	{
		(int)CoreWebView2ProcessKind.Browser => WebViewRuntimeProcessKind.Browser,
		(int)CoreWebView2ProcessKind.Renderer => WebViewRuntimeProcessKind.Renderer,
		(int)CoreWebView2ProcessKind.Utility => WebViewRuntimeProcessKind.Utility,
		(int)CoreWebView2ProcessKind.SandboxHelper => WebViewRuntimeProcessKind.SandboxHelper,
		(int)CoreWebView2ProcessKind.Gpu => WebViewRuntimeProcessKind.Gpu,
		(int)CoreWebView2ProcessKind.PpapiPlugin => WebViewRuntimeProcessKind.Plugin,
		(int)CoreWebView2ProcessKind.PpapiBroker => WebViewRuntimeProcessKind.PluginBroker,
		_ => WebViewRuntimeProcessKind.Utility
	};

	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	private delegate int GetUInt32(IntPtr instance, out uint value);

	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	private delegate int GetInt32(IntPtr instance, out int value);

	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	private delegate int GetComPointer(IntPtr instance, out IntPtr value);

	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	private delegate int GetProcessExtendedInfos(IntPtr instance, IntPtr callbackPointer);

	private sealed class RequiredWebView2InterfaceUnavailableException(Guid interfaceId) :
		Exception($"WebView2 does not expose required interface {interfaceId}.");
}

[GeneratedComInterface]
[Guid("F45E55AA-3BC2-11EE-BE56-0242AC120002")]
internal partial interface IWebView2GetProcessExtendedInfosCompletedHandlerNative
{
	void Invoke(
		int errorCode,
		IWebView2ProcessExtendedInfoCollectionNative? result);
}

[GeneratedComClass]
internal sealed partial class ProcessExtendedInfosCompletion :
	IWebView2GetProcessExtendedInfosCompletedHandlerNative
{
	private readonly TaskCompletionSource<IWebView2ProcessExtendedInfoCollectionNative> _completion =
		new(TaskCreationOptions.RunContinuationsAsynchronously);

	public Task<IWebView2ProcessExtendedInfoCollectionNative> Task => _completion.Task;

	public void Invoke(
		int errorCode,
		IWebView2ProcessExtendedInfoCollectionNative? result)
	{
		if (errorCode < 0)
		{
			_completion.TrySetException(
				Marshal.GetExceptionForHR(errorCode)
				?? new InvalidOperationException(
					$"WebView2 process enumeration failed with HRESULT 0x{errorCode:X8}."));
			return;
		}

		if (result is null)
		{
			_completion.TrySetException(
				new InvalidOperationException("WebView2 returned no process collection."));
			return;
		}

		_completion.TrySetResult(result);
	}
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("32EFA696-407A-11EE-BE56-0242AC120002")]
internal partial interface IWebView2ProcessExtendedInfoCollectionNative
{
	uint GetCount();

	IWebView2ProcessExtendedInfoNative GetValueAtIndex(uint index);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("AF4C4C2E-45DB-11EE-BE56-0242AC120002")]
internal partial interface IWebView2ProcessExtendedInfoNative
{
	IWebView2ProcessInfoNative GetProcessInfo();

	IWebView2FrameInfoCollectionNative GetAssociatedFrameInfos();
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("84FA7612-3F3D-4FBF-889D-FAD000492D72")]
internal partial interface IWebView2ProcessInfoNative
{
	int GetProcessId();

	int GetKind();
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("8F834154-D38E-4D90-AFFB-6800A7272839")]
internal partial interface IWebView2FrameInfoCollectionNative
{
}
