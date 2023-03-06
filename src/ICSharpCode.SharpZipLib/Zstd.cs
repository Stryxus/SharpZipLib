using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Zstd.Net;

public class ZStdException : Exception
{
	public ZStdException(string message) : base(message) { }
	public ZStdException(Exception ex) : base("Error processing file", ex) { }
}

public class InputStream : Stream
{
	private readonly Stream _inputStream;
	private readonly bool _leaveOpen;
	private readonly IntPtr _zst;
	private readonly byte[] _inputBufferArray;
	private bool _closed;
	private int _inputArrayPosition;
	private int _inputArraySize;
	private bool _depleted;

	public static bool IsZstdStream(byte[] buffBytes, long buffLen)
	{
		//0xFD2FB528 LE
		return buffLen > 3
			&& buffBytes[0] == 0x28
			&& buffBytes[1] == 0xB5
			&& buffBytes[2] == 0x2F
			&& buffBytes[3] == 0xFD;
	}

	public InputStream(Stream inputStream, bool leaveOpen = false)
	{
		_inputStream = inputStream;
		_leaveOpen = leaveOpen;
		_zst = Zstd.Library.CreateDStream();
		Zstd.CheckError(Zstd.Library.InitDStream(_zst));
		_inputBuffer.Size = Zstd.Library.DStreamInSize();
		_inputBufferArray = new byte[(int)_inputBuffer.Size.ToUInt32()];
		_outputBuffer.Size = Zstd.Library.DStreamOutSize();
	}

	protected override void Dispose(bool disposing)
	{
		if (_closed) return;
		Zstd.CheckError(Zstd.Library.FreeDStream(_zst));
		if (!_leaveOpen) _inputStream.Dispose();
		_closed = true;
		base.Dispose(disposing);
	}

	public override void Flush() => throw new NotSupportedException();
	public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
	public override void SetLength(long value) => throw new NotSupportedException();

	public override int Read(byte[] buffer, int offset, int count)
	{
		if (count == 0) return 0;
		int retVal = 0;
		GCHandle alloc1 = GCHandle.Alloc(_inputBufferArray, GCHandleType.Pinned);
		GCHandle alloc2 = GCHandle.Alloc(buffer, GCHandleType.Pinned);
		try
		{
			while (count > 0)
			{
				int left = _inputArraySize - _inputArrayPosition;
				if (left <= 0 && !_depleted)
				{
					_inputArrayPosition = 0;
					_inputArraySize = left = _inputStream.Read(_inputBufferArray, 0, _inputBufferArray.Length);
					// no more data at all
					if (left <= 0)
					{
						left = 0;
						_depleted = true;
					}
				}
				_inputBuffer.Position = UIntPtr.Zero;
				if (_depleted)
				{
					_inputBuffer.Size = UIntPtr.Zero;
					_inputBuffer.Data = IntPtr.Zero;
				}
				else
				{
					_inputBuffer.Size = new((uint)left);
					_inputBuffer.Data = Marshal.UnsafeAddrOfPinnedArrayElement(_inputBufferArray, _inputArrayPosition);
				}

				_outputBuffer.Position = UIntPtr.Zero;
				_outputBuffer.Size = new((uint)count);
				_outputBuffer.Data = Marshal.UnsafeAddrOfPinnedArrayElement(buffer, offset);
				Zstd.CheckError(Zstd.Library.DecompressStream(_zst, _outputBuffer, _inputBuffer));
				int bytesProduced = (int)_outputBuffer.Position.ToUInt32();
				if (bytesProduced == 0 && _depleted) break;
				retVal += bytesProduced;
				count -= bytesProduced;
				offset += bytesProduced;
				if (_depleted) continue;
				int bytesConsumed = (int)_inputBuffer.Position.ToUInt32();
				_inputArrayPosition += bytesConsumed;
			}
			return retVal;
		}
		catch (Exception ex) { throw new ZStdException(ex); }
		finally
		{
			alloc1.Free();
			alloc2.Free();
		}
	}

	private readonly Zstd.Buffer _inputBuffer = new();
	private readonly Zstd.Buffer _outputBuffer = new();

	public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

	public override bool CanRead => _inputStream.CanRead;
	public override bool CanSeek => false;
	public override bool CanWrite => false;
	public override long Length => 0;
	public override long Position 
	{
		get => 0;
		set => throw new NotSupportedException();
	}

}

internal static class Zstd
{
	internal static IImportZstd Library { get; }

	static Zstd()
	{
		Library = new Zstd_x64();
	}

	internal static void CheckError(UIntPtr x)
	{
		int code = Library.IsError(x);
		if (code == 0) return;
		throw new ZStdException($"Error {x}:{code}");
	}

	[StructLayout(LayoutKind.Sequential)]
	internal sealed class Buffer
	{
		public IntPtr Data;
		public UIntPtr Size;
		public UIntPtr Position;
	}

	// https://github.com/facebook/zstd/blob/dev/lib/zstd.h
	// https://facebook.github.io/zstd/zstd_manual.html
	// https://github.com/facebook/zstd/blob/master/doc/zstd_compression_format.md

	internal interface IImportZstd
	{
		int GetMaxCompessionLevel();
		int GetVersionNumber();
		string GetVersionString();

		int IsError(UIntPtr code);
		string GetErrorName(UIntPtr code);

		IntPtr CreateCStream();
		UIntPtr FreeCStream(IntPtr zcs);
		UIntPtr InitCStream(IntPtr zcs, int compressionLevel);
		UIntPtr CompressStream(IntPtr zcs, Buffer outputBuffer, Buffer inputBuffer);
		UIntPtr CStreamInSize();
		UIntPtr CStreamOutSize();

		IntPtr CreateDStream();
		UIntPtr FreeDStream(IntPtr zcs);
		UIntPtr InitDStream(IntPtr zcs);
		UIntPtr DecompressStream(IntPtr zcs, Buffer outputBuffer, Buffer inputBuffer);
		UIntPtr DStreamInSize();
		UIntPtr DStreamOutSize();

		UIntPtr FlushStream(IntPtr zcs, Buffer outputBuffer);
		UIntPtr EndStream(IntPtr zcs, Buffer outputBuffer);
	}

	private class Zstd_x64 : IImportZstd
	{
		#region DllImports

		private const string DllName = "libzstd";

		[DllImport(DllName, EntryPoint = "ZSTD_maxCLevel", CallingConvention = CallingConvention.Cdecl)]
		private static extern int zstd_GetMaxCompessionLevel();

		[DllImport(DllName, EntryPoint = "ZSTD_versionNumber", CallingConvention = CallingConvention.Cdecl)]
		private static extern int zstd_GetVersionNumber();

		//[DllImport(DllName, EntryPoint = "ZSTD_versionString", CallingConvention = CallingConvention.Cdecl)]
		//public static extern string zstd_versionString();

		[DllImport(DllName, EntryPoint = "ZSTD_isError", CallingConvention = CallingConvention.Cdecl)]
		private static extern int zstd_isError(UIntPtr code);

		[DllImport(DllName, EntryPoint = "ZSTD_getErrorName", CallingConvention = CallingConvention.Cdecl)]
		private static extern string zstd_getErrorName(UIntPtr code);

		[DllImport(DllName, EntryPoint = "ZSTD_createCStream", CallingConvention = CallingConvention.Cdecl)]
		private static extern IntPtr zstd_createCStream();

		[DllImport(DllName, EntryPoint = "ZSTD_freeCStream", CallingConvention = CallingConvention.Cdecl)]
		private static extern UIntPtr zstd_freeCStream(IntPtr zcs);

		[DllImport(DllName, EntryPoint = "ZSTD_initCStream", CallingConvention = CallingConvention.Cdecl)]
		private static extern UIntPtr zstd_initCStream(IntPtr zcs, int compressionLevel);

		[DllImport(DllName, EntryPoint = "ZSTD_compressStream", CallingConvention = CallingConvention.Cdecl)]
		private static extern UIntPtr zstd_compressStream(IntPtr zcs,
			[MarshalAs(UnmanagedType.LPStruct)] Buffer outputBuffer,
			[MarshalAs(UnmanagedType.LPStruct)] Buffer inputBuffer);

		[DllImport(DllName, EntryPoint = "ZSTD_flushStream", CallingConvention = CallingConvention.Cdecl)]
		private static extern UIntPtr zstd_flushStream(IntPtr zcs,
			[MarshalAs(UnmanagedType.LPStruct)] Buffer outputBuffer);

		[DllImport(DllName, EntryPoint = "ZSTD_endStream", CallingConvention = CallingConvention.Cdecl)]
		private static extern UIntPtr zstd_endStream(IntPtr zcs,
			[MarshalAs(UnmanagedType.LPStruct)] Buffer outputBuffer);

		[DllImport(DllName, EntryPoint = "ZSTD_CStreamInSize", CallingConvention = CallingConvention.Cdecl)]
		private static extern UIntPtr zstd_CStreamInSize();

		[DllImport(DllName, EntryPoint = "ZSTD_CStreamOutSize", CallingConvention = CallingConvention.Cdecl)]
		private static extern UIntPtr zstd_CStreamOutSize();

		[DllImport(DllName, EntryPoint = "ZSTD_createDStream", CallingConvention = CallingConvention.Cdecl)]
		private static extern IntPtr zstd_createDStream();

		[DllImport(DllName, EntryPoint = "ZSTD_freeDStream", CallingConvention = CallingConvention.Cdecl)]
		private static extern UIntPtr zstd_freeDStream(IntPtr zcs);

		[DllImport(DllName, EntryPoint = "ZSTD_initDStream", CallingConvention = CallingConvention.Cdecl)]
		private static extern UIntPtr zstd_initDStream(IntPtr zcs);

		[DllImport(DllName, EntryPoint = "ZSTD_decompressStream", CallingConvention = CallingConvention.Cdecl)]
		private static extern UIntPtr zstd_decompressStream(IntPtr zcs,
			[MarshalAs(UnmanagedType.LPStruct)] Buffer outputBuffer,
			[MarshalAs(UnmanagedType.LPStruct)] Buffer inputBuffer);

		[DllImport(DllName, EntryPoint = "ZSTD_DStreamInSize", CallingConvention = CallingConvention.Cdecl)]
		private static extern UIntPtr zstd_DStreamInSize();

		[DllImport(DllName, EntryPoint = "ZSTD_CStreamOutSize", CallingConvention = CallingConvention.Cdecl)]
		private static extern UIntPtr zstd_DStreamOutSize();

		#endregion

		#region IImportZstd Implementation

		public int GetMaxCompessionLevel() { return zstd_GetMaxCompessionLevel(); }

		public int GetVersionNumber() { return zstd_GetVersionNumber(); }

		public string GetVersionString()
		{
			int n = zstd_GetVersionNumber();
			return string.Format("{0}.{1}.{2}", n / 10000, (n % 10000) / 100, n % 100);
		}

		public int IsError(UIntPtr code) { return zstd_isError(code); }

		public string GetErrorName(UIntPtr code) { return zstd_getErrorName(code); }

		public IntPtr CreateCStream() { return zstd_createCStream(); }

		public UIntPtr FreeCStream(IntPtr zcs) { return zstd_freeCStream(zcs); }

		public UIntPtr InitCStream(IntPtr zcs, int compressionLevel) { return zstd_initCStream(zcs, compressionLevel); }

		public UIntPtr CompressStream(IntPtr zcs, Buffer outputBuffer, Buffer inputBuffer) { return zstd_compressStream(zcs, outputBuffer, inputBuffer); }

		public UIntPtr FlushStream(IntPtr zcs, Buffer outputBuffer) { return zstd_flushStream(zcs, outputBuffer); }

		public UIntPtr EndStream(IntPtr zcs, Buffer outputBuffer) { return zstd_endStream(zcs, outputBuffer); }

		public UIntPtr CStreamInSize() { return zstd_CStreamInSize(); }

		public UIntPtr CStreamOutSize() { return zstd_CStreamOutSize(); }

		public IntPtr CreateDStream() { return zstd_createDStream(); }

		public UIntPtr FreeDStream(IntPtr zcs) { return zstd_freeDStream(zcs); }

		public UIntPtr InitDStream(IntPtr zcs) { return zstd_initDStream(zcs); }

		public UIntPtr DecompressStream(IntPtr zcs, Buffer outputBuffer, Buffer inputBuffer) { return zstd_decompressStream(zcs, outputBuffer, inputBuffer); }

		public UIntPtr DStreamInSize() { return zstd_DStreamInSize(); }

		public UIntPtr DStreamOutSize() { return zstd_DStreamOutSize(); }

		#endregion
	}
}
