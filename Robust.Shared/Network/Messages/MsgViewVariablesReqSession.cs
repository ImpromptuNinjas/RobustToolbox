using System;
using System.IO;
using Lidgren.Network;
using Robust.Shared.GameObjects;
using Robust.Shared.Interfaces.Network;
using Robust.Shared.Interfaces.Serialization;
using Robust.Shared.IoC;
using Robust.Shared.Serialization;
using Robust.Shared.Utility;
using Robust.Shared.ViewVariables;

namespace Robust.Shared.Network.Messages
{
    /// <summary>
    ///     Sent from client to server to request to open a session.
    /// </summary>
    public class MsgViewVariablesReqSession : NetMessage
    {
        #region REQUIRED

        public const MsgGroups GROUP = MsgGroups.Command;
        public const string NAME = nameof(MsgViewVariablesReqSession);
        public MsgViewVariablesReqSession(INetChannel channel) : base(NAME, GROUP) { }

        #endregion

        /// <summary>
        ///     An ID the client assigns so it knows which request was accepted/denied through
        ///     <see cref="MsgViewVariablesOpenSession"/> and <see cref="MsgViewVariablesCloseSession"/>.
        /// </summary>
        public uint RequestId { get; set; }

        /// <summary>
        ///     A selector that can be used to describe a server object.
        ///     This isn't BYOND, we don't have consistent \ref references.
        /// </summary>
        public ViewVariablesObjectSelector Selector { get; set; }

        public override void ReadFromBuffer(NetIncomingMessage buffer)
        {
            RequestId = buffer.ReadUInt32();
            var length = buffer.ReadVariableInt32();
            var serializer = IoCManager.Resolve<IRobustSerializer>();
            var bytes = buffer.ReadBytes(length);
            using (var stream = new MemoryStream(bytes))
            {
                Selector = serializer.Deserialize<ViewVariablesObjectSelector>(stream);
            }
        }

        public override void WriteToBuffer(NetOutgoingMessage buffer)
        {
            buffer.Write(RequestId);
            var serializer = IoCManager.Resolve<IRobustSerializer>();
            using (var stream = new MemoryStream())
            {
                serializer.Serialize(stream, Selector);
                buffer.WriteVariableInt32((int)stream.Length);
                buffer.Write(stream.ToArray());
            }
        }
    }
}
