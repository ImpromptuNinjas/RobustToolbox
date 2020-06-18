using System.IO;
using Lidgren.Network;
using Robust.Shared.Interfaces.Network;
using Robust.Shared.Interfaces.Serialization;
using Robust.Shared.IoC;
using Robust.Shared.ViewVariables;

#nullable disable

namespace Robust.Shared.Network.Messages
{
    /// <summary>
    ///     Sent client to server to request data from the server.
    /// </summary>
    public class MsgViewVariablesReqData : NetMessage
    {
        #region REQUIRED

        public const MsgGroups GROUP = MsgGroups.Command;
        public const string NAME = nameof(MsgViewVariablesReqData);

        public MsgViewVariablesReqData(INetChannel channel) : base(NAME, GROUP)
        {
        }

        #endregion

        /// <summary>
        ///     The request ID that will be sent in <see cref="MsgViewVariablesRemoteData"/> to
        ///     identify this request among multiple potentially concurrent ones.
        /// </summary>
        public uint RequestId { get; set; }

        /// <summary>
        ///     The session ID for the session to read the data from.
        /// </summary>
        public uint SessionId { get; set; }

        /// <summary>
        ///     A metadata object that can be used by the server to know what data is being requested.
        /// </summary>
        public ViewVariablesRequest RequestMeta { get; set; }

        public override unsafe void ReadFromBuffer(NetIncomingMessage buffer)
        {
            RequestId = buffer.ReadUInt32();
            SessionId = buffer.ReadUInt32();
            var serializer = IoCManager.Resolve<IRobustSerializer>();
            var length = buffer.ReadInt32();
            var bytes = buffer.ReadBytes(stackalloc byte[length]);
            fixed(byte * p = bytes)
            {
                using var stream = new UnmanagedMemoryStream(p,bytes.Length, bytes.Length, FileAccess.Read);
                RequestMeta = serializer.Deserialize<ViewVariablesRequest>(stream);
            }
        }

        public override void WriteToBuffer(NetOutgoingMessage buffer)
        {
            buffer.Write(RequestId);
            buffer.Write(SessionId);
            var serializer = IoCManager.Resolve<IRobustSerializer>();
            using (var stream = new MemoryStream())
            {
                serializer.Serialize(stream, RequestMeta);
                buffer.Write((int)stream.Length);
                buffer.Write(stream.ToArray());
            }
        }
    }
}

