#nullable enable
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Lidgren.Network
{

	public static partial class NativeSocket
	{

		const int SockAddrStorageSize = 128; // sockaddr_storage

#if !__ANDROID__ && !__CONSTRAINED__ && !WINDOWS_RUNTIME && !UNITY_STANDALONE_LINUX
		private const bool IsWindows = true;
#else
		private  const bool IsWindows = false;
#endif

		[DllImport(IsWindows ? "ws2_32" : "libc", EntryPoint = "sendto")]
		internal static extern unsafe int _SendTo(int socket, byte* buf, int len, int flags, void* to, int toLen);

		[DllImport(IsWindows ? "ws2_32" : "libc", EntryPoint = "recvfrom")]
		internal static extern unsafe int _RecvFrom(int socket, byte* buf, int len, int flags, void* from, int* fromLen);

		public static int GetError(Socket socket)
			=> (int) socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error);

		public static unsafe int SendTo(this Socket socket, ReadOnlySpan<byte> buffer, int flags, IPEndPoint to)
		{
			switch (to.AddressFamily)
			{
				default:
					throw new NotImplementedException(to.AddressFamily.ToString());

				case AddressFamily.InterNetwork:
				{
					var error = 0;
					var pTo = stackalloc SockAddrIn[1];
					var port = (ushort) to.Port;
					pTo->SocketAddressFamily = (ushort) to.AddressFamily;
					pTo->Port = BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(port) : port;
					if (!to.Address.TryWriteBytes(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref pTo->InAddr, 1)), out _))
						throw new NotImplementedException("Can't write address.");

					int result;
					fixed (byte* pBuf = buffer)
					{
						result = _SendTo(socket.Handle.ToInt32(), pBuf, buffer.Length, flags, pTo, sizeof(SockAddrIn));
					}

					if (result == -1)
					{
						error = GetError(socket);
						if (error == 0)
							error = Marshal.GetLastWin32Error();
					}

					if (result == -1)
						throw new SocketException(error);

					return result;
				}

				case AddressFamily.InterNetworkV6:
				{
					var error = 0;
					var pTo = stackalloc SockAddrIn6[1];
					var port = (ushort) to.Port;
					pTo->SocketAddressFamily = (ushort) to.AddressFamily;
					pTo->Port = BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(port) : port;
					if (!to.Address.TryWriteBytes(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref pTo->InAddr, 1)), out _))
						throw new NotImplementedException("Can't write address.");

					pTo->InScopeId = checked((uint) to.Address.ScopeId);
					int result;
					fixed (byte* pBuf = buffer)
					{
						result = _SendTo(socket.Handle.ToInt32(), pBuf, buffer.Length, flags, pTo, sizeof(SockAddrIn6));
					}

					if (result == -1)
					{
						error = GetError(socket);
						if (error == 0)
							error = Marshal.GetLastWin32Error();
					}

					if (result == -1)
						throw new SocketException(error);

					return result;
				}
			}
		}

		public static unsafe int ReceiveFrom(this Socket socket, Span<byte> buffer, int flags, out IPEndPoint? from)
		{
			var error = 0;
			int result;
			var fromSockAddrSize = SockAddrStorageSize;
			var pFrom = stackalloc byte[SockAddrStorageSize];

			fixed (byte* pBuf = buffer)
			{
				result = _RecvFrom(socket.Handle.ToInt32(), pBuf, buffer.Length, flags, pFrom, &fromSockAddrSize);
			}

			if (result == -1)
			{
				error = GetError(socket);
				if (error == 0)
				{
					error = Marshal.GetLastWin32Error();
				}
			}

			ReadIp(out from, (SockAddr*) pFrom);

			if (result == -1)
			{
				throw new SocketException(error);
			}

			return result;
		}

		private static unsafe void ReadIp(out IPEndPoint? from, SockAddr* pFrom)
		{
			switch (pFrom->SocketAddressFamily)
			{
				default:
					throw new NotSupportedException(((AddressFamily) pFrom->SocketAddressFamily).ToString());
				case (ushort) AddressFamily.Unspecified:
					from = null;
					break;
				case (ushort) AddressFamily.InterNetwork:
					ReadIPv4(out from, (SockAddrIn*) pFrom);
					break;
				case (ushort) AddressFamily.InterNetworkV6:
					ReadIPv6(out from, (SockAddrIn6*) pFrom);
					break;
			}
		}

		private static unsafe void ReadIPv4(out IPEndPoint from, SockAddrIn* addr)
		{
			var port = BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(addr->Port) : addr->Port;
			var ip = BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(addr->InAddr) : addr->InAddr;

			from = new IPEndPoint(
				ip,
				port
			);
		}

		private static unsafe void ReadIPv6(out IPEndPoint from, SockAddrIn6* addr)
		{
			var port = BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(addr->Port) : addr->Port;

			var ip = new IPAddress(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref addr->InAddr, 1)), addr->InScopeId);

			from = new IPEndPoint(ip, port);
		}

	}

}
